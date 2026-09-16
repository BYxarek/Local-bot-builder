using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NativeBot.Storage;

public static class SecretProtector
{
    public static byte[] Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return Transform(Encoding.UTF8.GetBytes(secret), protect: true);
    }

    public static string Unprotect(byte[] protectedSecret) => Encoding.UTF8.GetString(Transform(protectedSecret, protect: false));

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, inputPointer, input.Length);
        var inputBlob = new DataBlob(input.Length, inputPointer);
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
                return result;
            }
            finally { LocalFree(outputBlob.Data); }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
            Array.Clear(input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int size, IntPtr data)
    {
        public int Size = size;
        public IntPtr Data = data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
