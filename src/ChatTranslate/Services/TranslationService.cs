using System.IO;
using ChatTranslate.Data;

namespace ChatTranslate.Services;

/// <summary>一次翻译请求的来源。</summary>
public enum TranslateSource
{
    /// <summary>输入框输入。</summary>
    Input,

    /// <summary>截图 OCR。</summary>
    Ocr,
}

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
public sealed class TranslationService
{
    private readonly ChatStore _store;
    private readonly ConfigStore _config;

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
    /// <para>规则：</para>
    /// <list type="bullet">
    /// <item>源语言显式选定且与目标相同时 → 交换目标语言（避免"把中文翻成中文"这种空转）</item>
    /// <item>源语言为自动检测，且检测结果恰好等于目标语言 → 同样交换</item>
    /// <item>其余情况 → 按用户所选执行</item>
    /// </list>
    /// <para>交换行为会通过 <see cref="LanguagePair.Swapped"/> 告知调用方，由界面提示用户，
    /// 不做静默处理。</para>
    /// </summary>
    public LanguagePair ResolveLanguages(string text)
    {
        var target = ConfiguredTarget;
        var source = ConfiguredSource;

        // 自动检测时给出检测结果，供界面提示使用
        var detectedCode = source?.Code ?? Languages.Detect(text);
        var detectedLanguage = detectedCode is null ? null : Languages.ByCode(detectedCode);

        if (detectedCode is null || detectedCode != target.Code)
        {
            return new LanguagePair(source, target, Swapped: false, detectedLanguage);
        }

        // 源与目标撞了：换到另一种
        var swapped = target.Code switch
        {
            "zh" => Languages.ByCode("en") ?? target,
            "en" => Languages.Default,
            _ => target,
        };

        var stillSame = swapped.Code == target.Code;
        return new LanguagePair(source, swapped, Swapped: !stillSame, detectedLanguage);
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

        // 1) 先取历史（此时还不含本次），据此构造对话上下文
        var history = _store.ListMessages(threadId);
        var context = BuildContext(history, pair);

        // 2) 追加本次的用户消息
        context.Add(ChatMessage.User(
            OllamaClient.BuildTranslatePrompt(original, pair.Target, pair.Source)));

        // 3) 用户消息先落库：即便翻译失败，用户的输入也不该丢
        _store.AddMessage(threadId, isUser: true, original, imagePath);
        _store.UpdateTitleIfDefault(threadId, original);

        // 4) 调用模型
        using var client = CreateClient();
        var buffer = new System.Text.StringBuilder();
        ChatMetrics? metrics = null;

        await foreach (var piece in client.StreamTextAsync(
                           context, m => metrics = m, temperature: null, ct: ct))
        {
            buffer.Clear();
            buffer.Append(piece);
            callbacks.OnProgress?.Invoke(buffer.ToString());
        }

        var translation = buffer.ToString().Trim();
        if (translation.Length == 0)
        {
            throw new InvalidOperationException("模型返回了空译文");
        }

        // 5) 译文落库
        _store.AddMessage(threadId, isUser: false, translation);

        if (metrics is { } m)
        {
            callbacks.OnCompleted?.Invoke(m);
        }

        return translation;
    }

    /// <summary>
    /// 把历史消息转成 Ollama 的对话格式。
    ///
    /// <para>用户侧历史存的是<b>裸原文</b>，发给模型时按**当前**语言对重新包裹指令——
    /// 这样用户改了目标语言之后，后续轮次会用新语言，而不是沿用旧指令。</para>
    /// </summary>
    private static List<ChatMessage> BuildContext(
        IReadOnlyList<ChatMessageEntity> history,
        LanguagePair pair)
    {
        var result = new List<ChatMessage>(history.Count + 1);

        foreach (var message in history)
        {
            // 空消息（例如截图没识别出文字）跳过，否则会污染上下文
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            result.Add(message.IsUser
                ? ChatMessage.User(OllamaClient.BuildTranslatePrompt(message.Text, pair.Target, pair.Source))
                : ChatMessage.Assistant(message.Text));
        }

        return result;
    }

    /// <summary>按当前配置构造 Ollama 客户端。</summary>
    public OllamaClient CreateClient()
    {
        var config = _config.Current;
        return new OllamaClient(config.OllamaHost, config.Model, config.NumCtx);
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
}
