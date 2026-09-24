using System.Diagnostics;
using System.Windows.Automation;

namespace ChatTranslate.Core;

/// <summary>取词所用的通道。</summary>
public enum SelectionMethod
{
    /// <summary>UI Automation TextPattern —— 不碰剪贴板。</summary>
    Uia,

    /// <summary>模拟 Ctrl+C 借道剪贴板（会短暂占用剪贴板，用完立即还原）。</summary>
    Clipboard,

    /// <summary>两条路都没取到。</summary>
    Failed,
}

/// <param name="Text">取到的文本；失败为 null。</param>
/// <param name="Method">实际生效的通道。</param>
/// <param name="Note">失败或降级原因，用于自检面板展示。</param>
public sealed record SelectionResult(string? Text, SelectionMethod Method, string? Note);

/// <summary>
/// 划词取词。
///
/// <para>双路策略：<b>先试 UIA</b>（不产生副作用），<b>失败才回退模拟 Ctrl+C</b>。</para>
///
/// <para>UIA 的 TextPattern 并非所有应用都实现（Chrome / Electron 默认不暴露），
/// 因此回退路径是必需的；但回退路径会占用剪贴板，所以必须做完整备份还原。</para>
/// </summary>
public static class SelectionGrabber
{
    /// <summary>UIA 单次尝试的超时。目标应用不响应 UIA 时会挂起，必须设上限。</summary>
    private const int UiaTimeoutMs = 400;

    /// <summary>模拟 Ctrl+C 后等待剪贴板变化的超时。</summary>
    private const int ClipboardTimeoutMs = 600;

    /// <summary>等待修饰键释放的上限；超过则放弃本次取词。</summary>
    private const int ModifierWaitMs = 1000;

    /// <summary>
    /// 取当前选中文本。必须在后台线程调用（会阻塞等待）。
    /// </summary>
    public static SelectionResult Grab()
    {
        var uiaText = TryUia();
        if (!string.IsNullOrWhiteSpace(uiaText))
        {
            return new SelectionResult(uiaText, SelectionMethod.Uia, null);
        }

        var (clipText, note) = TryClipboard();
        if (!string.IsNullOrWhiteSpace(clipText))
        {
            return new SelectionResult(clipText, SelectionMethod.Clipboard, null);
        }

        return new SelectionResult(null, SelectionMethod.Failed, note ?? "UIA 未取到文本，Ctrl+C 回退也未取到");
    }

    /// <summary>只走 UIA 通道；超时或失败返回 null。</summary>
    public static string? TryUia()
    {
        string? result = null;

        // 目标应用不响应 UIA 时该调用会长时间甚至永久阻塞。
        // 刻意不用 Task.Run：被卡住的线程池线程会持续占用线程池，反复触发取词时
        // 累积起来会拖垮整个池。改用专用后台线程——即使卡死也只是多一个空转线程，
        // 且不会阻止进程退出。
        var worker = new Thread(() =>
        {
            try
            {
                result = UiaCore();
            }
            catch
            {
                // UIA 在部分应用上会抛各种 COM 异常，一律视为"取不到"
            }
        })
        {
            IsBackground = true,
            Name = "ChatTranslate.UiaGrab",
        };

        worker.Start();

        return worker.Join(UiaTimeoutMs) ? result : null;
    }

