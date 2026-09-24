using System.Diagnostics;
using ChatTranslate.Data;

namespace ChatTranslate.Core;

/// <summary>划词命中：取到了可翻译的文本。</summary>
/// <param name="Text">取到的文本。</param>
/// <param name="ScreenX">触发时的光标 X（物理像素）。</param>
/// <param name="ScreenY">触发时的光标 Y（物理像素）。</param>
/// <param name="Method">实际生效的取词通道。</param>
public readonly record struct SelectionHit(
    string Text, int ScreenX, int ScreenY, SelectionMethod Method);

/// <summary>
/// 被动划词监听：鼠标钩子 → 防抖 → 黑名单 → 取词 → 规则判定 → 通知上层。
///
/// <para>上层只需要订阅 <see cref="SelectionDetected"/> 事件，不必关心钩子与取词细节。</para>
/// </summary>
public sealed class SelectionWatcher : IDisposable
{
    private readonly MouseHook _mouseHook = new();
    private readonly SelectionRuleEngine _rules = new();
    private readonly ConfigStore _config;

    private CancellationTokenSource? _debounce;
    private bool _enabled;
    private bool _disposed;

    /// <summary>检测到可翻译的划词内容时触发（在 UI 线程上）。</summary>
    public event Action<SelectionHit>? SelectionDetected;

    /// <summary>
    /// 全局鼠标左键抬起（<b>任何</b>位置，包括本进程窗口上）。
    /// </summary>
    /// <remarks>
    /// 供"点击浮窗外部即关闭"这类需求使用。刻意与划词判定解耦：
    /// 它不依赖窗口能否取得焦点，因此在浮窗抢不到焦点的场景下依然可靠。
    /// </remarks>
    public event Action<MouseUpEvent>? GlobalLeftClick;

    /// <summary>
    /// 取到的内容因过长而未翻译时触发（在 UI 线程上）。
    /// 这是唯一需要让用户知道的拒绝原因——其他拒绝都静默。
    /// </summary>
    public event Action<string, int, int>? ContentTooLong;

    public SelectionWatcher(ConfigStore config)
    {
        _config = config;
        _mouseHook.LeftButtonUp += OnLeftButtonUp;
    }

    /// <summary>当前是否已启用。</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// 启用监听。必须从 UI 线程调用（低级鼠标钩子依赖消息泵）。
    /// </summary>
    /// <returns>失败原因；成功返回 null。</returns>
    public string? Enable()
    {
        if (_disposed)
        {
            return "监听器已释放";
        }

        if (_enabled)
        {
            return null;
        }

        var error = _mouseHook.Install();
        if (error is not null)
        {
            AppLog.Error($"划词监听启用失败：{error}");
            return error;
        }

        // 清掉上次的去重记忆：用户刚打开开关时，第一次划词必须能正常触发
        _rules.Reset();
        _enabled = true;

        AppLog.Info($"划词监听已启用（防抖 {_config.Current.SelectionDelayMs} ms，"
                    + $"排除名单 {_config.Current.SelectionBlacklist?.Count ?? 0} 项）");

        return null;
    }

