using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void WinRmRevocationTests(X509Certificate2 issuer, X509Certificate2 leaf, byte[] crl)
    {
        // Memory provider only: no Windows trust store is opened or changed.
        IntPtr store = CertOpenStore(new IntPtr(2), 0, IntPtr.Zero, 0x2000, IntPtr.Zero);
        Assert(store != IntPtr.Zero, "the revocation fixture uses an isolated memory certificate store");
        try
        {
            byte[] encodedIssuer = issuer.RawData;
            Assert(CertAddEncodedCertificateToStore(store, 1, encodedIssuer, encodedIssuer.Length, 4, IntPtr.Zero), "the public test CA can be added to the memory store");
            uint before = ChainErrors(store, leaf);
            Assert((before & 0x40) != 0, "without a CRL Windows reports unknown certificate revocation status");
            WinRmRevocationStore.Add(store, crl);
            WinRmRevocationStore.Add(store, crl);
            Assert(WinRmRevocationStore.Contains(store, crl), "the signed CA revocation list can be installed idempotently");
            uint after = ChainErrors(store, leaf);
            Assert(after == 0, "with the signed CRL Windows validates the complete chain and revocation status (status " + after.ToString("X") + ")");
        }
        finally { CertCloseStore(store, 0); }
    }

    private static uint ChainErrors(IntPtr store, X509Certificate2 leaf)
    {
        var parameters = new ChainParameters { Size = Marshal.SizeOf<ChainParameters>() };
        var config = new ChainEngineConfig { Size = Marshal.SizeOf<ChainEngineConfig>(), ExclusiveRoot = store };
        Assert(CertCreateCertificateChainEngine(ref config, out var engine), "the test root is trusted only inside an isolated chain engine");
        try
        {
            Assert(CertGetCertificateChain(engine, leaf.Handle, IntPtr.Zero, store, ref parameters, 0x40000004, IntPtr.Zero, out var context),
                "Windows can build the chain using only the isolated store and cached revocation data");
            try { return Marshal.PtrToStructure<ChainHeader>(context).ErrorStatus; }
            finally { CertFreeCertificateChain(context); }
        }
        finally { CertFreeCertificateChainEngine(engine); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChainHeader { internal uint Size, ErrorStatus, InfoStatus; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UsageMatch { internal uint Type, Count; internal IntPtr Identifiers; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ChainParameters
    {
        internal int Size;
        internal UsageMatch RequestedUsage, RequestedIssuancePolicy;
        internal uint UrlTimeout;
        [MarshalAs(UnmanagedType.Bool)] internal bool CheckFreshness;
        internal uint FreshnessTime;
        internal IntPtr CacheResync, StrongSign;
        internal uint StrongSignFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ChainEngineConfig
    {
        internal int Size;
        internal IntPtr RestrictedRoot, RestrictedTrust, RestrictedOther;
        internal uint AdditionalStoreCount;
        internal IntPtr AdditionalStores;
        internal uint Flags, UrlTimeout, MaxCached, CycleModulus;
        internal IntPtr ExclusiveRoot, ExclusivePeople;
        internal uint ExclusiveFlags;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCreateCertificateChainEngine(ref ChainEngineConfig config, out IntPtr engine);
    [DllImport("crypt32.dll")]
    private static extern void CertFreeCertificateChainEngine(IntPtr engine);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertOpenStore(IntPtr provider, uint encoding, IntPtr cryptProvider, uint flags, IntPtr parameter);
    [DllImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(IntPtr store, uint flags);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertAddEncodedCertificateToStore(IntPtr store, uint encoding, byte[] encoded, int length, uint disposition, IntPtr context);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertGetCertificateChain(IntPtr engine, IntPtr certificate, IntPtr time, IntPtr additionalStore, ref ChainParameters parameters,
        uint flags, IntPtr reserved, out IntPtr chainContext);
    [DllImport("crypt32.dll")]
    private static extern void CertFreeCertificateChain(IntPtr context);
}
