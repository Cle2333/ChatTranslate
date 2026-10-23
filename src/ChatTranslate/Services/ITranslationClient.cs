namespace ChatTranslate.Services;

/// <summary>
/// 翻译服务客户端的统一契约。
/// </summary>
/// <remarks>
/// 本地 Ollama 与 DeepSeek 的请求格式、流式协议、模型管理能力都不同，但
/// <see cref="TranslationService"/> 只关心四件事：列模型、翻译、查驻留、加载/卸载。
/// 把差异留在各自客户端里，避免编排层出现成片的 provider 分支。
/// </remarks>
public interface ITranslationClient : IDisposable
{
    /// <summary>服务商显示名，用于状态栏与日志。</summary>
    string ProviderName { get; }

    /// <summary>当前模型名。</summary>
    string Model { get; }

    /// <summary>该客户端用于上下文预算的窗口大小（tokens）。</summary>
    int ContextWindowTokens { get; }

    /// <summary>是否支持把模型预载入 / 卸出显存。</summary>
    bool SupportsModelManagement { get; }

    /// <summary>最近一次失败原因；成功或尚未请求时为 null。</summary>
    string? LastError { get; }

    /// <summary>列出服务端可见的模型；失败返回 null。</summary>
    Task<IReadOnlyList<string>?> ListModelsAsync(CancellationToken ct = default);

    /// <summary>发送对话并流式回调累积译文。</summary>
    Task<ChatReply> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onProgress = null,
        double? temperature = null,
        CancellationToken ct = default);

    /// <summary>查询本地模型驻留状态；云端客户端固定返回 Unknown。</summary>
    Task<ModelResidencyState> GetResidencyAsync(CancellationToken ct = default);

    /// <summary>把模型预载入显存；云端客户端不支持。</summary>
    Task LoadModelAsync(CancellationToken ct = default);

    /// <summary>把模型卸出显存；云端客户端不支持。</summary>
    Task UnloadModelAsync(CancellationToken ct = default);
}

/// <summary>翻译链路的共享上限。</summary>
/// <remarks>
/// 输出上限必须由本地与云端共用：分段预算、截断提示都按同一个值计算。
/// 各服务商的字段名不同（<c>num_predict</c> / <c>max_tokens</c>），语义相同。
/// </remarks>
public static class TranslationLimits
{
    public const int MaxOutputTokens = 4096;
}