    /// <summary>停用监听（卸载钩子）。</summary>
    public void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _mouseHook.Uninstall();
        CancelDebounce();
        _rules.Reset();
        _enabled = false;
        AppLog.Info("划词监听已停用");
    }

    private void CancelDebounce()
    {
        var previous = Interlocked.Exchange(ref _debounce, null);
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已被释放，忽略
            }

            previous.Dispose();
        }
    }

    /// <summary>
    /// 鼠标左键抬起。这里必须立即返回：
    /// Windows 对低级钩子有超时限制，回调里做耗时操作会被摘掉钩子。
    /// </summary>
    private void OnLeftButtonUp(MouseUpEvent e)
    {
        if (!_enabled)
        {
            return;
        }

        // 先无条件广播"用户点了某处"。浮窗靠它实现"点外部即关闭"，
        // 这条路径不依赖窗口焦点（失焦事件依赖，而浮窗常抢不到焦点）。
        GlobalLeftClick?.Invoke(e);

        // 记录此刻的前台窗口：稍后取词时用户可能已经切换窗口，
        // 而我们要判断的是"划词发生在哪个应用"。
        var sourceWindow = NativeMethods.GetForegroundWindow();

        // 点击落在我们自己的窗口上（主窗口 / 弹窗 / 框选层）时不响应，
        // 否则点一下弹窗就会再次触发取词，形成回环。
        if (IsOwnProcess(sourceWindow))
        {
            AppLog.Trace("鼠标抬起：本进程窗口，忽略");
            return;
        }

        if (IsBlacklisted(sourceWindow, out var processName))
        {
            AppLog.Trace($"鼠标抬起：{processName} 在排除名单中，忽略");
            return;
        }

        AppLog.Trace($"鼠标抬起 @({e.ScreenX},{e.ScreenY}) 前台={processName}，开始防抖");

        // 每次抬起都重置防抖：连续拖选只会在最后一次停下后才触发
        CancelDebounce();

        var cts = new CancellationTokenSource();
        _debounce = cts;
        var token = cts.Token;
        var delay = Math.Max(0, _config.Current.SelectionDelayMs);

        _ = Task.Run(() => DebouncedGrabAsync(sourceWindow, e.ScreenX, e.ScreenY, delay, token), token);
    }

    private async Task DebouncedGrabAsync(
        IntPtr sourceWindow, int screenX, int screenY, int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户在防抖窗口内又点了一次，本次作废
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await GrabAndEvaluateAsync(sourceWindow, screenX, screenY, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户又划了新词，正常作废
        }
        catch (Exception ex)
        {
            // 这里的异常必须自己接住并记录：
            // 本方法是 fire-and-forget（`_ = Task.Run(...)`）启动的，
            // 一旦抛出就成为"无人观察的异常"，被运行时静默吞掉——
            // 表现正是"划词毫无反应且没有任何提示"，完全无法排查。
            AppLog.Error("划词处理失败", ex);
        }
    }

    private async Task GrabAndEvaluateAsync(
        IntPtr sourceWindow, int screenX, int screenY, CancellationToken ct)
    {
        // 取词会阻塞（UIA 可能挂起、Ctrl+C 要等剪贴板），必须离开 UI 线程
        var result = await Task.Run(SelectionGrabber.Grab, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            return;
        }

        AppLog.Trace($"取词结果：通道={result.Method}，长度={(result.Text?.Length ?? 0)}"
                     + (result.Note is null ? string.Empty : $"，原因={result.Note}"));

        var verdict = _rules.Evaluate(result.Text, sourceWindow);

        if (!verdict.Allowed)
        {
            if (verdict.NotifyUser)
            {
                // 超长是唯一需要提示的拒绝
                RaiseOnUi(() => ContentTooLong?.Invoke(verdict.Reason ?? "内容过长", screenX, screenY));
            }
            else
            {
                // 其余情况静默：被动模式下弹提示是骚扰，但日志里要留痕
                AppLog.Trace($"划词被规则拒绝：{verdict.Reason}");
            }

            return;
        }

        AppLog.Info($"划词命中：{result.Text!.Length} 字符，通道={result.Method}");

        var hit = new SelectionHit(result.Text!, screenX, screenY, result.Method);
        RaiseOnUi(() => SelectionDetected?.Invoke(hit));
    }

    /// <summary>把回调切回 UI 线程（弹窗必须在 UI 线程创建）。</summary>
    private static void RaiseOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>窗口是否属于本进程。</summary>
    private static bool IsOwnProcess(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>窗口所属进程是否在排除名单里。</summary>
    private bool IsBlacklisted(IntPtr window, out string processName)
    {
        processName = "?";
        if (window == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            if (pid == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;

            var blacklist = _config.Current.SelectionBlacklist;

            // 配置文件被手工编辑成 null 时（或字段缺失后又被显式写 null），
            // 这里必须当成"没有名单"而不是直接崩。
            if (blacklist is null || blacklist.Count == 0)
            {
                return false;
            }

            // out 参数不能直接用在 lambda 里，先拷到局部变量
            var name = processName;

            return blacklist.Any(entry =>
                !string.IsNullOrWhiteSpace(entry)
                && string.Equals(entry.Trim(), name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // 进程可能已退出、或权限不足，一律按"不在名单里"处理
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disable();
        _mouseHook.LeftButtonUp -= OnLeftButtonUp;
        _mouseHook.Dispose();
    }
}
