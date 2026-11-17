namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly ContextMenuStrip hostBackgroundMenu = new();
    private Func<string?>? chooseRemoteClient = null;
    private Func<IWorkerRunner> createHostConnectionRunner = () => new WorkerRunner("NovaTouchUpdater.ConnectionWorker.ps1");
    private Action<WinRmTestForm>? showWinRmDialog = null;

    private ContextMenuStrip PrepareHostBackgroundMenu()
    {
        contextHost = null;
        if (hostBackgroundMenu.Items.Count == 0)
        {
            hostBackgroundMenu.Font = Font;
            var add = new ToolStripMenuItem("Remote Client hinzufügen") { Name = "addRemoteClient" };
            add.Click += async (_, _) =>
            {
                if (!CanStartHostScan || !hostActionTask.IsCompleted) return;
                hostActionTask = PromptAddRemoteClientAsync();
                await hostActionTask;
            };
            hostBackgroundMenu.Items.Add(add);
        }
        hostBackgroundMenu.Items[0].Enabled = CanStartHostScan && hostActionTask.IsCompleted;
        UiTheme.StyleMenu(hostBackgroundMenu);
        return hostBackgroundMenu;
    }

    private async Task PromptAddRemoteClientAsync()
    {
        string? name;
        if (chooseRemoteClient is { } choose) name = choose();
        else
        {
            using var dialog = new AddRemoteClientDialog();
            name = dialog.ShowDialog(this) == DialogResult.OK ? dialog.HostName : null;
        }
        if (name is not null) await AddRemoteClientAsync(name);
    }

    private async Task AddRemoteClientAsync(string name)
    {
        if (!CanStartHostScan) return;
        if (!AddRemoteClientDialog.TryNormalize(name, out string normalized)) throw new ArgumentException("Ungültiger Remote-Hostname oder ungültige IP-Adresse.");
        HostState? host = hosts.FirstOrDefault(host => !host.IsLocal && host.Host.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (host is null)
        {
            ApplyRemoteHosts(hosts.Where(host => !host.IsLocal).Select(host => host.Host).Append(normalized).ToArray(), "Hostliste");
            host = hosts.Single(host => !host.IsLocal && host.Host.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        }
        if (Mode == UpdateExecutionMode.Local) executionMode.SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
        statusFilter.SelectedIndex = 0;
        if (host.Row is { } row) table.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
        await CheckRemoteHostConnectionAsync(host);
    }

    private async Task CheckRemoteHostConnectionAsync(HostState host)
    {
        if (!CanStartHostScan || host.IsLocal || !hosts.Contains(host) || !ValidateTargetCredentials([host])) return;
        string previous = host.Connection;
        bool? previousHttps = host.ConnectionState.UsesHttps;
        bool wasPaused = connectionScanPaused;
        bool useSsl = chkSsl.Checked;
        var request = new WorkerRequest
        {
            Action = "monitor-connections", Hosts = [host.Host], Parallelism = 1,
            Username = txtUser.Text.Trim(), Password = txtPassword.Text, UseSsl = useSsl, HttpsPort = (int)nHttpsPort.Value
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        busy = true; cancellation = timeout; connectionScanPaused = true;
        InvalidateConnectionChecks();
        Mark(host, h => h.Connection = "Prüfung läuft");
        int revision = host.ConnectionState.Revision;
        UpdateButtons();
        try
        {
            WorkerEvent? answer = null;
            using var worker = createHostConnectionRunner();
            WorkerResult result = await worker.RunAsync(request, ev =>
            {
                if (string.Equals(ev.Host, host.Host, StringComparison.OrdinalIgnoreCase)) answer = ev;
            }, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            bool connected = result.Success && answer?.Connected == true;
            string detail = !result.Success ? FriendlyError(result) : answer?.Message ?? "Keine Rückmeldung der Verbindungsprüfung.";
            Mark(host, h => h.ApplyConnectionCheck(revision, connected, detail, DateTimeOffset.Now, useSsl));
            Log(host.Host, "CONNECT => " + (connected ? "OK " : "FEHLER ") + DisplayText.Clean(detail));
        }
        catch (OperationCanceledException)
        {
            if (operationCancellationRequested || appLifetime.IsCancellationRequested)
                Mark(host, h => h.ConnectionState.SetForegroundStatus(previous, previousHttps));
            else Mark(host, h => h.ApplyConnectionCheck(revision, false, "Zeitlimit der Verbindungsprüfung überschritten.", DateTimeOffset.Now, useSsl));
        }
        catch (Exception ex)
        {
            Mark(host, h => h.ApplyConnectionCheck(revision, false, DisplayText.Clean(ex.Message), DateTimeOffset.Now, useSsl));
            Log(host.Host, "CONNECT => FEHLER " + ex.Message);
        }
        finally
        {
            cancellation = null; busy = false; connectionScanPaused = wasPaused;
            if (!IsDisposed) { RebuildRows(); UpdateSummary(); UpdateButtons(); }
        }
    }

    private void ShowWinRmConfiguration(HostState[] targets, string[] trustedHosts, bool allowUnconfirmedClientSetup = false)
    {
        if (!CanStartHostScan || !ValidateTargetCredentials(targets)) return;
        using var dialog = new WinRmTestForm(targets.Select(host => host.Host).ToArray(), txtUser.Text.Trim(), txtPassword.Text,
            (int)nScan.Value, chkSsl.Checked, (int)nHttpsPort.Value, trustedHosts, winRmCertificatePath, winRmPrivateKeyPath,
            (certificate, key) =>
            {
                winRmCertificatePath = certificate; winRmPrivateKeyPath = key;
                try
                {
                    Directory.CreateDirectory(userDataPaths.RootDirectory);
                    AppConfigStore.SaveWinRmCertificatePaths(userDataPaths.ConfigFile, certificate, key, startupConfigPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { AppMessageBox.Show(this, "Die PEM-Dateipfade konnten nicht gespeichert werden.\n" + ex.Message, "WinRM Konfiguration", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }, allowUnconfirmedClientSetup);
        if (showWinRmDialog is { } show) show(dialog); else dialog.ShowDialog(this);
    }
}
