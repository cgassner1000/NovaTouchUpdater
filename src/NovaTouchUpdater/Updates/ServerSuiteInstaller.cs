using System.Diagnostics;

namespace NovaTouchUpdater;

internal static class ServerSuiteInstaller
{
    // Older setups ignore /SERVICENAME and can select the wrong instance.
    internal const string ProtocolMarker = "NovaTouch Server Suite Updater v1";
    internal static bool IsSetup(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    internal static string ReadVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileDescription?.Trim() != ProtocolMarker || info.ProductName?.Trim() != "NovaTouch Server Suite")
            throw new InvalidDataException("Dieses Server-Suite-Setup unterstützt die benötigten Silent-Parameter für gezielte Instanzupdates nicht.\n\n" +
                "Mehrere Server-Suite-Instanzen können damit nicht zuverlässig aktualisiert werden.\n\n" +
                "Bitte ein neueres Setup mit dem aktualisierten Inno-Skript oder das passende Server-Suite-ZIP-Paket auswählen.");
        string version = info.ProductVersion?.Trim() ?? "";
        _ = SoftwarePackageCatalog.NumericVersion(version);
        if (UpdatePackageNames.TryServerSuiteVersion(path, out string named) &&
            SoftwarePackageCatalog.NumericVersion(named) != SoftwarePackageCatalog.NumericVersion(version))
            throw new InvalidDataException("Dateiname und Produktversion des Server-Suite-Setups stimmen nicht überein.");
        return version;
    }
}
