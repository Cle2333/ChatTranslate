using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ChatTranslate.Core;
using ChatTranslate.Services;
using Wpf.Ui.Controls;

namespace ChatTranslate.Views;

/// <summary>
/// 划词翻译浮窗：无边框、置顶、跟随光标弹出。
/// </summary>
/// <remarks>
/// <para><b>失焦关闭必须延迟</b>：Windows 下拖动窗口会先触发失焦、紧接着又回到焦点。
/// 若立即关闭，用户一拖动窗口它就消失了（pot-desktop 也踩过这个坑）。</para>
///
/// <para><b>定位用物理像素</b>：通过 <c>SetWindowPos</c> 直接设置，
/// 绕开 WPF 的 Left/Top（DIP）在多显示器不同缩放比下的换算歧义。</para>
/// </remarks>
public partial class TranslatePopup : Window
{
    /// <summary>失焦后延迟关闭的时间。给"拖动窗口先失焦再得焦"留出窗口。</summary>
    private static readonly TimeSpan BlurCloseDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// 正在翻译的文案。
    /// </summary>
    /// <remarks>
    /// 三处使用：弹出时的初始状态、每次流式分块的复位、以及复位后与
    /// <see cref="BubbleViewModel.ModelLoadingLabel"/> 的区分。写字面量容易漂移。
    /// </remarks>
    private const string TranslatingLabel = "翻译中…";

    private System.Windows.Threading.DispatcherTimer? _blurTimer;
    private CancellationTokenSource? _cts;
    private bool _pinned;
    private bool _positioned;

    /// <summary>
    /// 光标锚点（物理像素）。窗口高度会随译文流式增长而变，
    /// 需要靠它重新计算位置，所以必须留存而不是只在弹出时用一次。
    /// </summary>
    private int _anchorX;
    private int _anchorY;

