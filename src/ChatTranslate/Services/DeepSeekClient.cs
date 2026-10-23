using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatTranslate.Services;

/// <summary>
/// DeepSeek 官方 API（OpenAI 兼容协议）客户端。
/// </summary>
/// <remarks>
/// <para>与本地 Ollama 的差异集中在三处，不能照搬本地参数：</para>
/// <list type="bullet">
/// <item>认证用 <c>Authorization: Bearer</c>；</item>
/// <item>请求体是 <c>max_tokens</c>，没有 <c>num_ctx</c> / <c>keep_alive</c>；</item>
/// <item>流式响应是 SSE（<c>data: {...}</c> 与 <c>data: [DONE]</c>），不是逐行 NDJSON。</item>
/// </list>
///
/// <para>错误正文里可能带服务端提示，但绝不会把 API Key 写进异常或日志。</para>
/// </remarks>
public sealed class DeepSeekClient : ITranslationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly int _contextTokens;
    private readonly bool _ownsHttpClient;

    public DeepSeekClient(
        string endpoint,
        string model,
        string? apiKey,
        int contextTokens,
        HttpClient? httpClient = null)
    {
        _endpoint = NormalizeEndpoint(endpoint);
        _model = string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model.Trim();
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _contextTokens = contextTokens > 0 ? contextTokens : 65536;

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public string ProviderName => "DeepSeek";

    public string Model => _model;

    public int ContextWindowTokens => _contextTokens;

    public bool SupportsModelManagement => false;

    public int MaxOutputTokens => TranslationLimits.MaxOutputTokens;

    public string? LastError { get; private set; }

    public async Task<IReadOnlyList<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        if (_apiKey.Length == 0)
        {
            LastError = "尚未配置 DeepSeek API Key";
            return null;
        }

        try
        {
            LastError = null;
            using var request = CreateRequest(HttpMethod.Get, "models");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            LastError = "请求已取消或超时";
            return null;
        }
        catch (HttpRequestException ex)
        {
            LastError = ex.Message;
            return null;
        }
        catch (JsonException ex)
        {
            LastError = $"响应格式异常：{ex.Message}";
            return null;
        }
    }

    public async Task<ChatReply> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onProgress = null,
        double? temperature = null,
        CancellationToken ct = default)
    {
        if (_apiKey.Length == 0)
        {
            throw new InvalidOperationException(
                "未配置 DeepSeek API Key。请到设置 → 服务 → DeepSeek 云端 API 填写。");
        }

        var text = new StringBuilder();
        var metrics = new MetricsHolder();

        await foreach (var piece in StreamChunksAsync(messages, temperature, metrics, ct))
        {
            text.Append(piece);
            onProgress?.Invoke(OllamaClient.CleanOutput(text.ToString()));
        }

        var translation = OllamaClient.CleanOutput(text.ToString());
        if (translation.Length == 0)
        {
            throw new InvalidOperationException("DeepSeek 返回了空译文");
        }

        return new ChatReply(translation, metrics.Value ?? ChatMetrics.Empty);
    }

    /// <summary>云端模型没有显存驻留概念，界面不会显示加载/卸载。</summary>
    public Task<ModelResidencyState> GetResidencyAsync(CancellationToken ct = default) =>
        Task.FromResult(ModelResidencyState.Unknown);

    public Task LoadModelAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("DeepSeek 云端模型不需要加载到显存");

    public Task UnloadModelAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("DeepSeek 云端模型不需要卸载显存");

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// 单次流式请求的指标容器。
    /// </summary>
    /// <remarks>
    /// 不能用客户端字段保存：划词浮窗与主窗口可能同时翻译，共享字段会让后完成的请求
    /// 覆盖前一个，速度与截断提示随之串台。
    /// </remarks>
    private sealed class MetricsHolder
    {
        public ChatMetrics? Value { get; set; }
    }

    private async IAsyncEnumerable<string> StreamChunksAsync(
        IReadOnlyList<ChatMessage> messages,
        double? temperature,
        MetricsHolder metrics,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true },
            ["temperature"] = temperature ?? 0.7,
            ["max_tokens"] = TranslationLimits.MaxOutputTokens,
        };

        using var request = CreateRequest(HttpMethod.Post, "chat/completions");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        var started = Stopwatch.StartNew();
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var promptTokens = 0;
        var completionTokens = 0;
        string? finishReason = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                break;
            }

            string? piece = null;
            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;

                if (root.TryGetProperty("usage", out var usage)
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    promptTokens = ReadInt(usage, "prompt_tokens", promptTokens);
                    completionTokens = ReadInt(usage, "completion_tokens", completionTokens);
                }

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    piece = content.GetString();
                }

                if (choice.TryGetProperty("finish_reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String)
                {
                    finishReason = reason.GetString();
                }
            }
            catch (JsonException)
            {
                // 单个 SSE 块解析失败不中断整条流；与 Ollama 的 NDJSON 处理保持一致。
                continue;
            }

            if (!string.IsNullOrEmpty(piece))
            {
                yield return piece;
            }
        }

        started.Stop();
        metrics.Value = new ChatMetrics(
            promptTokens,
            completionTokens,
            started.Elapsed.Ticks * 100,
            started.Elapsed.Ticks * 100,
            0,
            _contextTokens,
            finishReason);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_endpoint}/{path}");
        if (_apiKey.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var value = string.IsNullOrWhiteSpace(endpoint)
            ? "https://api.deepseek.com"
            : endpoint.Trim();
        value = value.TrimEnd('/');

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("DeepSeek 地址格式不正确，应形如 https://api.deepseek.com");
        }

        return value;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var message = ExtractError(body);

        // 服务端或中间代理偶尔会把请求头原样回显到错误正文里；这里兜底抹掉 Key，
        // 保证异常信息进状态栏 / 日志时不会带出凭据。
        if (_apiKey.Length > 0)
        {
            message = message.Replace(_apiKey, "***", StringComparison.Ordinal);
        }

        throw new HttpRequestException(
            $"DeepSeek 返回 {(int)response.StatusCode} {response.ReasonPhrase}：{message}");
    }

    /// <summary>优先取 OpenAI 兼容错误体里的 <c>error.message</c>，取不到再截断原文。</summary>
    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(空响应体)";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误页：沿用截断后的正文
        }

        return body.Length <= 800 ? body : body[..800] + "…";
    }

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
}
