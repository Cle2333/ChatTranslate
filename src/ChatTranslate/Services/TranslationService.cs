using System.IO;
using ChatTranslate.Data;

namespace ChatTranslate.Services;

/// <summary>翻译的流式进度回调。</summary>
public sealed class TranslationCallbacks
{
    /// <summary>收到新的累积文本（已清洗控制 token）。</summary>
    public Action<string>? OnProgress { get; init; }

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
        var client = GetClient();
        var progress = callbacks.OnProgress;

        // 不加 ConfigureAwait(false)：后续的 OnCompleted 会直接更新状态栏，
        // 必须在 UI 线程上执行，否则抛"调用线程无法访问此对象"。
        var reply = await client
            .CompleteAsync(context, progress is null ? null : progress.Invoke, temperature: null, ct);

        var translation = reply.Text;
        if (translation.Length == 0)
        {
            throw new InvalidOperationException("模型返回了空译文");
        }

        // 5) 译文落库
        _store.AddMessage(threadId, isUser: false, translation);

        callbacks.OnCompleted?.Invoke(reply.Metrics);

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
    /// 单次翻译：<b>不读写会话、不带历史上下文</b>。
    /// </summary>
    /// <remarks>
    /// 专供划词翻译使用。划词是被动、高频、碎片化的操作，
    /// 若计入对话上下文会迅速污染对话串并白白消耗 token——
    /// 它是"用完即走"的查询，与"持续对话"是两种交互。
    /// </remarks>
    /// <returns>译文；失败时抛出。</returns>
    public async Task<string> TranslateOnceAsync(
        string text,
        LanguagePair pair,
        TranslationCallbacks callbacks,
        CancellationToken ct = default)
    {
        var context = new List<ChatMessage>
        {
            ChatMessage.User(OllamaClient.BuildTranslatePrompt(text, pair.Target, pair.Source)),
        };

        var client = GetClient();
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
