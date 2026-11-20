using System.Net;
using System.Text;
using System.Text.Json;
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

    internal static string CreateCommand(string host, bool https, int port, WinRmClientCertificate? certificate = null)
    {
        host = ValidateHost(host);
        string transportSetup = Environment.NewLine + ReadTemplate("Transport");
        if (!https) return HttpOneLine(ReadTemplate("Http") + transportSetup);
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (certificate is null) throw new InvalidOperationException("Für HTTPS zuerst das gemeinsame CA-Zertifikat und den Schlüssel laden.");
        // Send host-specific data on stdin so the PFX is not encoded twice in the process arguments.
        string input = JsonSerializer.Serialize(new { ExpectedHost = host, Port = port, CertificateData = certificate.Pfx, CertificatePassword = certificate.Password,
            IssuerData = certificate.Issuer, ExpectedThumbprint = certificate.Thumbprint, RevocationData = certificate.RevocationList });
        string script = ReadTemplate("Https") + transportSetup;
        string command = "'" + input.Replace("'", "''") + "' | powershell.exe -NoLogo -NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (command.Length >= 32760) throw new InvalidOperationException("Der Clientcode überschreitet die maximale Windows-Befehlslänge.");
        return command;
    }

    private static string ReadTemplate(string name)
    {
        using var stream = typeof(WinRmClientSetup).Assembly.GetManifestResourceStream("NovaTouchUpdater.WinRmClient" + name + ".ps1")
            ?? throw new InvalidOperationException("Der WinRM-Einrichtungscode fehlt.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return string.Join(Environment.NewLine, reader.ReadToEnd().Split('\n').Select(line => line.Trim()));
    }

    internal static string HttpOneLine(string script)
    {
        // Run the copied setup in Windows PowerShell even when
        // pasted into PowerShell 7, so the Windows PowerShell endpoints are enabled.
        string command = "powershell.exe -NoLogo -NoProfile -Command { " + string.Join(" ", script.Split('\n')
                .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))) + " }";
        if (command.Length >= 32760) throw new InvalidOperationException("Der Clientcode überschreitet die maximale Windows-Befehlslänge.");
        return command;
    }
}