    public TranslatePopup()
    {
        InitializeComponent();

        Deactivated += OnDeactivated;
        Activated += OnActivated;
        KeyDown += OnKeyDown;
        SourceInitialized += OnSourceInitialized;

        // 译文是流式推进的，窗口会不断变高。若不重新夹取位置，
        // 光标靠近屏幕底部时窗口下半部分（含按钮与状态栏）会溢出工作区，
        // 用户看不到也点不到。
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// 在当前光标位置弹出并开始翻译。
    /// </summary>
    /// <param name="text">待翻译文本。</param>
    /// <param name="service">翻译服务。</param>
    /// <param name="screenX">光标 X（物理像素）。</param>
    /// <param name="screenY">光标 Y（物理像素）。</param>
    /// <param name="method">实际生效的取词通道，用于提示用户是否借用了剪贴板。</param>
    public async Task ShowForAsync(string text, TranslationService service,
        int screenX, int screenY, SelectionMethod method = SelectionMethod.Uia)
    {
        CancelRunning();

        var pair = service.ResolveLanguages(text);

        _anchorX = screenX;
        _anchorY = screenY;
        _positioned = false;

        // 原文很短时不必占一块地方重复显示
        OriginalText.Text = text;
        OriginalBox.Visibility = text.Length > 60 ? Visibility.Visible : Visibility.Collapsed;

        LangText.Text = $"{pair.Source?.ChineseName ?? "自动"} → {pair.Target.ChineseName}";

        // 取词通道对用户有意义：Ctrl+C 回退会短暂占用剪贴板，
        // 说明了为什么有时剪贴板内容会"闪一下"。
        ChannelText.Text = method switch
        {
            SelectionMethod.Uia => "UIA",
            SelectionMethod.Clipboard => "剪贴板",
            _ => string.Empty,
        };

        // 换向必须显式说明：只靠标题栏那行小字用户注意不到，
        // 会以为程序把语言搞错了。
        if (pair.Swapped)
        {
            var origin = pair.ActualSource?.ChineseName ?? "原文";
            SwapNoticeText.Text = $"原文是{origin}，已自动译为{pair.Target.ChineseName}";
            SwapNotice.Visibility = Visibility.Visible;
        }
        else
        {
            SwapNotice.Visibility = Visibility.Collapsed;
        }

        TranslationText.Text = string.Empty;
        StatusText.Text = TranslatingLabel;
        CopyButton.IsEnabled = false;
        _pinned = false;
        PinButton.Appearance = ControlAppearance.Secondary;

        // 先以透明方式显示，等拿到窗口句柄定位后再显形，避免在旧位置闪一下
        Opacity = 0;
        Show();

        var cts = new CancellationTokenSource();
        _cts = cts;

        // 模型不在显存里时，这几秒是在等它载入——必须说出来，否则浮窗里长时间空着，
        // 用户会以为划词没生效。与翻译并行发起，不阻塞请求。
        _ = ApplyModelLoadHintAsync(service);

        try
        {
            await service.TranslateOnceAsync(
                text,
                pair,
                new TranslationCallbacks
                {
                    OnProgress = t =>
                    {
                        TranslationText.Text = t;

                        // 每次分块都幂等复位，而不是"只复位一次"：
                        // 探测结果可能晚于首块到达，若只在首块复位，就会出现
                        // 「译文已在滚动、状态栏却写着模型加载中」的假象。
                        // 与主窗口 RunTranslationAsync 保持同一做法。
                        StatusText.Text = TranslatingLabel;
                    },
                    OnCompleted = m =>
                    {
                        StatusText.Text = $"{m.TokensPerSecond:F1} tok/s · {m.EvalCount} tok";
                        CopyButton.IsEnabled = true;
                    },
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户又划了新的词，本次作废
        }
        catch (Exception ex)
        {
            TranslationText.Text = $"翻译失败：{ex.Message}";
            StatusText.Text = string.Empty;
        }
    }

    /// <summary>
    /// 模型不在显存里时，把状态栏改成「模型加载中…」。
    /// </summary>
    /// <remarks>
    /// 与翻译并行发起，自身不阻塞请求。探测失败或译文已经开始产出都不改文案：
    /// 后者在首块内容到达之后才回来的情况下再改就是在说谎。
    /// </remarks>
    private async Task ApplyModelLoadHintAsync(TranslationService service)
    {
        try
        {
            if (await service.NeedsModelLoadAsync()
                && string.IsNullOrEmpty(TranslationText.Text))
            {
                StatusText.Text = BubbleViewModel.ModelLoadingLabel;
            }
        }
        catch (Exception ex)
        {
            // 探测失败不影响翻译，只留一条诊断线索
            AppLog.Trace($"探测模型驻留状态失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 以"内容过长"模式弹出：只给提示，不翻译。
    /// </summary>
    public void ShowTooLong(string message, int screenX, int screenY)
    {
        CancelRunning();

        _anchorX = screenX;
        _anchorY = screenY;
        _positioned = false;

        OriginalBox.Visibility = Visibility.Collapsed;
        LangText.Text = string.Empty;
        ChannelText.Text = string.Empty;
        SwapNotice.Visibility = Visibility.Collapsed;
        TranslationText.Text = message;
        StatusText.Text = "未翻译";
        CopyButton.IsEnabled = false;

        // 复位固定态时视觉也要跟着复位，否则按钮仍显示"已固定"的样式，
        // 而实际会随点击外部关闭——状态与外观必须一致。
        _pinned = false;
        PinButton.Appearance = ControlAppearance.Secondary;

        Opacity = 0;
        Show();
    }

    // ---------------------------------------------------------------- 定位

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        if (_positioned)
        {
            return;
        }

        _positioned = true;
        PositionNearCursor(handle, _anchorX, _anchorY);
        Opacity = 1;
        Activate();
    }

    /// <summary>
    /// 窗口尺寸变化后重新夹取位置。
    /// </summary>
    /// <remarks>
    /// 这个窗口是 <c>SizeToContent="Height"</c> 的：译文流式增长会让它以左上角为锚点
    /// 不断变高。若只在弹出时定位一次，光标靠近屏幕底部时窗口下半部分
    /// （含复制 / 固定按钮与状态栏）就会溢出工作区，用户看不到也点不到。
    /// </remarks>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_positioned || e.HeightChanged == false)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        PositionNearCursor(handle, _anchorX, _anchorY);
    }

    /// <summary>
    /// 把窗口放在光标右下侧；放不下就翻到另一侧，最后夹进显示器工作区。
    /// </summary>
    private void PositionNearCursor(IntPtr handle, int screenX, int screenY)
    {
        // 用物理像素算尺寸，避免 DPI 换算误差导致贴边
        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPhysical = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var heightPhysical = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);

        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var hasWorkArea = monitor != IntPtr.Zero
                          && NativeMethods.GetMonitorInfo(monitor, ref info);

        var left = screenX + 12;
        var top = screenY + 18;

        if (hasWorkArea)
        {
            var work = info.rcWork;

            // 右边放不下 → 翻到光标左侧
            if (left + widthPhysical > work.Right)
            {
                left = screenX - widthPhysical - 12;
            }

            // 下边放不下 → 翻到光标上方
            if (top + heightPhysical > work.Bottom)
            {
                top = screenY - heightPhysical - 12;
            }

            // 最后夹进工作区，保证任何情况下都不会跑到屏幕外
            left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - widthPhysical));
            top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - heightPhysical));
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            left,
            top,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    // ---------------------------------------------------------------- 关闭行为

    private void OnActivated(object? sender, EventArgs e) => CancelBlurTimer();

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_pinned)
        {
            return;
        }

