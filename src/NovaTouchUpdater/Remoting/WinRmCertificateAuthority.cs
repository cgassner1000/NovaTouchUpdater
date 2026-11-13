using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NovaTouchUpdater;

// The issuer key stays in the updater process. Only a host-specific key is exported.
internal sealed class WinRmCertificateAuthority : IDisposable
{
    private readonly X509Certificate2 issuer;
    private readonly object sync = new();
    private byte[]? revocationList;
    private DateTime revocationDay;
    internal string Thumbprint => issuer.Thumbprint;
    internal string Subject => issuer.Subject;

    private WinRmCertificateAuthority(X509Certificate2 issuer) => this.issuer = issuer;

    internal static WinRmCertificateAuthority Load(string certificatePath, string keyPath)
        => Validate(X509Certificate2.CreateFromPemFile(certificatePath, keyPath));

    internal static WinRmCertificateAuthority LoadDefault()
        => Validate(X509Certificate2.CreateFromPem(ReadEmbeddedPem("novacom_default_cert.pem"), ReadEmbeddedPem("novacom_default_key.pem")));

    private static string ReadEmbeddedPem(string filename)
    {
        using var stream = typeof(WinRmCertificateAuthority).Assembly.GetManifestResourceStream("NovaTouchUpdater." + filename)
            ?? throw new InvalidOperationException("Das eingebaute HTTPS-Standardzertifikat fehlt.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static WinRmCertificateAuthority Validate(X509Certificate2 certificate)
    {
        try
        {
            if (!certificate.HasPrivateKey) throw new InvalidDataException("Der private Zertifikatsschlüssel fehlt.");
            if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true })
                throw new InvalidDataException("Das gemeinsame Zertifikat muss eine CA sein, damit passende Clientzertifikate ausgestellt werden können.");
            if (certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage &&
                (usage.KeyUsages & (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign)) != (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign))
                throw new InvalidDataException("Das CA-Zertifikat muss Zertifikatsausstellung und CRL-Signatur erlauben.");
            if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(1))
                throw new InvalidDataException("Das CA-Zertifikat ist noch nicht gültig oder läuft bereits ab.");
            return new(certificate);
        }
        catch { certificate.Dispose(); throw; }
    }

    internal bool IsTrusted()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, issuer.Thumbprint, false).Count == 0) return false;
        using var crls = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
        crls.Open(OpenFlags.ReadOnly);
        return WinRmRevocationStore.Contains(crls.StoreHandle, RevocationList());
    }

    internal void Trust()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        using var publicCertificate = new X509Certificate2(issuer.RawData);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, issuer.Thumbprint, false).Count == 0) store.Add(publicCertificate);
        using var crls = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
        crls.Open(OpenFlags.ReadWrite);
        WinRmRevocationStore.Add(crls.StoreHandle, RevocationList());
    }

    // This local CA has no CRL distribution endpoint. Install its signed CRL with
    // the public CA so Windows can keep normal revocation checking enabled.
    internal byte[] RevocationList()
    {
        lock (sync)
        {
            DateTimeOffset from = new(DateTime.UtcNow.Date, TimeSpan.Zero);
            if (revocationList is not null && revocationDay == from.UtcDateTime) return revocationList;
            DateTimeOffset until = from.AddYears(1).AddDays(1);
            if (until.UtcDateTime > issuer.NotAfter.ToUniversalTime()) until = issuer.NotAfter.ToUniversalTime();
            revocationDay = from.UtcDateTime;
            if (from.UtcDateTime < issuer.NotBefore.ToUniversalTime()) from = issuer.NotBefore.ToUniversalTime();
            return revocationList = new CertificateRevocationListBuilder().Build(issuer, new BigInteger(revocationDay.Ticks), until,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, from);
        }
    }

    internal WinRmClientCertificate Issue(string host)
    {
        host = WinRmClientSetup.ValidateHost(host);
        lock (sync)
        {
            using var key = RSA.Create(2048);
            string commonName = IPAddress.TryParse(host, out _) ? host : host.Split('.')[0];
            var request = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            var names = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address);
            else
            {
                names.AddDnsName(host);
                if (!commonName.Equals(host, StringComparison.OrdinalIgnoreCase)) names.AddDnsName(commonName);
            }
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset until = now.AddYears(1);
            if (until.UtcDateTime > issuer.NotAfter.ToUniversalTime()) until = issuer.NotAfter.ToUniversalTime();
            byte[] serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            DateTimeOffset from = now.AddMinutes(-5);
            if (from.UtcDateTime < issuer.NotBefore.ToUniversalTime()) from = issuer.NotBefore.ToUniversalTime();
            using var signed = request.Create(issuer, from, until, serial);
            using var leaf = signed.CopyWithPrivateKey(key);
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            return new(leaf.Export(X509ContentType.Pfx, password), password, issuer.RawData.ToArray(), leaf.Thumbprint, RevocationList());
        }
    }

    public void Dispose() { lock (sync) issuer.Dispose(); }
}

internal sealed record WinRmClientCertificate(byte[] Pfx, string Password, byte[] Issuer, string Thumbprint, byte[] RevocationList) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(Pfx);
}
