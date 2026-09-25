using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ChatTranslate.Data;

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
    int? NumCtx,
    string? DoneReason = null)
{
    /// <summary>
    /// 没有可用指标时的占位值（例如流里没有 <c>done=true</c> 的收尾块）。
    /// 各项为 0，因此 <see cref="TokensPerSecond"/> / <see cref="ContextRatio"/> 自然为 0，
    /// 调用方无需再做 null 判断。
    /// </summary>
    public static ChatMetrics Empty { get; } = new(0, 0, 0, 0, 0, null, null);

    /// <summary>输出速度（tok/s）。官方算法：eval_count / eval_duration × 10^9。</summary>
    public double TokensPerSecond =>
        EvalDurationNs > 0 ? EvalCount / (EvalDurationNs / 1_000_000_000.0) : 0;

    /// <summary>上下文占用量（已用 prompt token）。</summary>
    public double ContextRatio =>
        NumCtx is > 0 ? (double)PromptEvalCount / NumCtx.Value : 0;

    /// <summary>
    /// 本次生成是否因为达到输出上限（<c>num_predict</c>）而停止。
    /// </summary>
    /// <remarks>
    /// Ollama 在这种情况下的 <c>done_reason</c> 是 <c>length</c>（正常生成完是 <c>stop</c>）。
    /// 必须把它显式告知用户：否则拿到的是一段**被截断但看起来完整**的译文，
    /// 用户只会以为模型翻得不好，而不知道再翻长一点就能拿到全部。
    /// </remarks>
    public bool Truncated => DoneReason is "length";
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

    /// <summary>
    /// 单次生成的输出 token 上限（官方 README 的 <c>max_tokens</c>）。
    /// </summary>
    /// <remarks>
    /// <b>这个值不能省。</b>不设上限时 Ollama 用 <c>num_predict = -1</c>（不限制），
    /// 而 llama.cpp 在上下文写满后会做 context shift 继续生成——
    /// 也就是说**整个链路没有任何终止条件**。模型一旦陷入复读（重复输入、长文本都可能诱发），
    /// 就会一直产出：实测连续生成 20 万字符仍未结束，界面表现为永远「生成中」，
    /// 且译文全是重复内容。
    /// </remarks>
    public const int MaxOutputTokens = 4096;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _numCtx;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// 请求体里的 <c>keep_alive</c> 取值。
    /// </summary>
    /// <remarks>
    /// <para>必须以数字还是字符串发出去，取决于形态：Ollama 用 Go 的
    /// <c>time.ParseDuration</c> 解析字符串，<c>"600"</c> 与 <c>"-1"</c> 都不是合法
    /// duration（只有 <c>"0"</c> 是特例），所以秒数和"常驻不卸载"（负数）
    /// 只能发<b>数字</b>，时长串才发字符串。</para>
    ///
    /// <para>四种形态均已实测（Ollama 0.34.4）：<c>"30m"</c> → 30 分钟后过期；
    /// <c>60</c> → 60 秒；<c>-1</c> → 常驻（<c>expires_at</c> 到 2319 年）；
    /// <c>0</c> → 用完立即卸载。</para>
    /// </remarks>
    private readonly object? _keepAlive;

    public OllamaClient(
        string host, string model, int numCtx, string? keepAlive, HttpClient? httpClient = null)
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

        // 解析走 AppConfig.TryParseKeepAlive —— 与配置校验同一套判断（单一真源）。
        // 曾经两边各写一套，于是「校验放行、发送时退化成字符串」，出现 400 却看不出原因。
        //
        // 空值或无法识别 → 不带该字段，由 Ollama 用它自己的默认值（5 分钟）。
        // 配置层经 Normalize() 后不会是空值，这条分支是构造器的契约（直接构造/测试用）。
        _keepAlive = AppConfig.TryParseKeepAlive(keepAlive, out var parsed)
            ? parsed.Payload
            : null;
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

    /// <summary>
    /// 一次性取回完整译文（内部累积流式分块）。
    /// </summary>
    /// <param name="messages">对话上下文。</param>
    /// <param name="onProgress">每收到一块就把<b>当前完整译文</b>回调一次，用于流式上屏。</param>
    /// <param name="temperature">采样温度；null 用服务端默认。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// <see cref="StreamTextAsync"/> 每次 yield 的是<b>累积全文</b>而非增量，因此这里
    /// 保留最后一块即为完整译文；不需要再维护一个 StringBuilder 副本。
    /// <para>存在的意义是消除调用方各自手写的累积循环——原先
    /// <c>TranslationService.TranslateAsync</c> / <c>TranslateOnceAsync</c> /
    /// <c>BatchTranslator</c> 三处各写一遍，清洗规则或指标回调时机一旦调整就会漏改，
    /// 造成同一份数据在不同路径上行为不一致。</para>
    /// <para><b>⚠ 这里的 <c>await foreach</c> 绝不能加 <c>.ConfigureAwait(false)</c>。</b>
    /// <paramref name="onProgress"/> 的用途是<b>流式上屏</b>，调用方传进来的就是
    /// 直接写 WPF 控件的回调。加了 <c>ConfigureAwait(false)</c> 后续体会跑到线程池，
    /// 回调就在非 UI 线程上执行，抛
    /// <c>InvalidOperationException：调用线程无法访问此对象，因为另一个线程拥有该对象</c>，
    /// 界面显示"翻译失败"。契约就是：<b>回调在调用方的同步上下文上被调用</b>。</para>
    /// </remarks>
    public async Task<ChatReply> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onProgress = null,
        double? temperature = null,
        CancellationToken ct = default)
    {
        var latest = string.Empty;
        ChatMetrics? metrics = null;

        await foreach (var piece in StreamTextAsync(messages, m => metrics = m, temperature, ct))
        {
            latest = piece;
            onProgress?.Invoke(piece);
        }

        return new ChatReply(latest.Trim(), metrics ?? ChatMetrics.Empty);
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
            NumCtx: _numCtx,
            DoneReason: GetString(root, "done_reason"));

        static int GetInt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

        static long GetLong(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.TryGetInt64(out var i) ? i : 0;

        static string? GetString(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
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

            // 输出上限。**没有它就没有任何终止条件**：llama.cpp 在上下文写满后会
            // 做 context shift（丢弃最老的内容）继续生成，模型一旦陷入复读就会
            // 无限产出——实测连续生成 20 万字符仍未结束，界面上表现为一直"生成中"，
            // 整段译文全是重复内容。
            //
            // 4096 取自官方 README 的 max_tokens。它同时是"译文被截断"的边界，
            // 但相比无限生成，宁可截断。
            ["num_predict"] = MaxOutputTokens,
        };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = stream,
            ["options"] = options,
        };

        // 带上 keep_alive，否则每次都按 Ollama 自己的默认值（5 分钟）重新计时，
        // 隔一会儿不翻译模型就被卸载，下一次请求先得等它载回显存。
        if (_keepAlive is not null)
        {
            payload["keep_alive"] = _keepAlive;
        }

        return payload;
    }

    /// <summary>
    /// 查询模型此刻是否已驻留显存（<c>GET /api/ps</c>）。
    /// </summary>
    /// <returns><c>true</c> / <c>false</c> 为探测结果；<c>null</c> 表示查询失败，无法判定。</returns>
    /// <remarks>
    /// <para>只用于界面提示：模型不在显存里时，本次翻译要先等 Ollama 把它载入
    /// （实测 <c>load_duration</c> 7.05 s；已驻留时只有 2.9–3.8 ms），
    /// 这段等待在界面上没有任何反馈，用户会以为程序卡死。</para>
    ///
    /// <para>失败时返回 <c>null</c> 而不是 <c>false</c>：调用方据此保持默认文案，
    /// 宁可少提示，也不要凭空报「模型加载中」。</para>
    ///
    /// <para><b>任何情况下都不抛异常</b>——包括响应结构与预期不符
    /// （<c>models</c> 不是数组、条目不是对象、<c>name</c> 不是字符串）：
    /// 这类畸形响应一律当作「查不到」。调用方是 fire-and-forget 的界面提示，
    /// 让异常从这里穿透出去只会变成无人观察的异常。</para>
    /// </remarks>
    public async Task<bool?> IsModelResidentAsync(CancellationToken ct = default)
    {
        // 实测 /api/ps 的条目同时带 name 与 model 两个同值字段，认任一个即可。
        // 必须先判 ValueKind：json 值不是字符串时 GetString() 会抛 InvalidOperationException，
        // 与「查询失败返回 null、不抛异常」的契约不符。
        bool Matches(JsonElement entry, string field) =>
            entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } name
            && name.Equals(_model, StringComparison.OrdinalIgnoreCase);

        try
        {
            using var response = await _http.GetAsync(Api("api/ps"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            // models 不是数组（缺字段 / 被换成 null / 换成对象）都算「查不到」：
            // EnumerateArray 在这几种形态下同样会抛
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in models.EnumerateArray())
            {
                if (Matches(entry, "name") || Matches(entry, "model"))
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    /// <summary>
    /// 构造翻译提示词。
    /// </summary>
    /// <param name="text">待翻译文本。</param>
    /// <param name="target">目标语言。</param>
    /// <param name="source">
    /// 源语言；传 null 表示自动检测，此时使用官方默认模板（只带目标语言）。
    /// 实测：显式带上源语言时译文质量不降反升（一个英文长句译回中文时，
    /// 「braised pork」在带源语言的版本里被正确译为「红烧猪肉」而非「炖猪肉」）。
    /// </param>
    public static string BuildTranslatePrompt(string text, Language target, Language? source = null) =>
        source is null
            ? $"将以下文本翻译为 {target.ChineseName}，注意只需要输出翻译后的结果，不要额外解释：\n\n{text}"
            : $"将以下文本从 {source.ChineseName} 翻译为 {target.ChineseName}，注意只需要输出翻译后的结果，不要额外解释：\n\n{text}";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
