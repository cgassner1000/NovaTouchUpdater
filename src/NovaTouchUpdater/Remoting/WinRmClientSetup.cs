using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal static class WinRmClientSetup
{
    internal static string ValidateHost(string host)
    {
        host = host.Trim();
        if (!IPAddress.TryParse(host, out _) && (host.Length > 253 || host.Split('.').Any(label =>
            !Regex.IsMatch(label, @"\A[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?\z", RegexOptions.CultureInvariant))))
            throw new ArgumentException("Ungültiger Clientname für die WinRM-Einrichtung.");
        return host;
    }

    internal static string CreateScript(string host, bool https, int port, WinRmClientCertificate? certificate = null)
    {
        host = ValidateHost(host);
        if (!https) return ReadTemplate(false);
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (certificate is null) throw new InvalidOperationException("Für HTTPS zuerst das gemeinsame CA-Zertifikat und den Schlüssel laden.");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string parameters = "$ExpectedHost=" + Quote(host) + ";$Port=" + port + ";";
        parameters += "$CertificateData=" + Quote(Convert.ToBase64String(certificate.Pfx)) + ";$CertificatePassword=" + Quote(certificate.Password) +
                ";$IssuerData=" + Quote(Convert.ToBase64String(certificate.Issuer)) + ";$ExpectedThumbprint=" + Quote(certificate.Thumbprint) +
                ";$RevocationData=" + Quote(Convert.ToBase64String(certificate.RevocationList)) + ";";
        return parameters + Environment.NewLine + ReadTemplate(true);
    }

    private static string ReadTemplate(bool https)
    {
        using var stream = typeof(WinRmClientSetup).Assembly.GetManifestResourceStream("NovaTouchUpdater.WinRmClient" + (https ? "Https" : "Http") + ".ps1")
            ?? throw new InvalidOperationException("Der WinRM-Einrichtungscode fehlt.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return string.Join(Environment.NewLine, reader.ReadToEnd().Split('\n').Select(line => line.Trim()));
    }

    internal static string OneLine(string script, bool https)
    {
        // Run HTTP's single setup command in Windows PowerShell even when
        // pasted into PowerShell 7, so the Windows PowerShell endpoints are enabled.
        string command = https
            ? "powershell.exe -NoLogo -NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
            : "powershell.exe -NoLogo -NoProfile -Command { " + string.Join(" ", script.Split('\n')
                .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))) + " }";
        if (command.Length >= 32760) throw new InvalidOperationException("Der Clientcode überschreitet die maximale Windows-Befehlslänge.");
        return command;
    }
}
