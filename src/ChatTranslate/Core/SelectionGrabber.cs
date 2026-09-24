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

    /// <summary>只走 UIA 通道。</summary>
    public static string? TryUia()
    {
        string? result = null;

        // 目标应用不响应 UIA 时会长时间挂起，因此放到后台任务并限时
        var task = Task.Run(() =>
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is null)
                {
                    return;
                }

                if (TryReadTextPattern(focused, out var own))
                {
                    result = own;
                    return;
                }

                // 焦点元素本身没有文本内容时，往下找第一个支持 TextPattern 的元素
                var condition = new PropertyCondition(
                    AutomationElement.IsTextPatternAvailableProperty, true);
                var element = focused.FindFirst(TreeScope.Descendants, condition);
                if (element is not null && TryReadTextPattern(element, out var nested))
                {
                    result = nested;
                }
            }
            catch
            {
                // UIA 在部分应用上会抛各种 COM 异常，一律视为"取不到"
            }
        });

        return task.Wait(UiaTimeoutMs) ? result : null;
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
        var sequenceBefore = ClipboardBackup.SequenceNumber();
        List<ClipboardEntry> backup;

        try
        {
            backup = ClipboardBackup.Capture();
        }
        catch (Exception ex)
        {
            return (null, $"剪贴板备份失败：{ex.Message}");
        }

        try
        {
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

    /// <summary>发送 Ctrl+C。发送前等待修饰键释放，避免变成 Ctrl+Shift+C 之类的组合。</summary>
    private static bool SendCtrlC()
    {
        WaitForModifiersReleased(1000);

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

    private static void WaitForModifiersReleased(int timeoutMs)
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
                return;
            }

            Thread.Sleep(20);
        }
    }
}
