using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChatTranslate.Services;

/// <summary>一条对话消息。</summary>
public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

/// <summary>Ollama 返回的性能指标（时间为纳秒）。</summary>
public sealed record ChatMetrics(
    int PromptEvalCount,
    int EvalCount,
    long EvalDurationNs,
    long TotalDurationNs,
    long LoadDurationNs,
    int? NumCtx)
{
    /// <summary>输出速度（tok/s）。官方算法：eval_count / eval_duration × 10^9。</summary>
    public double TokensPerSecond =>
        EvalDurationNs > 0 ? EvalCount / (EvalDurationNs / 1_000_000_000.0) : 0;

    /// <summary>上下文占用量（已用 prompt token）。</summary>
    public double ContextRatio =>
        NumCtx is > 0 ? (double)PromptEvalCount / NumCtx.Value : 0;
}

/// <summary>一次翻译的完整结果。</summary>
public sealed record ChatReply(string Text, ChatMetrics Metrics);

/// <summary>
/// Ollama 客户端。
///
/// <para>只走 <c>/api/chat</c>。Hy-MT2 的控制 token 由 GGUF 内嵌的官方 Jinja 模板处理，
/// 这里只做两件事：把译文取出来、把偶发泄漏的控制 token 残片清掉。</para>
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>控制 token 残片清洗。模板正确时极少触发，但必须兜底。</summary>
    private static readonly Regex ControlTokenPattern = new(
        @"<\|(?:startoftext|endoftext|eos|extra_\d+)\|>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _numCtx;
    private readonly bool _ownsHttpClient;

    public OllamaClient(string host, string model, int numCtx = 8192, HttpClient? httpClient = null)
    {
        var normalized = host.TrimEnd('/');
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttpClient = httpClient is null;
        _http.BaseAddress ??= new Uri(normalized + "/");
        _model = model;
        _numCtx = numCtx;
    }

    public string Model => _model;

    /// <summary>探测服务是否可用，返回已安装的模型名；失败返回 null。</summary>
    public async Task<IReadOnlyList<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/tags", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("models", out var models))
            {
                return [];
            }

            return models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>非流式翻译。</summary>
    public async Task<ChatReply> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        double? temperature = null,
        CancellationToken ct = default)
    {
        var payload = BuildPayload(messages, stream: false, temperature);
        using var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync("api/chat", content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var root = document.RootElement;
        var text = root.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var contentElement)
                ? contentElement.GetString() ?? string.Empty
                : string.Empty;

        return new ChatReply(CleanOutput(text), ReadMetrics(root));
    }

    /// <summary>流式翻译，逐块产出译文增量。</summary>
    /// <param name="onComplete">
    /// 流结束（收到 <c>done=true</c> 的收尾块）时回调一次，携带本次推理的完整指标。
    /// 有了它就不必为了拿指标再跑一遍非流式推理，指标与耗时也就对得上了。
    /// </param>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<ChatMetrics>? onComplete = null,
        double? temperature = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildPayload(messages, stream: true, temperature);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        ChatMetrics? finalMetrics = null;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? piece = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var contentElement))
                {
                    piece = contentElement.GetString();
                }

                if (root.TryGetProperty("done", out var done)
                    && done.ValueKind == JsonValueKind.True)
                {
                    finalMetrics = ReadMetrics(root);
                }
            }
            catch (JsonException)
            {
                // 单行解析失败不应中断整条流
                continue;
            }

            if (!string.IsNullOrEmpty(piece))
            {
                yield return piece;
            }
        }

        if (onComplete is not null && finalMetrics is not null)
        {
            onComplete(finalMetrics);
        }
    }

    /// <summary>从响应块中提取性能指标。上下文容量来自配置，不来自响应。</summary>
    private ChatMetrics ReadMetrics(JsonElement root)
    {
        return new ChatMetrics(
            PromptEvalCount: GetInt(root, "prompt_eval_count"),
            EvalCount: GetInt(root, "eval_count"),
            EvalDurationNs: GetLong(root, "eval_duration"),
            TotalDurationNs: GetLong(root, "total_duration"),
            LoadDurationNs: GetLong(root, "load_duration"),
            NumCtx: _numCtx);

        static int GetInt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

        static long GetLong(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.TryGetInt64(out var i) ? i : 0;
    }

    private Dictionary<string, object?> BuildPayload(
        IReadOnlyList<ChatMessage> messages, bool stream, double? temperature)
    {
        var options = new Dictionary<string, object?>
        {
            ["temperature"] = temperature ?? 0.7,
            ["top_p"] = 0.6,
            ["top_k"] = 20,
            ["repeat_penalty"] = 1.05,
            ["num_ctx"] = _numCtx,
        };

        return new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = stream,
            ["options"] = options,
        };
    }

    /// <summary>清理控制 token 残片与首尾空白。</summary>
    public static string CleanOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return ControlTokenPattern.Replace(text, string.Empty).Trim();
    }

    /// <summary>构造官方推荐的翻译提示词。</summary>
    public static string BuildTranslatePrompt(string text, Language target) =>
        $"将以下文本翻译为 {target.ChineseName}，注意只需要输出翻译后的结果，不要额外解释：\n\n{text}";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
