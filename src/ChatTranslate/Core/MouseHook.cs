using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatTranslate.Core;

/// <summary>鼠标左键抬起事件。</summary>
/// <param name="ScreenX">光标屏幕坐标 X（物理像素）。</param>
/// <param name="ScreenY">光标屏幕坐标 Y（物理像素）。</param>
public readonly record struct MouseUpEvent(int ScreenX, int ScreenY);

/// <summary>
/// 全局低级鼠标钩子（WH_MOUSE_LL）。
///
/// <para>用于被动划词：不依赖快捷键，靠监听"鼠标左键抬起"来推断用户刚选完词。</para>
/// </summary>
/// <remarks>
/// <para><b>线程模型：必须在带消息泵的线程（WPF UI 线程）上安装。</b>
/// 低级钩子的回调由安装它的线程通过消息泵投递，没有消息泵就永远收不到。</para>
///
/// <para><b>回调必须极快</b>：Windows 对低级钩子有超时限制（默认几百毫秒），
/// 超时会把钩子摘掉，表现为"用一会儿就失灵"。因此这里只做取坐标 + 发事件，
/// 取词、翻译等耗时操作全部交给上层异步处理。</para>
///
/// <para><b>委托必须保持存活</b>：传给 SetWindowsHookEx 的委托若被 GC 回收，
/// 系统回调到已释放的地址会导致进程崩溃。这里用字段持有引用。</para>
/// </remarks>
public sealed class MouseHook : IDisposable
{
    /// <summary>钩子回调（在 UI 线程上触发）。</summary>
    public event Action<MouseUpEvent>? LeftButtonUp;

    // 必须用字段持有：局部变量会被 GC 回收，导致系统回调到已释放地址
    private readonly NativeMethods.LowLevelMouseProc _proc;

    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    /// <summary>钩子是否已安装。</summary>
    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>
    /// 安装钩子。必须从 UI 线程调用。
    /// </summary>
    /// <returns>失败原因；成功返回 null。</returns>
    public string? Install()
    {
        if (_disposed)
        {
            return "MouseHook 已释放";
        }

        if (_hookHandle != IntPtr.Zero)
        {
            return null;
        }

        // 低级钩子不需要注入目标进程，hMod 传当前模块句柄即可；
        // WH_MOUSE_LL 是全局钩子，dwThreadId 必须为 0
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            return $"安装鼠标钩子失败（Win32 错误 {code}）";
        }

        return null;
    }

    /// <summary>卸载钩子。</summary>
    public void Uninstall()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 表示系统要求必须直接透传，不得做任何处理
        if (nCode >= 0 && wParam == NativeMethods.WM_LBUTTONUP)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                LeftButtonUp?.Invoke(new MouseUpEvent(data.pt.X, data.pt.Y));
            }
            catch (Exception ex)
            {
                // 回调里抛异常会直接带崩进程，且这里的异常不影响系统输入，
                // 因此吞掉并只记录日志。
                Debug.WriteLine($"鼠标钩子回调异常：{ex}");
            }
        }

        // 无论如何都要传递给下一个钩子，否则会阻断系统输入
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
    }
}