    private static string? UiaCore()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is null)
        {
            return null;
        }

        if (TryReadTextPattern(focused, out var own))
        {
            return own;
        }

        // 焦点元素本身没有文本内容时，往下找第一个支持 TextPattern 的元素
        var condition = new PropertyCondition(
            AutomationElement.IsTextPatternAvailableProperty, true);
        var element = focused.FindFirst(TreeScope.Descendants, condition);
        return element is not null && TryReadTextPattern(element, out var nested) ? nested : null;
    }

    private static bool TryReadTextPattern(AutomationElement element, out string? text)
    {
        text = null;

        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
            || patternObject is not TextPattern textPattern
            || textPattern.SupportedTextSelection == SupportedTextSelection.None)
        {
            return false;
        }

        var ranges = textPattern.GetSelection();
        if (ranges.Length == 0)
        {
            return false;
        }

        var selected = ranges[0].GetText(-1);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return false;
        }

        text = selected;
        return true;
    }

    /// <summary>
    /// 模拟 Ctrl+C 借道剪贴板取词，无论成功与否都还原剪贴板原内容。
    /// </summary>
    private static (string? Text, string? Note) TryClipboard()
    {
        List<ClipboardEntry>? backup;

        try
        {
            backup = ClipboardBackup.Capture();
        }
        catch (Exception ex)
        {
            return (null, $"剪贴板备份失败：{ex.Message}");
        }

        // 备份失败（剪贴板被其他进程占用）时必须放弃本次取词。
        // 若继续模拟 Ctrl+C，用户剪贴板里的内容会被覆盖且无法还原——那是不可逆的数据丢失，
        // 而失败只是让用户重试一次。两害相权，取后者。
        if (backup is null)
        {
            return (null, "剪贴板被其他进程占用，无法备份，已放弃本次取词以保护剪贴板内容");
        }

        try
        {
            // 先等修饰键松开、再采样序号。
            // 若在备份之前就采样，备份耗时（复制大位图可能数十毫秒）加上等待修饰键的
            // 这段时间里，任何一次外部剪贴板改动都会让序号比对失效，从而把改动后的
            // 内容误判成"复制成功"的结果——正是本方法要避免的误报。
            if (!WaitForModifiersReleased(ModifierWaitMs))
            {
                return (null, "修饰键长时间未释放，已放弃本次取词以免发出错误组合键");
            }

            var sequenceBefore = ClipboardBackup.SequenceNumber();

            if (!SendCtrlC())
            {
                return (null, "SendInput 发送 Ctrl+C 失败");
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ClipboardTimeoutMs)
            {
                if (ClipboardBackup.SequenceNumber() != sequenceBefore)
                {
                    // 剪贴板确实被改写了，说明应用响应了复制
                    var text = ClipboardBackup.GetText();
                    return string.IsNullOrWhiteSpace(text)
                        ? (null, "剪贴板已变化但内容不是文本")
                        : (text, null);
                }

                Thread.Sleep(15);
            }

            // 剪贴板序号没变 = 目标应用没有响应复制（可能未选中内容）
            // 注意：绝不能在此返回剪贴板旧内容，否则会把上次复制的东西当选中文本翻译
            return (null, "Ctrl+C 后剪贴板未变化，目标应用可能未选中文本或无响应");
        }
        finally
        {
            try
            {
                ClipboardBackup.Restore(backup);
            }
            catch
            {
                // 还原失败不应影响取词结果，静默
            }

            foreach (var entry in backup)
            {
                entry.Dispose();
            }
        }
    }

    /// <summary>
    /// 发送 Ctrl+C。调用方必须先经 <see cref="WaitForModifiersReleased"/> 确认修饰键已释放，
    /// 否则合成出的会变成 Ctrl+Shift+C 一类的组合键，取回的文本不可信。
    /// </summary>
    private static bool SendCtrlC()
    {
        var inputs = new NativeMethods.INPUT[4];

        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = NativeMethods.VK_CONTROL;

        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = NativeMethods.VK_C;

        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = NativeMethods.VK_C;
        inputs[2].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].u.ki.wVk = NativeMethods.VK_CONTROL;
        inputs[3].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        var sent = NativeMethods.SendInput(
            4, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());

        return sent == 4;
    }

    /// <summary>等待修饰键全部释放；超时返回 false，调用方此时不应发送 Ctrl+C。</summary>
    private static bool WaitForModifiersReleased(int timeoutMs)
    {
        int[] modifiers =
        [
            NativeMethods.VK_CONTROL, NativeMethods.VK_SHIFT,
            NativeMethods.VK_MENU, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN,
        ];

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var anyDown = modifiers.Any(vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0);
            if (!anyDown)
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }
}
