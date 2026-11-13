using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NovaTouchUpdater;

internal static class WinRmRevocationStore
{
    internal static void Add(IntPtr store, byte[] encoded)
    {
        if (Contains(store, encoded)) return;
        // ADD_NEWER retains newer revocation information if another instance
        // has already installed it, instead of replacing it with an older CRL.
        if (!CertAddEncodedCRLToStore(store, 1, encoded, encoded.Length, 6, IntPtr.Zero) && Marshal.GetLastWin32Error() != unchecked((int)0x80092005))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Die signierte CA-Sperrliste konnte nicht gespeichert werden.");
    }

    internal static bool Contains(IntPtr store, byte[] encoded)
    {
        IntPtr context = IntPtr.Zero;
        while ((context = CertEnumCRLsInStore(store, context)) != IntPtr.Zero)
        {
            var entry = Marshal.PtrToStructure<CrlContext>(context);
            if (entry.Length != encoded.Length) continue;
            byte[] stored = new byte[entry.Length];
            Marshal.Copy(entry.Encoded, stored, 0, stored.Length);
            if (!stored.SequenceEqual(encoded)) continue;
            CertFreeCRLContext(context);
            return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CrlContext
    {
        internal uint Encoding;
        internal IntPtr Encoded;
        internal int Length;
        internal IntPtr Info;
        internal IntPtr Store;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertAddEncodedCRLToStore(IntPtr store, uint encoding, byte[] encoded, int length, uint disposition, IntPtr context);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertEnumCRLsInStore(IntPtr store, IntPtr previous);
    [DllImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertFreeCRLContext(IntPtr context);
}
