using System.IO;
using System.Text;
using ChatTranslate.Core;
using ChatTranslate.Data;

namespace ChatTranslate.Services;

/// <summary>翻译的流式进度回调。</summary>
public sealed class TranslationCallbacks
{
    /// <summary>收到新的累积文本（已清洗控制 token）。</summary>
    public Action<string>? OnProgress { get; init; }

    /// <summary>
    /// 分段翻译的进度：<c>(第几段, 共几段)</c>，从 1 开始。
    /// </summary>
    /// <remarks>
    /// 只在真的分了多段时回调。长文翻译是分钟级操作，
    /// 不给"还剩几段"的反馈，用户无法判断是在正常推进还是卡住了。
    /// </remarks>
    public Action<int, int>? OnChunkProgress { get; init; }

    /// <summary>完成时携带性能指标。</summary>
    public Action<ChatMetrics>? OnCompleted { get; init; }
}

/// <summary>本次翻译实际使用的语言对。</summary>
/// <param name="Source">用户选定的源语言；null 表示自动检测。</param>
/// <param name="Target">实际使用的目标语言（可能因冲突被交换过）。</param>
/// <param name="Swapped">是否因为源与目标冲突而自动交换了目标语言。</param>
/// <param name="ActualSource">实际生效的源语言（自动检测时即检测结果），用于界面提示措辞。</param>
public sealed record LanguagePair(
    Language? Source,
    Language Target,
    bool Swapped,
    Language? ActualSource = null);

/// <summary>
/// 翻译编排：把「会话存储 + 对话上下文 + Ollama 调用」串起来。
///
/// <para>对话上下文是<b>多轮累积</b>的：除新开对话外，同一会话内的历史会一并发送。</para>
/// </summary>
public sealed class TranslationService : IDisposable
{
    /// <summary>
    /// 上下文预留比例：历史消息最多占用 num_ctx 的这个比例，其余留给本轮输入与生成。
    /// </summary>
    private const double HistoryBudgetRatio = 0.5;

    /// <summary>粗估 1 个 token 约等于多少字符（中文场景偏保守）。</summary>
    private const double CharsPerToken = 1.5;

    /// <summary>
    /// 译文 token 相对原文 token 的膨胀系数（经验上界）。
    /// </summary>
    /// <remarks>
    /// 中→英实测约 1.0~1.3；取 1.4 是为了让"输出撞上限被截断"这件事留有富余。
    /// 反方向（英→中）译文更短，这个系数偏保守，代价只是分段多一点。
    /// </remarks>
    private const double ExpansionRatio = 1.4;

    /// <summary>预算的安全余量：实测值本身有波动，留三成不亏。</summary>
    private const double BudgetSafety = 0.7;

    /// <summary>单段最小字符数：再小也切不出有意义的段落，反而会多发请求。</summary>
    private const int MinCharsPerRequest = 400;

    /// <summary>单段字符数上限（防止 num_ctx 被设得极大时算出荒谬的值）。</summary>
    private const int MaxCharsPerRequestLimit = 6000;

    private readonly ChatStore _store;
    private readonly ConfigStore _config;
    private readonly object _clientGate = new();

    private OllamaClient? _cachedClient;
    private string? _cachedFingerprint;

