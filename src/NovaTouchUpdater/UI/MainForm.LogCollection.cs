namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Func<IWorkerRunner> createLogCollectionRunner = () => new WorkerRunner();
    private Func<string> logCollectionBaseDirectory = () => AppContext.BaseDirectory;
    private Action<string, bool>? showLogCollectionReport = null;

    private async Task CollectHostLogsAsync(HostState host)
    {
        if (!CanStartHostScan || !hosts.Contains(host) || IsUnreachableRemoteHost(host) || !ValidateTargetCredentials([host])) return;
        bool wasPaused = connectionScanPaused;
        string directory = "";
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        busy = true; cancellation = ct; connectionScanPaused = true;
        InvalidateConnectionChecks();
        UpdateButtons();
        try
        {
            string executableDirectory = logCollectionBaseDirectory();
            directory = LogCollectionFolder.Create(executableDirectory, host.IsLocal ? Environment.MachineName + " (LOKAL)" : host.Host, DateTimeOffset.Now);
            Mark(host, h => { h.Phase = "Logs werden gesammelt"; h.SetProgress(0); });
            Log(host.Host, "COLLECT LOGS gestartet: " + directory);
            using var worker = createLogCollectionRunner();
            WorkerResult result = await worker.RunAsync(new WorkerRequest
            {
                Action = "collect-logs", Host = host.Host, LocalExecution = host.IsLocal, LogCollectionDirectory = directory,
                Username = host.IsLocal ? "" : txtUser.Text.Trim(), Password = host.IsLocal ? "" : txtPassword.Text,
                UseSsl = chkSsl.Checked, HttpsPort = (int)nHttpsPort.Value
            }, ev =>
            {
                Log(host.Host, ev.Phase + " | " + ev.Message);
                Mark(host, h => { h.Phase = ev.Message; h.SetProgress(ev.ProgressPercent); });
            }, ct.Token);
            ct.Token.ThrowIfCancellationRequested();
            bool hasWarnings = !result.Success || result.LogCollectionWarnings.Length > 0;
            string outcome = result.Success ? result.Message : FriendlyError(result);
            string report = outcome + "\n\nZIP: " + directory + ".zip";
            string dialogReport = report;
            if (result.LogCollectionWarnings.Length > 0) report += "\n\nHinweise:\n" + string.Join("\n", result.LogCollectionWarnings);
            if (result.LogCollectionWarnings.Length > 0)
            {
                dialogReport += "\n\nHinweise:\n" + string.Join("\n", result.LogCollectionWarnings.Take(5).Select(warning => warning.Length > 200 ? warning[..197] + "…" : warning));
                dialogReport += "\n\nVollständiges Protokoll: Sammlung.txt / Quellen.txt";
            }
            File.AppendAllText(Path.Combine(directory, "Sammlung.txt"), "\nErgebnis: " + report + "\n");
            Mark(host, h => h.Phase = "Log-ZIP wird erstellt");
            LogCollectionArchiveResult archive = await Task.Run(() => LogCollectionArchive.CreateAsync(executableDirectory, directory, ct.Token), ct.Token);
            Log(host.Host, "COLLECT LOGS => " + outcome + " | ZIP: " + archive.ArchivePath);
            foreach (string warning in result.LogCollectionWarnings) Log(host.Host, "COLLECT LOGS Hinweis: " + warning);
            if (archive.CleanupWarning.Length > 0)
            {
                hasWarnings = true;
                Log(host.Host, archive.CleanupWarning);
                dialogReport += "\n\n" + archive.CleanupWarning;
            }
            Mark(host, h => { h.Phase = outcome + (hasWarnings ? " – siehe Sammelprotokoll" : ""); h.SetProgress(result.Success ? 100 : null); });
            if (!IsDisposed)
            {
                if (!result.Success || archive.CleanupWarning.Length > 0) ShowIssue(dialogReport);
                try { launchHostExplorer(HostExplorer.CreateDirectoryStartInfo(Path.GetDirectoryName(archive.ArchivePath)!)); }
                catch (Exception ex)
                {
                    Log(host.Host, "Log-ZIP erstellt; Explorer öffnen fehlgeschlagen: " + ex.Message);
                    ShowIssue("Die ZIP wurde erstellt: " + archive.ArchivePath + "\n\nExplorer öffnen fehlgeschlagen: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            RecordFailure("Abgebrochen – die Sammlung ist unvollständig.");
        }
        catch (Exception ex)
        {
            RecordFailure("Logs sammeln fehlgeschlagen: " + ex.Message);
            if (!IsDisposed)
            {
                ShowIssue(ex.Message + (directory.Length > 0 && Directory.Exists(directory) ? "\n\nGesammelte Dateien: " + directory : ""));
            }
        }
        finally
        {
            cancellation = null; busy = false; connectionScanPaused = wasPaused;
            if (!IsDisposed) { UpdateSummary(); UpdateButtons(); }
        }

        void RecordFailure(string message)
        {
            Log(host.Host, "COLLECT LOGS => " + message);
            Mark(host, h => h.Phase = message);
            if (directory.Length == 0 || !Directory.Exists(directory)) return;
            try { File.AppendAllText(Path.Combine(directory, "Sammlung.txt"), "\nErgebnis: " + message + "\n"); }
            catch (Exception ex) { Log(host.Host, "Sammelprotokoll konnte nicht ergänzt werden: " + ex.Message); }
        }

        void ShowIssue(string message)
        {
            if (showLogCollectionReport is not null) showLogCollectionReport(message, true);
            else AppMessageBox.Show(this, message, "Collect Logs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
