using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ChatTranslate.Core;

/// <summary>
/// 用 Windows DPAPI 保护落盘的小型密钥。
/// </summary>
/// <remarks>
/// <para>API Key 不能以明文写进 <c>config.json</c>：配置是用户可见、可备份、也可能被同步盘
/// 上传的文件。这里用当前用户范围的 DPAPI 加密，密文只能在同一 Windows 用户下解开。</para>
///
/// <para><b>失败不把明文带出去</b>：加密失败由调用方提示；解密失败返回 <c>null</c>，
/// 界面显示「未设置」，翻译时报「无法读取 API Key」，而不是把密文或异常原文当密钥发送。</para>
/// </remarks>
public static class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>加密为 Base64 密文；空串原样返回。</summary>
    public static string Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            var encrypted = Transform(plaintext, protect: true);
            if (encrypted is null)
            {
                throw new InvalidOperationException(
                    $"Windows DPAPI 加密失败（错误码 {Marshal.GetLastWin32Error()}）");
            }

            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>解开 Base64 密文；空值、格式错误或非本用户创建的密文返回 null。</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
        {
            return null;
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        var plaintext = Transform(encrypted, protect: false);
        if (plaintext is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[]? Transform(byte[] data, bool protect)
    {
        var input = new DataBlob
        {
            Size = data.Length,
            Data = Marshal.AllocHGlobal(data.Length),
        };
        var output = default(DataBlob);

        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);

            var ok = protect
                ? CryptProtectData(
                    ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(
                    ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);

            if (!ok)
            {
                return null;
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);

            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }
}