    public TranslationService(ChatStore store, ConfigStore config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>
    /// 单次请求最多送进模型的原文长度（字符）。超过就分段翻译。
    /// </summary>
    /// <remarks>
    /// <para>两个限制取小，再留安全余量：</para>
    /// <list type="number">
    /// <item><b>prompt 预算</b>：prompt 与生成共用 <c>num_ctx</c>，生成至少要留出
    /// <c>num_predict</c> 的位置，所以 prompt 只能用 <c>num_ctx - num_predict</c> 个 token。</item>
    /// <item><b>输出预算</b>：译文与原文同量级（中→英约 1.0~1.3 倍 token），
    /// 撞上 <c>num_predict</c> 就是<b>静默截断</b>——用户拿到的是看起来完整、其实少一截的译文。</item>
    /// </list>
    ///
    /// <para><b>本机实测</b>（num_ctx 8192 / num_predict 4096，中→英）：
    /// 2000 字符 → prompt 1158 tok、输出 1189 tok、正常；
    /// 3015 字符 → prompt 2044 tok、输出 2605 tok、正常（61.9 s）；
    /// 4010 字符 → 输出顶到 4096 被截断，尾部丢失；
    /// 5000 字符 → prompt 3400 tok，生成到一半服务端返回 500。</para>
    ///
    /// <para>默认参数下据此得到约 3000 字符/段，落在实测的安全区内。</para>
    /// </remarks>
    private int MaxCharsPerRequest
    {
        get
        {
            var predict = OllamaClient.MaxOutputTokens;

            var promptChars = (_config.Current.NumCtx - predict) * CharsPerToken;
            var outputChars = predict / ExpansionRatio * CharsPerToken;

            var chars = Math.Min(promptChars, outputChars) * BudgetSafety;

            return (int)Math.Clamp(chars, MinCharsPerRequest, MaxCharsPerRequestLimit);
        }
    }

    /// <summary>配置里选定的输入语言；null 表示自动检测。</summary>
    public Language? ConfiguredSource =>
        _config.Current.SourceLanguage is "auto" or ""
            ? null
            : Languages.ByCode(_config.Current.SourceLanguage);

    /// <summary>配置里选定的目标语言。</summary>
    public Language ConfiguredTarget =>
        Languages.ByCode(_config.Current.TargetLanguage) ?? Languages.Default;

    /// <summary>
    /// 解析本次翻译实际使用的语言对。
    ///
    /// <para>规则：只要源语言（显式选定或自动检测所得）与目标语言相同，就换到另一种目标语言——
    /// 否则会出现「把中文翻成中文」的空转。交换行为通过 <see cref="LanguagePair.Swapped"/>
    /// 上报给界面，由界面提示用户，不做静默处理。</para>
    /// </summary>
    public LanguagePair ResolveLanguages(string text)
    {
        var configuredTarget = ConfiguredTarget;
        var source = ConfiguredSource;

        // 自动检测时给出检测结果，供界面提示使用
        var detectedCode = source?.Code ?? Languages.Detect(text);
        var detectedLanguage = detectedCode is null ? null : Languages.ByCode(detectedCode);

        if (detectedCode is null || detectedCode != configuredTarget.Code)
        {
            return new LanguagePair(source, configuredTarget, Swapped: false, detectedLanguage);
        }

        var swapped = FallbackTarget(configuredTarget);
        return new LanguagePair(
            source,
            swapped,
            Swapped: swapped.Code != configuredTarget.Code,
            detectedLanguage);
    }

    /// <summary>
    /// 选一个与给定语言不同的目标语言。
    ///
    /// <para>必须覆盖<b>全部</b>语种，不能只管中英：若只在 zh/en 之间切换，
    /// 用户显式选择「日语 → 日语」时会原样返回日语，提示词变成
    /// 「将以下文本从 日语 翻译为 日语」，与"避免空转"的承诺不符。
    /// 首选默认语言（中文），中文冲突则退到英语。</para>
    /// </summary>
    private static Language FallbackTarget(Language current)
    {
        var preferred = Languages.Default;
        if (preferred.Code != current.Code)
        {
            return preferred;
        }

        return Languages.ByCode("en") ?? preferred;
    }

    /// <summary>
    /// 翻译一段文本，并把「原文 + 译文」写入会话。
    /// </summary>
    /// <param name="threadId">目标会话。</param>
    /// <param name="original">待翻译文本。</param>
    /// <param name="imagePath">截图 OCR 场景下的图片路径；输入翻译传 null。</param>
    /// <param name="callbacks">流式回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>译文；失败时抛出。</returns>
    public async Task<string> TranslateAsync(
        long threadId,
        string original,
        string? imagePath,
        TranslationCallbacks callbacks,
        CancellationToken ct = default)
    {
        var pair = ResolveLanguages(original);
        var segments = TextChunker.Split(original, MaxCharsPerRequest);
        var single = segments.Count == 1;

        var client = GetClient();
        string translation;

        if (single)
        {
            // 1) 取历史（此时还不含本次）并按上下文预算裁剪，据此构造对话上下文
            var history = _store.ListMessages(threadId);
            var context = BuildContext(history, pair);

            // 2) 追加本次的用户消息
            context.Add(ChatMessage.User(
                OllamaClient.BuildTranslatePrompt(original, pair.Target, pair.Source)));

            // 3) 用户消息先落库：即便翻译失败，用户的输入也不该丢
            _store.AddMessage(threadId, isUser: true, original, imagePath);
            _store.UpdateTitleIfDefault(threadId, original);

            // 4) 调用模型
            var progress = callbacks.OnProgress;

            // 不加 ConfigureAwait(false)：后续的 OnCompleted 会直接更新状态栏，
            // 必须在 UI 线程上执行，否则抛"调用线程无法访问此对象"。
            var reply = await client
                .CompleteAsync(context, progress is null ? null : progress.Invoke, temperature: null, ct);

            translation = reply.Text;
            if (translation.Length == 0)
            {
                throw new InvalidOperationException("模型返回了空译文");
            }

            callbacks.OnCompleted?.Invoke(reply.Metrics);
        }
        else
        {
            // 长文：分段落发送，且**不带对话历史**。
            //
            // 两个理由：
            //  ① 历史预算（num_ctx 的一半）与分段预算是同一份 num_ctx，叠加必然超出；
            //  ② 长文翻译本质上不是"对话"，把前几轮塞进去只会挤掉正文。
            //
            // 用户消息仍然先落库，且整次翻译在会话里仍是一条原文 + 一条译文。
            AppLog.Info($"输入较长（{original.Length} 字符），分 {segments.Count} 段翻译，本次不带对话历史");

            _store.AddMessage(threadId, isUser: true, original, imagePath);
            _store.UpdateTitleIfDefault(threadId, original);

            translation = await TranslateSegmentsAsync(client, segments, pair, callbacks, ct);
        }

        // 5) 译文落库
        _store.AddMessage(threadId, isUser: false, translation);

        return translation;
    }

    /// <summary>
    /// 把历史消息转成 Ollama 的对话格式，并按上下文预算裁剪。
    /// </summary>
    /// <remarks>
    /// <para>用户侧历史存的是<b>裸原文</b>，发给模型时按**当前**语言对重新包裹指令——
    /// 这样用户改了目标语言之后，后续轮次会用新语言，而不是沿用旧指令。</para>
    ///
    /// <para><b>必须裁剪</b>：不裁的话长会话跑几轮就会超出 <c>num_ctx</c>，
    /// 届时由 Ollama 自行丢弃最前面的消息，行为不可预期，
    /// 状态栏还会出现 <c>prompt_eval_count &gt; num_ctx</c> 的越界显示。
    /// 这里从最近的往回保留，达到预算即停，保证最近几轮始终在上下文内。</para>
    /// </remarks>
    private List<ChatMessage> BuildContext(
        IReadOnlyList<ChatMessageEntity> history,
        LanguagePair pair)
    {
        var budgetChars = _config.Current.NumCtx * HistoryBudgetRatio * CharsPerToken;

        var selected = new List<ChatMessageEntity>();
        var used = 0.0;

        // 从最近的消息往前取，直到用完预算
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];

            // 空消息（例如截图没识别出文字）跳过，否则会污染上下文
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var cost = message.Text.Length + 40;   // 40 ≈ 指令模板本身的固定开销
            if (used + cost > budgetChars && selected.Count > 0)
            {
                break;
            }

            selected.Add(message);
            used += cost;
        }

