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

    private static readonly uint[] SafeFormats =
    [
        CF_TEXT, CF_OEMTEXT, CF_UNICODETEXT,      // 文本
        CF_BITMAP, CF_DIB, CF_DIBV5, CF_TIFF,     // 位图
        CF_WAVE,                                   // 音频
        CF_HDROP,                                  // 文件列表
    ];

    // 刻意排除 CF_PALETTE：它在剪贴板里的载荷是 HPALETTE（GDI 调色板句柄），
    // 不是 HGLOBAL 内存块，按字节块读写会写坏数据。调色板信息已由 CF_DIB / CF_DIBV5
    // 自带（DIB 内含颜色表），因此不单独处理它。

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

    /// <summary>
    /// 备份当前剪贴板的全部已知格式。
    /// </summary>
    /// <returns>
    /// 备份到的条目；<b>返回 null 表示读取剪贴板失败</b>（被其他进程占用等），
    /// 此时调用方不得用这个结果去还原——否则会把用户原有内容直接抹掉。
    /// 返回空列表表示剪贴板本来就是空的。
    /// </returns>
    public static List<ClipboardEntry>? Capture()
    {
        lock (Gate)
        {
            var list = new List<ClipboardEntry>();
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return null;
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
                        // 位图句柄归剪贴板所有，必须真正复制一份才能在其被清空后继续持有。
                        // ⚠️ 不能用 LR_COPYRETURNORG：该标志的含义是「可共享时直接返回原句柄」，
                        // 正好与需求相反。在 cxDesired/cyDesired = 0 时位图通常被原样返回，
                        // 于是我们手里拿的就是剪贴板自身的位图（系统所有）——一旦有进程清空剪贴板，
                        // 句柄失效；更糟的是还原时 SetClipboardData 移交所有权后，调用方紧接着
                        // Dispose→DeleteObject 会把系统仍在使用的位图删掉，粘贴时可能崩溃。
                        // fuFlags = 0 才是真正的独立复制。
                        var copy = NativeMethods.CopyImage(handle, IMAGE_BITMAP, 0, 0, 0);
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

    /// <summary>
    /// 把备份的内容写回剪贴板。
    /// </summary>
    /// <param name="entries">
    /// <see cref="Capture"/> 的返回值。<b>传 null 表示备份失败，此时<b>完全不动剪贴板</b></b>——
    /// 这是必须的：若先 EmptyClipboard 再发现无内容可回填，用户的剪贴板就被抹掉了。
    /// 传空列表表示剪贴板原本为空，可以安全清空。
    /// </param>
    public static void Restore(IReadOnlyList<ClipboardEntry>? entries)
    {
        // 备份失败 → 一个字节都不碰
        if (entries is null)
        {
            return;
        }

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
                        // 同样必须真复制：LR_COPYRETURNORG 可能原样返回 entry.BitmapHandle，
                        // 而该句柄经 SetClipboardData 后所有权已归系统，再被调用方 Dispose
                        // 就等于删除系统正在持有的位图。
                        var copy = NativeMethods.CopyImage(
                            entry.BitmapHandle, IMAGE_BITMAP, 0, 0, 0);
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
