using System.Runtime.InteropServices;

namespace LanDisk.Core.Interop;

/// <summary>
/// Windows DPAPI, used to seal the local certificate authority's private key to the current user.
///
/// <para>The requirement is "the key at rest is only readable by this Windows user, and LanDisk never
/// has to invent its own key management". DPAPI is exactly that primitive: the ciphertext is bound to
/// the user's credentials, so copying the file to another account or machine yields nothing. It is
/// reached through P/Invoke rather than a package so that the product keeps its "no extra runtime
/// dependency" property and works from a fully offline build.</para>
///
/// <para>This protects the key <b>at rest</b>. It is not a claim that the key is unexportable: while
/// LanDisk is running, the key is necessarily in this process's memory to sign with. What it does
/// guarantee is that the bytes on disk are useless to anyone but this user.</para>
/// </summary>
internal static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Seals <paramref name="plaintext"/> to the current Windows user.</summary>
    public static byte[] Protect(byte[] plaintext, string description)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var input = new DataBlob { cbData = plaintext.Length, pbData = Marshal.AllocHGlobal(plaintext.Length) };
        try
        {
            Marshal.Copy(plaintext, 0, input.pbData, plaintext.Length);
            if (!CryptProtectData(ref input, description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out DataBlob output))
            {
                throw new InvalidOperationException(
                    $"DPAPI 保护失败（Win32 错误 {Marshal.GetLastWin32Error()}）。无法安全保存本地 CA 私钥。");
            }

            try { return CopyOut(output); }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
            {
                // Zero the plaintext copy before releasing it; the managed array is the caller's.
                for (int i = 0; i < plaintext.Length; i++) Marshal.WriteByte(input.pbData, i, 0);
                Marshal.FreeHGlobal(input.pbData);
            }
        }
    }

    /// <summary>Opens a blob produced by <see cref="Protect"/> for the current user.</summary>
    public static byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var input = new DataBlob { cbData = ciphertext.Length, pbData = Marshal.AllocHGlobal(ciphertext.Length) };
        try
        {
            Marshal.Copy(ciphertext, 0, input.pbData, ciphertext.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out DataBlob output))
            {
                throw new InvalidOperationException(
                    $"DPAPI 解保护失败（Win32 错误 {Marshal.GetLastWin32Error()}）。该文件可能属于其它 Windows 用户。");
            }

            try { return CopyOut(output); }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    private static byte[] CopyOut(DataBlob blob)
    {
        var result = new byte[blob.cbData];
        if (blob.cbData > 0) Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }
}