        // 取的时候是倒序，恢复成时间顺序
        selected.Reverse();

        var result = new List<ChatMessage>(selected.Count + 1);
        foreach (var message in selected)
        {
            result.Add(message.IsUser
                ? ChatMessage.User(
                    OllamaClient.BuildTranslatePrompt(message.Text, pair.Target, pair.Source))
                : ChatMessage.Assistant(message.Text));
        }

        return result;
    }

    /// <summary>
    /// 取 Ollama 客户端（进程内复用）。
    /// </summary>
    /// <remarks>
    /// <b>不能每次翻译都新建</b>：<see cref="OllamaClient"/> 在未注入 HttpClient 时会自建一个，
    /// 于是每次翻译都新建/销毁 HttpClient —— 连接无法复用、每次请求都要重新握手，
    /// 长时间运行还会累积 TIME_WAIT。
    /// 这里按「host / model / numCtx / keepAlive」指纹缓存，配置变了才重建。
    /// </remarks>
    public OllamaClient GetClient()
    {
        var config = _config.Current;
        var fingerprint =
            $"{config.OllamaHost}|{config.Model}|{config.NumCtx}|{config.OllamaKeepAlive}";

        lock (_clientGate)
        {
            if (_cachedClient is not null && _cachedFingerprint == fingerprint)
            {
                return _cachedClient;
            }

            _cachedClient?.Dispose();
            _cachedClient = new OllamaClient(
                config.OllamaHost, config.Model, config.NumCtx, config.OllamaKeepAlive);
            _cachedFingerprint = fingerprint;

            return _cachedClient;
        }
    }

    /// <summary>
    /// 模型是否不在显存里、本次请求要先等它载入（<c>GET /api/ps</c>）。
    /// </summary>
    /// <remarks>
    /// <para>只用于界面提示，与翻译请求并行发起。探测失败返回 <c>false</c> ——
    /// 宁可少提示，也不要凭空报「模型加载中」。</para>
    ///
    /// <para><b>⚠ 绝不能加 <c>ConfigureAwait(false)</c></b>：调用方拿到结果后要直接改界面
    /// （气泡文案 / 浮窗状态栏），续体必须留在调用方的同步上下文上。
    /// 本项目已经因为这一点栽过一次——逻辑全对而界面显示「翻译失败」。</para>
    /// </remarks>
    public async Task<bool> NeedsModelLoadAsync(CancellationToken ct = default) =>
        await GetClient().IsModelResidentAsync(ct) == false;

    /// <summary>
    /// 单次翻译：<b>不读写会话、不带历史上下文</b>。
    /// </summary>
    /// <remarks>
    /// 专供划词翻译使用。划词是被动、高频、碎片化的操作，
    /// 若计入对话上下文会迅速污染对话串并白白消耗 token——
    /// 它是"用完即走"的查询，与"持续对话"是两种交互。
    /// <para>原文超过单次请求容量时自动分段（见 <see cref="MaxCharsPerRequest"/>），
    /// 逐段翻译后按原文的分隔符拼回去。</para>
    /// </remarks>
    /// <returns>译文；失败时抛出。</returns>
    public async Task<string> TranslateOnceAsync(
        string text,
        LanguagePair pair,
        TranslationCallbacks callbacks,
        CancellationToken ct = default)
    {
        var client = GetClient();
        var segments = TextChunker.Split(text, MaxCharsPerRequest);

        if (segments.Count > 1)
        {
            AppLog.Info($"划词内容较长（{text.Length} 字符），分 {segments.Count} 段翻译");
            return await TranslateSegmentsAsync(client, segments, pair, callbacks, ct);
        }

        var context = new List<ChatMessage>
        {
            ChatMessage.User(OllamaClient.BuildTranslatePrompt(text, pair.Target, pair.Source)),
        };

        var progress = callbacks.OnProgress;

        // 同 TranslateAsync：续体必须留在 UI 线程（OnCompleted 更新状态栏）
        var reply = await client
            .CompleteAsync(context, progress is null ? null : progress.Invoke, temperature: null, ct);

        var translation = reply.Text;
        if (translation.Length == 0)
        {
            throw new InvalidOperationException("模型返回了空译文");
        }

        callbacks.OnCompleted?.Invoke(reply.Metrics);

        return translation;
    }

    /// <summary>
    /// 分段翻译：逐段请求、逐段上屏，最后拼接。
    /// </summary>
    /// <remarks>
    /// <para><b>逐段而不是并发</b>：本机只有一张卡，并发只会互相抢算力与显存，
    /// 而且并发下无法保证顺序、也报不出有意义的进度。</para>
    ///
    /// <para><b>某一段空了就整次失败</b>：交付一段"中间少了几百字"的译文比明确报错更糟——
    /// 用户不会逐字核对，只会把缺失当成模型的翻译风格。</para>
    ///
    /// <para>指标按段累加，<c>Truncated</c> 取或：任一段顶到输出上限，
    /// 整次译文就是不完整的，界面必须照实说。</para>
    /// </remarks>
    private async Task<string> TranslateSegmentsAsync(
        OllamaClient client,
        IReadOnlyList<TextSegment> segments,
        LanguagePair pair,
        TranslationCallbacks callbacks,
        CancellationToken ct)
    {
        var translations = new List<string>(segments.Count);
        var progress = callbacks.OnProgress;

        var promptTokens = 0;
        var evalTokens = 0;
        var evalNs = 0L;
        var totalNs = 0L;
        var loadNs = 0L;
        var truncated = false;

        for (var i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            callbacks.OnChunkProgress?.Invoke(i + 1, segments.Count);

            var context = new List<ChatMessage>
            {
                ChatMessage.User(OllamaClient.BuildTranslatePrompt(
                    segments[i].Text, pair.Target, pair.Source)),
            };

            var done = i;   // 闭包捕获：避免所有回调都读到循环结束后的 i
            var reply = await client.CompleteAsync(
                context,
                progress is null
                    ? null
                    : piece => progress(TextChunker.Join(
                        segments.Take(done + 1).ToList(),
                        [.. translations, piece])),
                temperature: null,
                ct);

            if (reply.Text.Length == 0)
            {
                throw new InvalidOperationException(
                    $"第 {i + 1}/{segments.Count} 段返回了空译文，已中止（避免交付残缺译文）");
            }

            translations.Add(reply.Text);

            promptTokens += reply.Metrics.PromptEvalCount;
            evalTokens += reply.Metrics.EvalCount;
            evalNs += reply.Metrics.EvalDurationNs;
            totalNs += reply.Metrics.TotalDurationNs;
            loadNs += reply.Metrics.LoadDurationNs;
            truncated |= reply.Metrics.Truncated;

            // 把已完成的段整段推一次，让界面在段与段之间也看得到进展
            progress?.Invoke(TextChunker.Join(segments.Take(i + 1).ToList(), translations));
        }

        var result = TextChunker.Join(segments, translations);

        callbacks.OnCompleted?.Invoke(new ChatMetrics(
            promptTokens,
            evalTokens,
            evalNs,
            totalNs,
            loadNs,
            _config.Current.NumCtx,
            truncated ? "length" : "stop"));

        return result;
    }

    /// <summary>
    /// 把截图另存到应用数据目录，返回新路径。
    ///
    /// <para>不直接引用临时文件：临时目录会被系统清理，而会话记录要长期可回溯。</para>
    /// </summary>
    public static string SaveImageForThread(byte[] pngBytes)
    {
        AppPaths.EnsureCreated();

        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png";
        var path = Path.Combine(AppPaths.ImagesDirectory, fileName);
        File.WriteAllBytes(path, pngBytes);

        return path;
    }

    public void Dispose()
    {
        lock (_clientGate)
        {
            _cachedClient?.Dispose();
            _cachedClient = null;
            _cachedFingerprint = null;
        }
    }
}
