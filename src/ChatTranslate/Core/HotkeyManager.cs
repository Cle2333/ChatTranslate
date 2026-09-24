using System.Diagnostics;
using System.Windows.Interop;

namespace ChatTranslate.Core;

/// <summary>全局热键的修饰键组合。</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,

    /// <summary>按住不放时不重复触发。</summary>
    NoRepeat = 0x4000,
}

/// <summary>
/// 全局热键管理。
///
/// <para><b>线程模型：必须在带消息泵的 STA（即 WPF UI）线程上创建与调用。</b>
/// <c>HwndSource</c> 若在无消息泵的线程创建，<c>WM_HOTKEY</c> 永远不会到达，
/// 表现为"注册成功但热键没反应"。内部状态（<c>_handlers</c>/<c>_nextId</c>）无锁，
/// 因此不支持跨线程并发调用。</para>
///
/// <para>WPF 没有内置的全局热键 API，需要注册 Win32 <c>RegisterHotKey</c> 并拦截
/// <c>WM_HOTKEY</c> 消息。这里用一个隐藏的消息窗口承载消息，避免依赖主窗口的生命周期。</para>
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    /// <summary>HWND_MESSAGE：仅用于接收消息的窗口，不显示、不参与 Z 序。</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _messageSource;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>热键被按下时触发（在 UI 线程上回调）。</summary>
    public event Action<int>? HotkeyPressed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ChatTranslate.HotkeySink")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);
    }

    /// <summary>
    /// 注册一个全局热键。
    /// </summary>
    /// <param name="modifiers">修饰键组合。</param>
    /// <param name="virtualKey">主键的虚拟键码。</param>
    /// <param name="description">用于日志与错误信息的说明。</param>
    /// <param name="error">注册失败原因。</param>
    /// <returns>成功返回热键 ID，失败返回 -1。</returns>
    public int Register(
        HotkeyModifiers modifiers,
        uint virtualKey,
        string description,
        out string? error)
    {
        error = null;

        if (_disposed)
        {
            error = "HotkeyManager 已释放";
            return -1;
        }

        var id = _nextId++;

        // 加上 NoRepeat，避免按住不放时反复触发
        var flags = (uint)(modifiers | HotkeyModifiers.NoRepeat);

        if (!NativeMethods.RegisterHotKey(_messageSource.Handle, id, flags, virtualKey))
        {
            var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = code switch
            {
                1409 => $"热键 {description} 已被其他程序占用",
                _ => $"注册热键 {description} 失败（Win32 错误 {code}）",
            };
            return -1;
        }

        _handlers[id] = () => HotkeyPressed?.Invoke(id);
        return id;
    }

    /// <summary>注销指定热键。</summary>
    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_messageSource.Handle, id);
        }
    }

    /// <summary>注销全部热键。</summary>
    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys.ToList())
        {
            Unregister(id);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        if (_handlers.TryGetValue(id, out var handler))
        {
            handled = true;

            // 这里跑在 WPF 消息泵内：订阅者一旦抛异常，会穿过 HwndSource 的钩子进入
            // 消息循环成为未处理异常，直接把进程带崩。必须隔离。
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"热键 {id} 回调异常：{ex}");
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _messageSource.RemoveHook(WndProc);
        _messageSource.Dispose();
    }
}
