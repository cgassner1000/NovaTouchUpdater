namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly ContextMenuStrip hostActionsMenu = new();
    private HostState? contextHost;
    private Task hostActionTask = Task.CompletedTask;
    private Func<IWorkerRunner> createCommandRunner = () => new WorkerRunner("NovaTouchUpdater.RemoteCommand.ps1");
    private Func<string, string, bool>? confirmHostAction = null;
    private Action<string, string>? showHostCommandReport = null;

    private void ConfigureHostActionsMenu()
    {
        hostActionsMenu.Font = Font;
        Add("hostUpdate", "Update", async host => await UpdateSingleHostAsync(host));
        Add("hostCommand", "Eingabeaufforderung", host => { ShowHostCommand(host); return Task.CompletedTask; });
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

    private void ShowHostCommand(HostState host)
    {
        if (!ValidateTargetCredentials([host])) return;
        using var dialog = new HostCommandDialog(host.DisplayName, host.IsLocal, (command, ct) => RunHostCommandAsync(host, command, ct));
        dialog.ShowDialog(this);
    }

    private async Task<WorkerResult> RunHostCommandAsync(HostState host, string command, CancellationToken ct)
    {
        if (!CanStartHostScan || !hosts.Contains(host) || !ValidateTargetCredentials([host]))
            return new() { Error = "Der Host ist gerade nicht verfügbar." };
        if (string.IsNullOrWhiteSpace(command)) return new() { Error = "Bitte einen Befehl eingeben." };
        CaptureCredentials();
        WorkerResult result = new() { Error = "Befehl wurde abgebrochen." };
        await InBatchAsync([host], async batchToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, batchToken);
            using var worker = createCommandRunner();
            Mark(host, h => h.Phase = "Befehl wird ausgeführt");
            try
            {
                result = await worker.RunAsync(new()
                {
                    Action = "command", Host = host.Host, LocalExecution = host.IsLocal, CommandText = command,
                    Username = activeUser, Password = activePassword, UseSsl = activeSsl, HttpsPort = activeHttpsPort
                }, ev => { Log(host.Host, ev.Phase + ": " + ev.Message); Mark(host, h => h.ApplyProgress(ev)); }, linked.Token);
                Mark(host, h =>
                {
                    if (result.Success) h.CompleteCommand(false, DateTimeOffset.Now);
                    else h.FailCommand(false, FriendlyError(result));
                    h.Phase = result.Success ? "Befehl abgeschlossen" : "Befehl fehlgeschlagen";
                });
                Log(host.Host, "COMMAND => " + (result.Success ? "OK " + result.Message : "FEHLER " + FriendlyError(result)));
            }
            catch (OperationCanceledException) { Mark(host, h => h.CancelCommand()); throw; }
            catch (Exception ex)
            {
                result = new() { Error = ex.Message };
                Mark(host, h => { h.FailCommand(false, ex.Message); h.Phase = "Befehl fehlgeschlagen"; });
                Log(host.Host, "COMMAND => FEHLER " + ex.Message);
            }
            finally { CountCompleted(); }
        });
        return result;
    }
}
