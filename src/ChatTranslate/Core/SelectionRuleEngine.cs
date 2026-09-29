namespace ChatTranslate.Core;

/// <summary>规则判定结果。</summary>
/// <param name="Allowed">是否放行去翻译。</param>
/// <param name="Reason">被拒原因（用于调试日志，不弹给用户）。</param>
/// <param name="NotifyUser">是否需要给用户一个可见提示（仅超长时用）。</param>
public readonly record struct SelectionVerdict(bool Allowed, string? Reason, bool NotifyUser = false);

/// <summary>
/// 被动划词的防抖与内容过滤规则。
///
/// <para><b>为什么必须有这层</b>：被动模式没有快捷键，意味着<b>每一次鼠标左键抬起都会触发</b>。
/// 缺少过滤的典型翻车：拖动滑块、选中文件名、鼠标划过文本都在翻译，
/// 甚至把剪贴板里的旧内容翻出来。</para>
///
/// <para>本类是<b>纯逻辑</b>：不碰钩子、不碰 UI、不碰网络，只根据输入给出判定。
/// 这样规则可以脱离实际环境被单独验证。</para>
/// </summary>
public sealed class SelectionRuleEngine
{
    /// <summary>短于该长度的选中内容不翻译。</summary>
    public const int MinLength = 2;

    /// <summary>未指定上限时使用的默认值。</summary>
    public const int DefaultMaxLength = Data.AppConfig.DefaultSelectionMaxChars;

    private readonly Func<int> _maxChars;

    /// <summary>
    /// 构造规则引擎。
    /// </summary>
    /// <param name="maxChars">
    /// 取"原文长度上限"的委托。<b>用委托而不是构造时取一次值</b>：上限是设置项，
    /// 用户在设置里改完应立即生效，而构造发生在监听器创建时（早于任何一次改设置）。
    /// </param>
    public SelectionRuleEngine(Func<int>? maxChars = null)
    {
        _maxChars = maxChars ?? (() => DefaultMaxLength);
    }

    /// <summary>当前生效的长度上限（字符）。</summary>
    /// <remarks>
    /// 取值规则与 <c>AppConfig.Normalize</c> 保持一致：非正值视为「没配」并回落到默认，
    /// 其余一律收敛到 <c>[MinSelectionMaxChars, MaxSelectionMaxChars]</c>。
    /// 直接用「≥ 选中最短长度就采纳」的话，2~199 这种比配置下限还小的值会被原样采用，
    /// 而 0/负数会被放大成默认上限（20000）—— 调用方想表达「不许超长」却得到最宽松的值。
    /// </remarks>
    public int MaxLength
    {
        get
        {
            var value = _maxChars();

            if (value <= 0)
            {
                return DefaultMaxLength;
            }

            return Math.Clamp(
                value,
                Data.AppConfig.MinSelectionMaxChars,
                Data.AppConfig.MaxSelectionMaxChars);
        }
    }

    /// <summary>同一内容 + 同一窗口在该时间窗内不重复翻译。</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();

    private string? _lastText;
    private IntPtr _lastWindow;
    private DateTime _lastAcceptedAt = DateTime.MinValue;

    /// <summary>
    /// 判定一段取到的文本是否可以送去翻译。
    /// </summary>
    /// <param name="text">取词结果；null 表示取词失败。</param>
    /// <param name="sourceWindow">取词时前台窗口句柄，用于同源去重。</param>
    /// <param name="now">当前时间（便于测试注入）。</param>
    public SelectionVerdict Evaluate(string? text, IntPtr sourceWindow, DateTime? now = null)
    {
        var timestamp = now ?? DateTime.UtcNow;

        // 取词失败或空内容：静默丢弃。
        // 被动模式下弹窗提示是骚扰——用户可能只是点了一下空白处。
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SelectionVerdict(false, "未取到文本");
        }

        var trimmed = text.Trim();

        if (trimmed.Length < MinLength)
        {
            return new SelectionVerdict(false, $"内容过短（{trimmed.Length} 字符）");
        }

        if (IsTrivial(trimmed))
        {
            return new SelectionVerdict(false, "无意义的字符（纯数字 / 标点 / 符号）");
        }

        if (trimmed.Length > MaxLength)
        {
            // 超长是唯一需要让用户知道的拒绝：他确实选了东西，但没被翻译，
            // 不提示的话会以为程序坏了。提示里要带上"去哪调"，
            // 否则用户只会得出"这软件翻不了长文"的结论。
            lock (_gate)
            {
                Remember(trimmed, sourceWindow, timestamp);
            }

            return new SelectionVerdict(
                false,
                $"内容过长（{trimmed.Length} 字符，上限 {MaxLength}）\n\n"
                + "上限可在「设置 → 划词翻译 → 最长字符数」里调整。",
                NotifyUser: true);
        }

        lock (_gate)
        {
            // 同源去重：同一窗口里连续两次取到完全一样的内容，
            // 通常是用户在同一个位置反复点击（或我们的 Ctrl+C 回退把内容又写了一遍），
            // 不应该重复请求模型。
            if (_lastText == trimmed
                && _lastWindow == sourceWindow
                && timestamp - _lastAcceptedAt < DuplicateWindow)
            {
                return new SelectionVerdict(false, "与上一次内容相同且间隔过短");
            }

            Remember(trimmed, sourceWindow, timestamp);
        }

        return new SelectionVerdict(true, null);
    }

    /// <summary>
    /// 是否属于"没有翻译价值"的内容：全是数字、标点、空白或符号，且不含任何字母/汉字。
    /// </summary>
    /// <remarks>
    /// 用「是否含有文字类字符」判断，而不是逐个白名单标点——
    /// 后者在不同语言的全角/半角标点上很容易漏。
    /// </remarks>
    public static bool IsTrivial(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>清空去重记忆（例如用户关闭再开启划词时，避免旧记录挡住第一次翻译）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _lastText = null;
            _lastWindow = IntPtr.Zero;
            _lastAcceptedAt = DateTime.MinValue;
        }
    }

    private void Remember(string text, IntPtr window, DateTime timestamp)
    {
        _lastText = text;
        _lastWindow = window;
        _lastAcceptedAt = timestamp;
    }
}
