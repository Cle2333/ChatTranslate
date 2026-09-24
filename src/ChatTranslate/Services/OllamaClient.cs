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
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // 只改写自建客户端的 BaseAddress。
        // 注入进来的客户端属于调用方：改写它会静默覆盖调用方的配置，
        // 若该实例已发起过请求再赋 BaseAddress 还会抛 InvalidOperationException。
        if (_ownsHttpClient)
        {
            _http.BaseAddress = new Uri(normalized + "/");
        }

        _baseUrl = normalized + "/";
        _model = model;
        _numCtx = numCtx;
    }

    private readonly string _baseUrl;

    public string Model => _model;

    /// <summary>
    /// 探测服务是否可用，返回已安装的模型名；失败返回 null。
    /// 返回 null 只表示"没拿到列表"，具体原因（未启动 / 超时 / 响应格式异常）记入 <see cref="LastError"/>。
    /// </summary>
    public string? LastError { get; private set; }

    public async Task<IReadOnlyList<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await _http.GetAsync(Api("api/tags"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}：{await SafeReadBodyAsync(response, ct)}";
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
        catch (OperationCanceledException)
        {
            LastError = "请求已取消或超时";
            return null;
        }
        catch (HttpRequestException ex)
        {
            LastError = $"无法连接 Ollama：{ex.Message}";
            return null;
        }
        catch (JsonException ex)
        {
            LastError = $"响应格式异常：{ex.Message}";
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

        using var response = await _http.PostAsync(Api("api/chat"), content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var root = document.RootElement;
        var text = root.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var contentElement)
                ? contentElement.GetString() ?? string.Empty
                : string.Empty;

        return new ChatReply(CleanOutput(text), ReadMetrics(root));
    }

    /// <summary>
    /// 流式翻译，逐块产出译文增量。
    /// </summary>
    /// <param name="onComplete">
    /// 流结束（收到 <c>done=true</c> 的收尾块）时回调一次，携带本次推理的完整指标。
    /// 有了它就不必为了拿指标再跑一遍非流式推理，指标与耗时也就对得上了。
    /// </param>
    /// <remarks>
    /// ⚠️ <b>产出的增量是原始文本，未做控制 token 清洗。</b>
    /// 原因是残片可能跨分块切分（例如 <c>&lt;|endo</c> 与 <c>ftext|&gt;</c> 分在两块），
    /// 对单块调用清洗函数无法识别。
    /// <b>调用方必须把增量累积后整体过一遍 <see cref="CleanOutput"/></b>，
    /// 或改用 <see cref="StreamTextAsync"/>（已内置累积与清洗）。
    /// </remarks>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<ChatMetrics>? onComplete = null,
        double? temperature = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildPayload(messages, stream: true, temperature);
        using var request = new HttpRequestMessage(HttpMethod.Post, Api("api/chat"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        ChatMetrics? finalMetrics = null;

        // 用 ReadLineAsync 返回 null 作为结束条件，而不是同步属性的 EndOfStream：
        // 后者在数据未就绪时会阻塞当前线程，与异步读取混用会白占线程池线程，
        // 并降低取消（ct）的响应及时性。
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

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

    /// <summary>
    /// 流式翻译的**安全变体**：内部累积全部分块并在结束时统一清洗控制 token，
    /// 再逐块把「已清洗结果」交出来。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="StreamAsync"/> 的区别：不会把未清洗的原始增量暴露给调用方，
    /// 因此调用方无需记住清洗约定。<see cref="StreamAsync"/> 保留给需要自己控制
    /// 增量渲染节奏的场景（自行累积后调用 <see cref="CleanOutput"/>）。
    /// </remarks>
    public async IAsyncEnumerable<string> StreamTextAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<ChatMetrics>? onComplete = null,
        double? temperature = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var raw = new StringBuilder();
        await foreach (var piece in StreamAsync(messages, onComplete, temperature, ct)
                           .ConfigureAwait(false))
        {
            raw.Append(piece);
            yield return CleanOutput(raw.ToString());
        }
    }

    /// <summary>拼出绝对 URL。注入的 HttpClient 可能没有 BaseAddress，因此一律用绝对地址。</summary>
    private string Api(string path) => _baseUrl + path;

    /// <summary>
    /// 检查响应状态，失败时把 Ollama 的错误正文带进异常。
    /// <c>EnsureSuccessStatusCode()</c> 会丢掉正文，只留一个 404/500，无法区分
    /// 「模型未安装」「参数不合法」等情况。
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Ollama 返回 {(int)response.StatusCode} {response.ReasonPhrase}：{body}");
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? "(空响应体)" : body;
        }
        catch
        {
            return "(读取响应体失败)";
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
