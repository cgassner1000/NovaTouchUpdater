namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Func<CancellationToken, Task<IrisUploadTarget[]>> prepareDatabaseTargets;
    private Func<CancellationToken, Task<IrisInventoryResult>> readIrisInventory = new IrisDatabaseInventory().ReadAsync;
    private IrisUploadTarget[]? activeDatabaseTargets;
    private bool preparingDatabaseUpload;
    private int activeDatabaseUploadParallelism = 8;
    private bool activeIrisUseTls;
    private bool activeStreamDatabasePackages;
    private bool activeUploadToZzzSoftware = true;
    private bool activeRestartPosAfterUpdate;
    private Func<IWorkerRunner> createPosUpdateRunner = () => new WorkerRunner("NovaTouchUpdater.RemoteCommand.ps1");

    private async Task<IrisInventoryResult> ReadSelectedIrisInventoryAsync(HostState[] selected, CancellationToken ct)
    {
        var instances = new List<IrisInstanceInfo>();
        var warnings = new List<string>();
        foreach (HostState host in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (host.IsLocal)
                {
                    IrisInventoryResult local = await readIrisInventory(ct);
                    instances.AddRange(local.Instances.Select(instance => instance with { Name=host.DisplayName + " / " + instance.Name }));
                    warnings.AddRange(local.Warnings.Select(warning => host.DisplayName + ": " + warning));
                }
                else
                {
                    var request = new WorkerRequest { Action="database-inventory", Host=host.Host, Username=txtUser.Text.Trim(), Password=txtPassword.Text,
                        UseSsl=chkSsl.Checked, HttpsPort=(int)nHttpsPort.Value };
                    WorkerResult result = await runner.RunAsync(request, _ => { }, ct);
                    if (!result.Success) { warnings.Add(host.DisplayName + ": " + FriendlyError(result)); continue; }
                    instances.AddRange(result.IrisInstances.Select(instance => new IrisInstanceInfo(host.DisplayName + " / " + instance.Name,
                        instance.Directory, instance.ConfigurationFile, instance.Version, instance.Namespaces.Where(IrisDatabaseInventory.IsApplicationNamespace).ToArray(),
                        instance.Error, instance.SuperServerPort, instance.ConnectionError)));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add(host.DisplayName + ": " + ex.Message); }
        }
        return new(instances.ToArray(), warnings.ToArray());
    }

    private async Task<bool> PrepareLocalDatabaseUploadAsync(CancellationToken ct)
    {
        preparingDatabaseUpload = true;
        UpdateButtons();
        try
        {
            IrisInventoryResult inventory = await readIrisInventory(ct);
            if (IsDisposed || ct.IsCancellationRequested) return false;
            ct.ThrowIfCancellationRequested();
            activeDatabaseTargets = IrisUploadPlan.Create(inventory, chkIrisTls.Checked);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message, "zzzSoftware-Vorbereitung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally { preparingDatabaseUpload = false; if (!IsDisposed) UpdateButtons(); }
    }

    private async Task<bool> PublishLocalSoftwarePackagesAsync(HostState host, CancellationToken ct)
    {
        if (!activeUploadToZzzSoftware || msiPackages.Count == 0 || host.Updates?.DatabaseUploadCompleted == true) return true;
        try
        {
            Mark(host, h => { h.Result = "Läuft"; h.Error = ""; h.Attempt = 0; h.Phase = "IRIS-Instanzen auf " + host.DisplayName + " für zzzSoftware ermitteln"; });
            IrisUploadTarget[] targets = host.IsLocal ? await prepareDatabaseTargets(ct) : [];
            if (host.IsLocal && targets.Length == 0)
            {
                Log(host.Host, "zzzSoftware: keine lokalen IRIS-Namespaces gefunden; kein Datenbank-Upload erforderlich.");
                if (host.Updates is { } noDatabase) noDatabase.CompleteDatabaseUpload(false);
                return true;
            }
            MsiPackageRequest[] packages = msiPackages.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            foreach (MsiPackageRequest package in packages)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(package.PayloadPath);
                package.Sha256 = preparedPackageHashes is not null && preparedPackageHashes.TryGetValue(path, out string? hash)
                    ? hash : await WorkerRunner.Sha256Async(path, ct);
            }
            var request = new WorkerRequest { Action = "publish-msi-packages", Host = host.Host, LocalExecution = host.IsLocal,
                Username=host.IsLocal ? "" : activeUser, Password=host.IsLocal ? "" : activePassword,
                UseSsl=!host.IsLocal && activeSsl, HttpsPort=activeHttpsPort, RemoteInstallPath=activeRemoteInstallPath,
                SkipZipTransfer=!host.IsLocal && activeSkipZipTransfer, SkipLocalPackageCopy=!host.IsLocal && activeStreamDatabasePackages,
                IrisUseTls=activeIrisUseTls, MsiPackages = packages, DatabaseTargets = targets, DatabaseUploadParallelism = activeDatabaseUploadParallelism };
            WorkerResult result = await runner.RunAsync(request, ev =>
            {
                Log(host.Host, ev.Phase + ": " + ev.Message);
                Mark(host, h => h.ApplyProgress(ev));
            }, ct);
            if (!result.Success) throw new IOException(FriendlyError(result));
            if (host.Updates is { } state) state.CompleteDatabaseUpload(result.Changed);
            Mark(host, h => { h.Error = ""; h.Phase = "zzzSoftware-Upload abgeschlossen"; h.SetProgress(100); });
            Log(host.Host, "zzzSoftware => OK " + result.Message);
            return true;
        }
        catch (OperationCanceledException)
        {
            Mark(host, h => { h.Result = "Abgebrochen"; h.Phase = "zzzSoftware-Upload abgebrochen"; });
            throw;
        }
        catch (Exception ex)
        {
            if (host.Updates is { } state) state.DatabaseUploadError = DisplayText.DatabaseUploadError(ex.Message);
            Mark(host, h => { h.Result = "Fehler"; h.UpdateFailed = true; h.Phase = "zzzSoftware-Upload fehlgeschlagen"; h.Error = DisplayText.DatabaseUploadError(ex.Message); });
            Log(host.Host, "zzzSoftware => FEHLER " + ex.Message);
            return false;
        }
    }

    private async Task CompleteUpdateRunAsync(HostState[] selected, CancellationToken ct)
    {
        HostState[] completed = selected.Where(host => host.Updates is { Pending: UpdateComponents.None } state &&
            (!host.UpdateFailed || state.DatabaseUploadError.Length > 0 || state.PosRestartError.Length > 0)).ToArray();
        try
        {
            foreach (HostState host in completed)
            {
                if (!activeUploadToZzzSoftware) host.Updates!.DatabaseUploadError = "";
                if (!activeRestartPosAfterUpdate) host.Updates!.PosRestartError = "";
            }
            await ParallelPhaseAsync(completed, activeDatabaseUploadParallelism, async (host, token) =>
            {
                await PublishLocalSoftwarePackagesAsync(host, token);
            }, ct);
            bool uploadsFailed = activeUploadToZzzSoftware && msiPackages.Count > 0 && selected.Any(host =>
                host.Updates?.Pending != UpdateComponents.None || host.Updates.DatabaseUploadError.Length > 0);
            if (activeRestartPosAfterUpdate && uploadsFailed)
                foreach (HostState host in completed.Where(host => !host.IsLocal && !host.Updates!.PosRestartCompleted))
                    host.Updates!.PosRestartError = "POS-Neustart wartet auf erfolgreiche zzzSoftware-Uploads.";
            if (activeRestartPosAfterUpdate && !uploadsFailed)
            {
                HostState[] remote = completed.Where(host => !host.IsLocal && !host.Updates!.PosRestartCompleted && host.Updates.DatabaseUploadError.Length == 0).ToArray();
                await ParallelPhaseAsync(remote, activeDatabaseUploadParallelism, RestartPosAfterUpdateAsync, ct);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (HostState host in completed)
                Mark(host, h => { h.UpdateFailed = true; h.Result = "Abgebrochen"; h.Phase = "Update-Abschluss abgebrochen"; });
            throw;
        }
        foreach (HostState host in completed) FinishHostUpdate(host);
    }

    private async Task RestartPosAfterUpdateAsync(HostState host, CancellationToken ct)
    {
        if (host.IsLocal) return;
        ct.ThrowIfCancellationRequested();
        HostUpdateState state = host.Updates!;
        Mark(host, h => { h.Result = "Läuft"; h.Error = ""; h.Phase = "POS-Neustart nach Update"; });
        try
        {
            using IWorkerRunner command = createPosUpdateRunner();
            WorkerResult result = await command.RunAsync(new WorkerRequest { Action="restart-pos", Host=host.Host,
                Username=activeUser, Password=activePassword, UseSsl=activeSsl, HttpsPort=activeHttpsPort }, ev =>
                { Log(host.Host, ev.Phase + ": " + ev.Message); Mark(host, h => h.ApplyProgress(ev)); }, CancellationToken.None);
            if (!result.Success) throw new IOException(FriendlyError(result));
            state.CompletePosRestart(result);
            Log(host.Host, "POS nach Update => " + (result.Skipped ? "ÜBERSPRUNGEN " : "OK ") + result.Message);
        }
        catch (Exception ex)
        {
            state.PosRestartError = "POS-Neustart: " + ex.Message;
            Log(host.Host, "POS nach Update => FEHLER " + ex.Message);
        }
    }
}
