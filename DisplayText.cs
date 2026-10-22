using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

/// <summary>Verbirgt technische Prüfsummenwerte in GUI, Hostprotokoll und HTML-Bericht.</summary>
internal static class DisplayText
{
    public static string DotNet48(bool? installed) => installed switch { true => "✓", false => "✗", null => "–" };

    public static string DotNet48Description(bool? installed) => installed switch
    {
        true => ".NET Framework 4.8 oder neuer installiert",
        false => ".NET Framework 4.8 oder neuer nicht installiert",
        null => ".NET Framework 4.8 noch nicht geprüft oder Prüfung fehlgeschlagen"
    };

    private static readonly Regex NamedHashes = new(
        @"\b(?:PaymentSha256|FiscalSha256)\s*[:=]\s*[^\s;,|]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HexHash = new(
        @"\b[0-9a-f]{64}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GitBuildHash = new(
        @"\+[0-9a-f]{7,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VersionNumber = new(
        @"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);
    private static readonly Regex HashName = new(
        @"\bSHA[\s-]?256\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Clean(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        string text = NamedHashes.Replace(message, "");
        text = HexHash.Replace(text, "[Prüfsumme ausgeblendet]");
        text = GitBuildHash.Replace(text, "");
        return HashName.Replace(text, "Prüfsumme");
    }

    /// <summary>Behält die ursprüngliche Fehler-/Information erhalten und zeigt zusätzlich den aktuellen Schritt an.</summary>
    public static string Information(string? phase, string? error)
    {
        string currentStep = Clean(phase).Trim();
        string detail = Clean(error).Trim();
        if (currentStep.Length == 0) return detail;
        if (detail.Length == 0 || currentStep.Equals(detail, StringComparison.OrdinalIgnoreCase)) return currentStep;
        return currentStep + "  |  " + detail;
    }

    public static string Version(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        Match match = VersionNumber.Match(value);
        return match.Success ? match.Value : Clean(value.Trim());
    }

    public static string ServiceVersions(IEnumerable<ServiceInfo> services)
    {
        ServiceInfo[] installed = services.ToArray();
        if (installed.Length == 0) return "Nicht installiert";
        string[] versions = installed.Select(s => Version(
                string.IsNullOrWhiteSpace(s.ProductVersion) ? s.FileVersion : s.ProductVersion))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray();
        return versions.Length > 0 ? string.Join(", ", versions) : "–";
    }
}
