using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal static class ServerSuiteInstaller
{
    internal static bool IsSetup(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    internal static string ReadVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.ProductName?.Trim(), "NovaTouch Server Suite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die ausgewählte EXE ist kein NovaTouch-Server-Suite-Setup.");
        if (!Regex.IsMatch(info.FileDescription?.Trim() ?? "", @"^NovaTouch Server Suite (?:Updater|Silent Update) v[1-9][0-9]*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("Dieses Server-Suite-Setup hat keine unterstützte Kennung ab v1. " +
                "Ein zuverlässiges stilles Update mehrerer Instanzen ist damit nicht möglich.\n\n" +
                "Bitte für diese Version das passende Server-Suite-ZIP-Paket verwenden.");
        string version = info.ProductVersion?.Trim() ?? "";
        _ = SoftwarePackageCatalog.NumericVersion(version);
        if (UpdatePackageNames.TryServerSuiteVersion(path, out string named) &&
            SoftwarePackageCatalog.NumericVersion(named) != SoftwarePackageCatalog.NumericVersion(version))
            throw new InvalidDataException("Dateiname und Produktversion des Server-Suite-Setups stimmen nicht überein.");
        return version;
    }
}
