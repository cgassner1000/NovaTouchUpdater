using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal sealed record MsiMetadata(UpdateComponents Component, string ProductName, string ProductVersion,
    string ProductCode, string UpgradeCode, string[] ExternalCabinets, string MsiProductVersion);

// Read MSI tables without installing the product or querying Win32_Product.
internal static class MsiPackageReader
{
    private static readonly Regex FourPartVersion = new(@"(?<![\d.])\d+\.\d+\.\d+\.\d+(?![\d.])", RegexOptions.CultureInvariant);
    internal static UpdateComponents ComponentForName(string name) => System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ").ToUpperInvariant() switch
    {
        "NOVATOUCH POS" => UpdateComponents.Pos,
        "NOVATOUCH CONTROL" => UpdateComponents.Ntc,
        "NOVATOUCH VOUCHER" => UpdateComponents.Ntv,
        "NOVATOUCH ORDERMAN SOL" => UpdateComponents.Oman,
        _ => UpdateComponents.None
    };

    internal static MsiMetadata Read(string path, params string[] originalNames)
    {
        Check(MsiOpenDatabase(Path.GetFullPath(path), IntPtr.Zero, out uint database), "MSI öffnen");
        try
        {
            var properties = Rows(database, "SELECT `Property`, `Value` FROM `Property`", 2)
                .ToDictionary(row => row[0], row => row[1], StringComparer.OrdinalIgnoreCase);
            string name = properties.GetValueOrDefault("ProductName", "").Trim();
            string version = properties.GetValueOrDefault("ProductVersion", "").Trim();
            string productCode = properties.GetValueOrDefault("ProductCode", "");
            if (!Version.TryParse(version, out _) || !Guid.TryParse(productCode, out _))
                throw new InvalidDataException($"MSI enthält keine gültige ProductVersion / ProductCode: {path}");
            string[] cabinets = Rows(database, "SELECT `Cabinet` FROM `Media`", 1).Select(row => row[0])
                .Where(value => value.Length > 0 && !value.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            UpdateComponents component = ComponentForName(name);
            return new(component, name, ReleaseVersion(database, component, version, originalNames.Append(path)), productCode,
                properties.GetValueOrDefault("UpgradeCode", ""), cabinets, version);
        }
        finally { MsiCloseHandle(database); }
    }

    private static string ReleaseVersion(uint database, UpdateComponents component, string productVersion, IEnumerable<string> names)
    {
        Version product = Version.Parse(productVersion);
        string normalized = new Version(product.Major, product.Minor, Math.Max(0, product.Build), Math.Max(0, product.Revision)).ToString(4);
        if (product.Revision >= 0 || component == UpdateComponents.None) return normalized;
        string primaryFile = component switch
        {
            UpdateComponents.Ntc => "Nov.WaWi.exe", UpdateComponents.Pos => "Nov.NT.Communication.Devices.dll",
            UpdateComponents.Ntv => "Nov.Gutscheinverwaltung.exe", UpdateComponents.Oman => "Nov.Oman.SOL.Config.exe", _ => ""
        };
        bool MatchesRelease(string candidate) => Version.TryParse(candidate, out var full) && full.Revision >= 0 &&
            full.Major == product.Major && full.Minor == product.Minor && full.Build == product.Build;
        // The NovaTouch build deliberately stores only three fields in ProductVersion.
        // Its source application's File table entry retains the complete release version.
        if (Rows(database, "SELECT `Name` FROM `_Tables`", 1).Any(row => row[0].Equals("File", StringComparison.OrdinalIgnoreCase)))
            foreach (string[] file in Rows(database, "SELECT `FileName`, `Version` FROM `File`", 2))
                if (file[0].Split('|')[^1].Equals(primaryFile, StringComparison.OrdinalIgnoreCase) && MatchesRelease(file[1])) return file[1];
        foreach (string name in names)
            foreach (Match match in FourPartVersion.Matches(Path.GetFileName(name)))
                if (MatchesRelease(match.Value)) return match.Value;
        return normalized;
    }

    private static IEnumerable<string[]> Rows(uint database, string query, int columns)
    {
        Check(MsiDatabaseOpenView(database, query, out uint view), "MSI-Tabelle lesen");
        try
        {
            Check(MsiViewExecute(view, 0), "MSI-Abfrage ausführen");
            while (true)
            {
                uint result = MsiViewFetch(view, out uint record);
                if (result == 259) yield break;
                Check(result, "MSI-Zeile lesen");
                string[] values = new string[columns];
                try
                {
                    for (uint field = 1; field <= columns; field++)
                    {
                        uint length = 0;
                        uint status = MsiRecordGetString(record, field, null, ref length);
                        if (status is not (0 or 234)) Check(status, "MSI-Wert lesen");
                        var text = new StringBuilder(checked((int)length + 1));
                        length++;
                        Check(MsiRecordGetString(record, field, text, ref length), "MSI-Wert lesen");
                        values[field - 1] = text.ToString();
                    }
                }
                finally { MsiCloseHandle(record); }
                yield return values;
            }
        }
        finally { MsiCloseHandle(view); }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new Win32Exception((int)result, operation);
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW", ExactSpelling = true)]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW", ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW", ExactSpelling = true)]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint length);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
}
