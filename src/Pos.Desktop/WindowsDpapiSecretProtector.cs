using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ILocalSecretProtector
{
    private const int UiForbidden = 0x1;

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(byte[] protectedData) => Transform(protectedData, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var outputBlob = default(DataBlob);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob { Length = input.Length, Data = inputPointer };
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no pudo proteger o recuperar la configuración local.");
            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
            if (outputBlob.Data != IntPtr.Zero) _ = LocalFree(outputBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
