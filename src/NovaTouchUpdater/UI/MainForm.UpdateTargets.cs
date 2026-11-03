namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Func<bool> isProcessElevated = AppElevation.IsProcessElevated;
    private Action<string>? showLocalAdministratorNotice = null;

    private bool MatchesSelectedSoftware(HostState host)
    {
        UpdateComponents selected = SelectedUpdateComponents();
        return host.IsLocal || selected == UpdateComponents.None || host.InstalledComponents is not { } installed || (installed & selected) != 0;
    }

    private bool IsDatabaseUploadServer(HostState host) =>
        host.HasIrisInstances == true && chkUploadToZzzSoftware.Checked && msiPackages.Count > 0;

    private static UpdateComponents InstalledComponents(HostState host)
    {
        UpdateComponents installed = UpdateComponents.None;
        foreach (ServiceInfo service in host.Services)
            installed |= service.Kind.ToLowerInvariant() switch
            {
                "payment" => UpdateComponents.Payment, "fiscal" => UpdateComponents.Fiscal,
                "serversuite" => UpdateComponents.ServerSuite, _ => UpdateComponents.None
            };
        foreach (InstalledProgramInfo program in host.InstalledPrograms)
            installed |= program.Kind.ToLowerInvariant() switch
            {
                "pos" => UpdateComponents.Pos, "ntc" => UpdateComponents.Ntc,
                "ntv" => UpdateComponents.Ntv, "oman" => UpdateComponents.Oman, _ => UpdateComponents.None
            };
        return installed;
    }

    private bool EnsureLocalInstallationAccess(HostState[] selected, bool onlyFailed)
    {
        bool installsLocally = selected.Any(host => host.IsLocal &&
            (ComponentsForUpdate(host, onlyFailed) & (host.InstalledComponents ?? UpdateComponents.All)) != UpdateComponents.None);
        if (!installsLocally || isProcessElevated()) return true;
        const string message = "Für lokale Installationen muss der Updater mit erhöhten Rechten laufen, auch bei einem Administratorkonto.\n\n" +
            "Bitte im Header links von ‚Report issue‘ auf ‚NovaTouchUpdater als Administrator starten‘ klicken.\n\n" +
            "Es wurden noch keine Update-Dateien kopiert und keine Installationen gestartet.";
        if (showLocalAdministratorNotice is { } show) show(message);
        else MessageBox.Show(this, message, "NovaTouch Updater als Administrator neu starten", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }
}
