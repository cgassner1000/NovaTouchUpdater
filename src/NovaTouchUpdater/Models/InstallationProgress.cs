namespace NovaTouchUpdater;

// Weighted workflow completion, not elapsed time. Installer steps without a
// numerical report hold their reached milestone. Only HostUpdateState.Complete
// may publish 100%, after the worker result has confirmed the whole component.
internal static class InstallationProgress
{
    internal static int Advance(int previous, WorkerEvent progress)
    {
        if (progress.ComponentProgressPercent is int overall)
            return Math.Clamp(Math.Max(previous, overall), 0, 99);

        (int Start, int End, int RawStart, int RawEnd)? stage = (progress.Phase ?? "").Trim().ToLowerInvariant() switch
        {
            "verbindung" => (0, 3, 0, 100),
            "versionen" or "softwareprüfung" or "msi-version prüfen" or "msi-vorprüfung" or "setup prüfen" or "uploadziel" => (3, 5, 0, 100),
            "übertragung" or "lokales kopieren" or "paketquelle prüfen" or "zip-prüfung" => (5, 25, 0, 100),
            "pakete vorbereiten" => (5, 5, 0, 100),
            "msi-paket geprüft" or "msi-quelle geprüft" or "setup planen" => (25, 25, 0, 100),
            "archive entpacken" or "msi entpacken" => (25, 40, 0, 100),
            "update vorbereiten" or "msi prüfen" => (40, 40, 0, 100),
            "dienste stoppen" or "dienst stoppen" => (40, 50, 0, 50),
            "server-suite-prozesse beenden" or "windows eventlog stoppen" or "python bereinigen" => (50, 50, 0, 100),
            "update installieren" or "dateien kopieren" or "installieren" or "msi installieren" or "setup installieren" or "warte auf dateifreigabe" => (50, 85, 0, 100),
            "windows eventlog starten" => (85, 85, 0, 100),
            "dienste starten" or "dienst starten" => (85, 95, 50, 100),
            "setup abgeschlossen" => (95, 95, 0, 100),
            "verifikation" or "msi-version bestätigen" or "msi verifiziert" or "instanz geprüft" => (95, 99, 0, 100),
            _ => null
        };
        if (stage is not { } range) return Math.Clamp(previous, 0, 99);
        int raw = Math.Clamp(progress.ProgressPercent ?? range.RawStart, range.RawStart, range.RawEnd);
        int value = range.Start + (range.End - range.Start) * (raw - range.RawStart) / (range.RawEnd - range.RawStart);
        return Math.Clamp(Math.Max(previous, value), 0, 99);
    }
}
