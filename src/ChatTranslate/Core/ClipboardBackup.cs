using System.Runtime.InteropServices;

namespace ChatTranslate.Core;

/// <summary>剪贴板上的一项数据。</summary>
/// <param name="Format">剪贴板格式 ID。</param>
/// <param name="Data">HGLOBAL 类格式的原始字节；HBITMAP 类格式为 null。</param>
/// <param name="BitmapHandle">位图句柄（仅 CF_BITMAP）；其余为 IntPtr.Zero。</param>
public sealed record ClipboardEntry(uint Format, byte[]? Data, IntPtr BitmapHandle) : IDisposable
{
    public void Dispose()
    {
        if (BitmapHandle != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(BitmapHandle);
        }
    }
}

/// <summary>
/// 剪贴板备份 / 还原。
///
/// <para><b>只处理已知格式</b>（文本 / 位图 / 文件列表 / HTML / RTF），刻意<b>不</b>枚举
/// 未知的 vendor 私有格式。原因：Office 等应用会把指向自身内部数据的指针放进剪贴板，
/// 一旦被我们替换再还原，指针即悬空——崩溃的是对方进程，不是我们。
/// 宁可"行为怪异"，也不要制造对方崩溃。</para>
/// </summary>
public static class ClipboardBackup
{
    private const uint CF_TEXT = 1;
    private const uint CF_BITMAP = 2;
    private const uint CF_TIFF = 6;
    private const uint CF_OEMTEXT = 7;
    private const uint CF_DIB = 8;
    private const uint CF_PALETTE = 9;
    private const uint CF_WAVE = 12;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint CF_DIBV5 = 17;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint IMAGE_BITMAP = 0;
    private const uint LR_COPYRETURNORG = 0x00000004;

    private static readonly uint[] SafeFormats =
    [
        CF_TEXT, CF_OEMTEXT, CF_UNICODETEXT,      // 文本
        CF_BITMAP, CF_DIB, CF_DIBV5, CF_TIFF,     // 位图
        CF_PALETTE, CF_WAVE,                      // 与位图配对的调色板 / 音频
        CF_HDROP,                                 // 文件列表
    ];

    private static readonly string[] SafeNamedFormats =
    [
        "HTML Format",
        "Rich Text Format",
    ];

    private static readonly object Gate = new();

    /// <summary>剪贴板内容是否为空。</summary>
    public static bool IsEmpty()
    {
        return !NativeMethods.OpenClipboard(IntPtr.Zero)
            ? true
            : CloseAndReturn(NativeMethods.EnumClipboardFormats(0) == 0);

        static bool CloseAndReturn(bool result)
        {
            NativeMethods.CloseClipboard();
            return result;
        }
    }

    /// <summary>当前剪贴板序号，用于判断剪贴板是否被外部改动过。</summary>
    public static uint SequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    /// <summary>备份当前剪贴板的全部已知格式。</summary>
    public static List<ClipboardEntry> Capture()
    {
        lock (Gate)
        {
            var list = new List<ClipboardEntry>();
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return list;
            }

            try
            {
                var named = SafeNamedFormats
                    .Select(NativeMethods.RegisterClipboardFormat)
                    .Where(id => id != 0)
                    .ToHashSet();

                uint fmt = 0;
                while ((fmt = NativeMethods.EnumClipboardFormats(fmt)) != 0)
                {
                    if (!SafeFormats.Contains(fmt) && !named.Contains(fmt))
                    {
                        continue;
                    }

                    var handle = NativeMethods.GetClipboardData(fmt);
                    if (handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (fmt == CF_BITMAP)
                    {
                        // 位图句柄归剪贴板所有，必须复制一份才能在其被清空后继续持有
                        var copy = NativeMethods.CopyImage(handle, IMAGE_BITMAP, 0, 0, LR_COPYRETURNORG);
                        if (copy != IntPtr.Zero)
                        {
                            list.Add(new ClipboardEntry(fmt, null, copy));
                        }
                        continue;
                    }

                    var bytes = ReadGlobal(handle);
                    if (bytes is not null)
                    {
                        list.Add(new ClipboardEntry(fmt, bytes, IntPtr.Zero));
                    }
                }
            }
            catch
            {
                // 异常路径上必须显式释放已收集的位图句柄：
                // 调用方收到的是异常而非半成品列表，无从 Dispose，句柄就泄漏了。
                foreach (var entry in list)
                {
                    entry.Dispose();
                }

                throw;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            return list;
        }
    }

    /// <summary>把备份的内容写回剪贴板。传空列表则清空剪贴板。</summary>
    public static void Restore(IReadOnlyList<ClipboardEntry> entries)
    {
        lock (Gate)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return;
            }

            try
            {
                NativeMethods.EmptyClipboard();

                foreach (var entry in entries)
                {
                    if (entry.Format == CF_BITMAP && entry.BitmapHandle != IntPtr.Zero)
                    {
                        var copy = NativeMethods.CopyImage(
                            entry.BitmapHandle, IMAGE_BITMAP, 0, 0, LR_COPYRETURNORG);
                        if (copy != IntPtr.Zero)
                        {
                            NativeMethods.SetClipboardData(CF_BITMAP, copy);
                        }
                        continue;
                    }

                    if (entry.Data is null || entry.Data.Length == 0)
                    {
                        continue;
                    }

                    var size = (nuint)entry.Data.Length;
                    var handle = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, size);
                    if (handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    var ptr = NativeMethods.GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                    {
                        NativeMethods.GlobalFree(handle);
                        continue;
                    }

                    Marshal.Copy(entry.Data, 0, ptr, entry.Data.Length);
                    NativeMethods.GlobalUnlock(handle);

                    // SetClipboardData 成功后所有权移交系统，不可再 GlobalFree
                    if (NativeMethods.SetClipboardData(entry.Format, handle) == IntPtr.Zero)
                    {
                        NativeMethods.GlobalFree(handle);
                    }
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
    }

    /// <summary>读取剪贴板中的纯文本；没有文本返回 null。</summary>
    public static string? GetText()
    {
        lock (Gate)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return null;
            }

            try
            {
                var handle = NativeMethods.GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var ptr = NativeMethods.GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(ptr);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
    }

    private static byte[]? ReadGlobal(IntPtr handle)
    {
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = NativeMethods.GlobalSize(handle);
            if (size == 0)
            {
                return null;
            }

            var buffer = new byte[(int)size];
            Marshal.Copy(ptr, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }
}
