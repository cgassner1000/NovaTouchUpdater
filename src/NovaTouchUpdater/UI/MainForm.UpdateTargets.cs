using System.Security.Principal;

namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Func<bool> isLocalAdministrator = () =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    };
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
        if (!installsLocally || isLocalAdministrator()) return true;
        const string message = "Für lokale Installationen sind Administratorrechte erforderlich.\n\n" +
            "Bitte NovaTouch Updater schließen und mit Rechtsklick auf die EXE → ‚Als Administrator ausführen‘ neu starten.\n\n" +
            "Es wurden noch keine Update-Dateien kopiert und keine Installationen gestartet.";
        if (showLocalAdministratorNotice is { } show) show(message);
        else MessageBox.Show(this, message, "NovaTouch Updater als Administrator neu starten", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }
}
