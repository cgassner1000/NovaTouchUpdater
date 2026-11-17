namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly ContextMenuStrip hostActionsMenu = new();
    private HostState? contextHost;
    private Task hostActionTask = Task.CompletedTask;
    private Func<IWorkerRunner> createCommandRunner = () => new WorkerRunner("NovaTouchUpdater.RemoteCommand.ps1");
    private Func<string, string, bool>? confirmHostAction = null;
    private Action<string, string>? showHostCommandReport = null;
    private Func<HostConsoleOptions, CancellationToken, Task<bool>> openHostConsole = (options, ct) => HostConsole.OpenAsync(options, ct);

    private void ConfigureHostActionsMenu()
    {
        hostActionsMenu.Font = Font;
        Add("hostUpdate", "Update", async host => await UpdateSingleHostAsync(host));
        Add("hostCommand", "Eingabeaufforderung", ShowHostCommandAsync);
        hostActionsMenu.Items.Add(new ToolStripSeparator());
        Add("hostRestartPos", "NovaTouch POS neustarten", host => RunHostActionsAsync([host], false, true, false, false, false));
        Add("hostRestartPayment", "Nt.Payment neustarten", host => RestartHostServiceAsync(host, UpdateComponents.Payment));
        Add("hostRestartFiscal", "Nt.Fiskal neustarten", host => RestartHostServiceAsync(host, UpdateComponents.Fiscal));
        Add("hostRestartServerSuite", "NT3.Server.Suite neustarten", host => RestartHostServiceAsync(host, UpdateComponents.ServerSuite));
        hostActionsMenu.Items.Add(new ToolStripSeparator());
        Add("hostRemove", "Client entfernen", host => { RemoveRemoteHost(host); return Task.CompletedTask; });

        void Add(string name, string text, Func<HostState, Task> action)
        {
            var item = new ToolStripMenuItem(text) { Name = name };
            item.Click += async (_, _) =>
            {
                HostState? host = contextHost;
                if (host is null || !hosts.Contains(host) || !CanStartHostScan || !hostActionTask.IsCompleted) return;
                if (name is "hostUpdate" or "hostCommand" && IsUnreachableRemoteHost(host)) return;
                hostActionTask = action(host);
                await hostActionTask;
            };
            hostActionsMenu.Items.Add(item);
        }
    }

    private ContextMenuStrip PrepareHostActionsMenu(HostState host)
    {
        contextHost = host;
        bool idle = CanStartHostScan && hostActionTask.IsCompleted && hosts.Contains(host);
        foreach (ToolStripItem item in hostActionsMenu.Items) item.Enabled = idle;
        hostActionsMenu.Items["hostUpdate"]!.Enabled = hostActionsMenu.Items["hostCommand"]!.Enabled = idle && !IsUnreachableRemoteHost(host);
        hostActionsMenu.Items["hostCommand"]!.ToolTipText = host.IsLocal ? "Eingabeaufforderung als Administrator öffnen." : "Interaktive PowerShell mit Administratorrechten über die eingestellte WinRM-Verbindung öffnen.";
        hostActionsMenu.Items["hostRestartPos"]!.Enabled = idle && (host.InstalledPrograms.Any(program => program.Kind == "pos") || HasInstalledVersion(host.PosVersion));
        hostActionsMenu.Items["hostRestartPayment"]!.Enabled = idle && HasService(host, "payment");
        hostActionsMenu.Items["hostRestartFiscal"]!.Enabled = idle && HasService(host, "fiscal");
        hostActionsMenu.Items["hostRestartServerSuite"]!.Enabled = idle && HasService(host, "serversuite");
        hostActionsMenu.Items["hostRemove"]!.Enabled = idle && !host.IsLocal;
        hostActionsMenu.Items["hostRemove"]!.ToolTipText = host.IsLocal ? "Der lokale Rechner gehört automatisch zur Ansicht." : "Nur diesen Client aus der Hostliste entfernen.";
        UiTheme.StyleMenu(hostActionsMenu);
        return hostActionsMenu;
    }

    private static bool HasService(HostState host, string kind) => host.Services.Any(service => service.Kind == kind && service.ServiceExists);
    private static bool IsUnreachableRemoteHost(HostState host) => !host.IsLocal && host.Connection == "Nicht erreichbar";
    private static bool HasInstalledVersion(string version) => Version.TryParse(version, out _);

    private async Task UpdateSingleHostAsync(HostState host)
    {
        if (!CanStartHostScan || !hosts.Contains(host)) return;
        if (!host.Scanned)
        {
            await StartHostScan([host]);
            if (host.Result == "Abgebrochen") return;
        }
        if (host.Scanned && hosts.Contains(host)) await RunUpdatesForHostsAsync(false, [host]);
    }

    private bool ConfirmHostCommand(string message, string title) => confirmHostAction?.Invoke(message, title) ??
        AppMessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private Task RestartHostServiceAsync(HostState host, UpdateComponents component)
    {
        string kind = component == UpdateComponents.Payment ? "payment" : component == UpdateComponents.Fiscal ? "fiscal" : "serversuite";
        if (!HasService(host, kind) || !ConfirmHostCommand(HostUpdateState.Label(component) + " auf " + host.DisplayName + " neu starten?", "Dienst neu starten"))
            return Task.CompletedTask;
        return RunHostActionsAsync([host], false, false, component == UpdateComponents.Payment, component == UpdateComponents.Fiscal, component == UpdateComponents.ServerSuite);
    }

    private void RemoveRemoteHost(HostState host)
    {
        if (!CanStartHostScan || host.IsLocal || !hosts.Contains(host)) return;
        ApplyRemoteHosts(hosts.Where(candidate => !candidate.IsLocal && candidate != host).Select(candidate => candidate.Host).ToArray(), "Hostliste");
    }

    private async Task ShowHostCommandAsync(HostState host)
    {
        if (!CanStartHostScan || !hosts.Contains(host) || IsUnreachableRemoteHost(host) || !ValidateTargetCredentials([host])) return;
        var options = host.IsLocal ? new HostConsoleOptions(host.Host, true)
            : new HostConsoleOptions(host.Host, false, txtUser.Text.Trim(), txtPassword.Text, chkSsl.Checked, (int)nHttpsPort.Value);
        try
        {
            bool opened = await openHostConsole(options, appLifetime.Token);
            Log(host.Host, opened ? "Interaktive Konsole geöffnet: " + (host.IsLocal ? "CMD als Administrator" : "PowerShell / WinRM") : "Start der Eingabeaufforderung abgebrochen.");
        }
        catch (OperationCanceledException) when (appLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log(host.Host, "Konsole öffnen fehlgeschlagen: " + ex.Message);
            if (!IsDisposed) AppMessageBox.Show(this, ex.Message, "Eingabeaufforderung", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