        // 延迟关闭：拖动窗口会先失焦再得焦，立即关闭会导致窗口拖不动
        CancelBlurTimer();

        _blurTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = BlurCloseDelay,
        };
        _blurTimer.Tick += (_, _) =>
        {
            CancelBlurTimer();

            // 定时器期间可能又被激活了（拖动场景），再确认一次
            if (!_pinned && !IsActive)
            {
                Close();
            }
        };
        _blurTimer.Start();
    }

    private void CancelBlurTimer()
    {
        if (_blurTimer is null)
        {
            return;
        }

        _blurTimer.Stop();
        _blurTimer = null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>关闭按钮。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 用户点了浮窗外部：未固定则关闭。
    /// </summary>
    /// <remarks>
    /// 由全局鼠标钩子驱动，<b>不依赖窗口焦点</b>。
    /// 原来的"失焦自动关闭"依赖 <see cref="Window.Deactivated"/>，
    /// 而该事件只在窗口真正取得过焦点后才会触发——划词场景下浮窗常抢不到焦点
    /// （Windows 前台锁定），于是浮窗会一直留在屏幕上。
    /// </remarks>
    public void CloseIfNotPinned()
    {
        if (_pinned)
        {
            return;
        }

        Close();
    }

    /// <summary>
    /// 判断某个<b>屏幕物理坐标</b>是否落在浮窗内。
    /// </summary>
    /// <remarks>
    /// 句柄尚未创建时返回 true（当作"在里面"）：宁可暂时不关，
    /// 也不要因为拿不到窗口矩形就误关一个刚弹出的浮窗。
    /// </remarks>
    public bool ContainsScreenPoint(int screenX, int screenY)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return true;
        }

        if (!NativeMethods.GetWindowRect(handle, out var rect))
        {
            return true;
        }

        return screenX >= rect.Left && screenX <= rect.Right
               && screenY >= rect.Top && screenY <= rect.Bottom;
    }

    private void OnDragBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 鼠标已释放等情况下 DragMove 会抛异常，忽略
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;

        if (_pinned)
        {
            CancelBlurTimer();
            PinButton.Appearance = ControlAppearance.Primary;
            Topmost = true;
        }
        else
        {
            PinButton.Appearance = ControlAppearance.Secondary;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TranslationText.Text))
            {
                Clipboard.SetText(TranslationText.Text);
                StatusText.Text = "已复制";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>取消正在进行的翻译但不关窗。</summary>
    private void CancelRunning()
    {
        var previous = _cts;
        _cts = null;

        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略
        }

        previous.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelBlurTimer();
        CancelRunning();
        base.OnClosed(e);
    }
}
