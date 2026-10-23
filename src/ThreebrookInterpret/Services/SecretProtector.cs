using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 用 Windows DPAPI（当前用户）加密配置里的 API Key、密钥：只有同一 Windows 账户在本机能解开，
/// 配置文件被拷走也读不出明文。直接调用 crypt32，不引入额外的包。
/// 加密后的值以 "dpapi:" 开头；不带前缀的旧明文照常读取，下次保存时自动加密。
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    // 沿用改名前的值：改了会让已经加密保存的 Key 解不开
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ThreeBrookTrans.settings");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(Prefix, StringComparison.Ordinal))
            return plain;
        var encrypted = Crypt(Encoding.UTF8.GetBytes(plain), protect: true);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    /// <summary>解不开（换了电脑或 Windows 账户）时返回空，并记日志，需要重新填写。</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        try
        {
            var plain = Crypt(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicFailure)
        {
            DiagnosticLog.Write("secret could not be decrypted (different user or machine?): " + ex.Message, "settings.log");
            return "";
        }
    }

    private sealed class CryptographicFailure : Exception
    {
        public CryptographicFailure(int error) : base($"DPAPI error {error}") { }
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var entropyBlob = new DataBlob();
        var outBlob = new DataBlob();
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inBlob = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok)
                throw new CryptographicFailure(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
            if (outBlob.pbData != IntPtr.Zero)
                LocalFree(outBlob.pbData);
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
