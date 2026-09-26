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
    private readonly SelectionRuleEngine _rules;
    private readonly ConfigStore _config;

    private CancellationTokenSource? _debounce;
    private bool _enabled;
    private bool _disposed;

    /// <summary>
    /// 取词在途标记。0 = 空闲，非 0 = 已有一次取词在执行。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须串行化取词</b>：Ctrl+C 回退路径是
    /// 「备份剪贴板 → 模拟 Ctrl+C → 轮询序号 → 还原」的<b>复合</b>操作，不是原子的
    /// （<c>ClipboardBackup</c> 的锁只保护单次剪贴板调用，不保护整个事务）。</para>
    ///
    /// <para>防抖只能取消"还在等待中"的任务；一旦上一次已进入 <c>Grab()</c>
    /// （UIA 最长 400ms，Ctrl+C 回退还含最长 1s 的修饰键等待），用户再次鼠标抬起
    /// 就会并发进入第二次取词。此时后一次的备份可能正好读到前一次刚写进去的选中文本，
    /// 两次还原的先后顺序又是不确定的——最坏情况是<b>用户原本的剪贴板内容被永久
    /// 替换成这段选中文本，不可逆</b>。</para>
    /// </remarks>
    private int _grabInFlight;

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

        // 上限取"每次判定时现读配置"，用户在设置里改完立即生效，不必重启监听
        _rules = new SelectionRuleEngine(() => _config.Current.SelectionMaxChars);
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

    /// <summary>
    /// 取消尚未开始的防抖等待。
    /// </summary>
    /// <remarks>
    /// <b>只 Cancel，不 Dispose。</b>在仍有并发注册时 Dispose 一个 CancellationTokenSource
    /// 是已知隐患：<c>Task.Delay</c> 内部会注册取消回调，若此时 CTS 已被释放，
    /// 可能抛 <see cref="ObjectDisposedException"/>，而该异常会逃出 fire-and-forget 任务
    /// 被运行时静默吞掉。CTS 由持有它的那个任务在 finally 中释放。
    /// </remarks>
    private void CancelDebounce()
    {
        var previous = Interlocked.Exchange(ref _debounce, null);

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已被任务侧释放，忽略
        }
    }

    /// <summary>
    /// 鼠标左键抬起。
    /// </summary>
    /// <remarks>
    /// <para><b>本方法运行在低级鼠标钩子的回调栈上，必须极快返回。</b>
    /// 超过 <c>LowLevelHooksTimeout</c> 时 Windows 会<b>静默摘除钩子</b>，
    /// 表现是"用一会儿就失灵"；且回调在 UI 线程上，阻塞还会造成界面卡顿。</para>
    ///
    /// <para>因此这里只做两件极轻量的事：取光标坐标、取前台窗口<b>句柄</b>。
    /// 进程名查询（<c>Process.GetProcessById</c>）、黑名单比对与日志写盘
    /// （<c>File.AppendAllText</c> + 抢锁）全部下移到后台任务——它们都可能耗时。</para>
    ///
    /// <para>前台句柄必须在<b>这里</b>取而不能下移：用户随后可能切换窗口，
    /// 那时再取就不是"划词发生在哪个应用"了。</para>
    /// </remarks>
    private void OnLeftButtonUp(MouseUpEvent e)
    {
        if (!_enabled)
        {
            return;
        }

        var sourceWindow = NativeMethods.GetForegroundWindow();
        var isOwn = IsOwnProcess(sourceWindow);

        // 点外部关闭浮窗：投递到消息泵执行，不占用钩子回调的时间。
        // 这条路径不依赖窗口焦点（失焦事件依赖，而浮窗常抢不到焦点）。
        RaiseOnUi(() => GlobalLeftClick?.Invoke(e));

        // 点在自己窗口上（主窗口 / 弹窗 / 框选层）不参与划词，否则会形成回环
        if (isOwn)
        {
            return;
        }

        // 以下都是轻量操作：取消上一个防抖、建令牌、派后台任务。
        // 连续拖选只会在最后一次停下后才真正取词。
        CancelDebounce();

        var cts = new CancellationTokenSource();
        _debounce = cts;
        var delay = Math.Max(0, _config.Current.SelectionDelayMs);

        // 刻意不把 token 传给 Task.Run：token 已取消时 Task.Run 会直接返回，
        // 那样方法体不执行、finally 里的 CTS 释放就被跳过了。
        // 让方法体自己观察取消状态，代价只是一次极短的执行。
        _ = Task.Run(() => DebouncedGrabAsync(
            sourceWindow, e.ScreenX, e.ScreenY, delay, cts));
    }

    /// <summary>
    /// 防抖等待 + 取词 + 规则判定。整个方法体都要保证不抛异常。
    /// </summary>
    /// <remarks>
    /// 本方法由 fire-and-forget 启动，任何逃出的异常都会成为"无人观察的异常"
    /// 被运行时静默吞掉——表现正是"划词毫无反应且无法排查"。
    /// 所以最外层必须有 catch-all，并且负责释放传入的 CTS。
    /// </remarks>
    private async Task DebouncedGrabAsync(
        IntPtr sourceWindow, int screenX, int screenY, int delayMs, CancellationTokenSource cts)
    {
        var ct = cts.Token;

        try
        {
            // 黑名单与日志都在这里做（不能在钩子回调里做）。
            // 放在延迟之前：命中的话连等都不用等。
            if (IsBlacklisted(sourceWindow, out var processName))
            {
                AppLog.Trace($"鼠标抬起：{processName} 在排除名单中，忽略");
                return;
            }

            AppLog.Trace($"鼠标抬起 @({screenX},{screenY}) 前台={processName}，开始防抖");

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            // 串行化取词：在途时直接丢弃本次触发。
            // 见 _grabInFlight 的说明——并发会让 Ctrl+C 回退路径破坏用户剪贴板。
            if (Interlocked.CompareExchange(ref _grabInFlight, 1, 0) != 0)
            {
                AppLog.Trace("已有取词在执行，丢弃本次触发");
                return;
            }

            try
            {
                // 取词会阻塞（UIA 可能挂起、Ctrl+C 要等剪贴板），必须离开 UI 线程
                var result = await Task.Run(SelectionGrabber.Grab, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                Evaluate(sourceWindow, screenX, screenY, result);
            }
            finally
            {
                Volatile.Write(ref _grabInFlight, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户又划了新词，正常作废
        }
        catch (Exception ex)
        {
            AppLog.Error("划词处理失败", ex);
        }
        finally
        {
            // CTS 由持有它的任务释放，避免在并发注册时被 Dispose
            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 忽略
            }
        }
    }

    /// <summary>对取词结果做规则判定并上报。</summary>
    private void Evaluate(IntPtr sourceWindow, int screenX, int screenY, SelectionResult result)
    {
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

    /// <summary>
    /// 把回调切回 UI 线程（弹窗必须在 UI 线程创建）。
    /// </summary>
    /// <remarks>
    /// 订阅者的异常必须在这里接住：<c>BeginInvoke</c> 投递的委托若抛出，
    /// 会沿 WPF 的 <c>DispatcherUnhandledException</c> 冒泡，
    /// 单个订阅者出问题就能拖垮整个应用；而且是异步投递，异常也回传不到调用方。
    /// </remarks>
    private static void RaiseOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        void SafeInvoke()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("划词事件订阅者抛出异常", ex);
            }
        }

        if (dispatcher.CheckAccess())
        {
            SafeInvoke();
        }
        else
        {
            dispatcher.BeginInvoke((Action)SafeInvoke);
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
            // 名单为空时直接返回，省掉一次进程查询。
            // 顺序不能反：用户清空名单后这次查询纯属浪费，
            // 而它过去还发生在钩子回调线程上。
            // 配置文件被手工编辑成 null 时也要当成"没有名单"而不是崩。
            var blacklist = _config.Current.SelectionBlacklist;
            if (blacklist is null || blacklist.Count == 0)
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            if (pid == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;

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
