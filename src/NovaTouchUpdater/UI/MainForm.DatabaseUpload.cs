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
                    host.HasIrisInstances = local.Instances.Length > 0;
                    instances.AddRange(local.Instances.Select(instance => instance with { Name=host.DisplayName + " / " + instance.Name }));
                    warnings.AddRange(local.Warnings.Select(warning => host.DisplayName + ": " + warning));
                }
                else
                {
                    var request = new WorkerRequest { Action="database-inventory", Host=host.Host, Username=txtUser.Text.Trim(), Password=txtPassword.Text,
                        UseSsl=chkSsl.Checked, HttpsPort=(int)nHttpsPort.Value };
                    WorkerResult result = await runner.RunAsync(request, _ => { }, ct);
                    if (!result.Success) { warnings.Add(host.DisplayName + ": " + FriendlyError(result)); continue; }
                    host.HasIrisInstances = result.IrisInstances.Length > 0;
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
        if (!activeUploadToZzzSoftware || msiPackages.Count == 0)
        {
            string reason = !activeUploadToZzzSoftware ? "zzzSoftware: Upload ausgeschaltet." : "zzzSoftware: keine MSI-Pakete ausgewählt; kein Upload.";
            if (host.Updates is { } skipped) skipped.DatabaseUploadMessage = reason;
            Log(host.Host, reason);
            return true;
        }
        if (host.Updates?.DatabaseUploadCompleted == true)
        {
            Log(host.Host, "zzzSoftware: bereits in diesem Durchlauf abgeschlossen; " + host.Updates.DatabaseUploadMessage);
            return true;
        }
        if (!host.IsLocal && host.HasIrisInstances == false)
        {
            const string reason = "zzzSoftware: keine IRIS-Instanz auf diesem Zielrechner; kein Upload.";
            host.Updates?.CompleteDatabaseUpload(false, reason);
            Log(host.Host, reason);
            return true;
        }
        try
        {
            Mark(host, h => { h.Result = "Läuft"; h.Error = ""; h.Attempt = 0; h.Phase = "IRIS-Instanzen auf " + host.DisplayName + " für zzzSoftware ermitteln"; });
            IrisUploadTarget[] targets = host.IsLocal ? await prepareDatabaseTargets(ct) : [];
            Log(host.Host, "zzzSoftware-Vorbereitung: " + msiPackages.Count + " MSI-Paket(e); " +
                (host.IsLocal ? targets.Sum(target => target.Namespaces.Length) + " lokale IRIS-Namespaces." : "IRIS-Namespaces werden auf dem Remote-Host ermittelt."));
            if (host.IsLocal && targets.Length == 0)
            {
                Log(host.Host, "zzzSoftware: keine lokalen IRIS-Namespaces gefunden; kein Datenbank-Upload erforderlich.");
                if (host.Updates is { } noDatabase) noDatabase.CompleteDatabaseUpload(false, "zzzSoftware: keine lokalen IRIS-Anwendungsnamespaces gefunden; kein Upload.");
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
                SkipRemotePackageCopy=!host.IsLocal && activeSkipRemotePackageCopy,
                IrisUseTls=activeIrisUseTls, MsiPackages = packages, DatabaseTargets = targets, DatabaseUploadParallelism = activeDatabaseUploadParallelism };
            WorkerResult result = await runner.RunAsync(request, ev =>
            {
                Log(host.Host, ev.Phase + ": " + ev.Message);
                Mark(host, h => h.ApplyProgress(ev));
            }, ct);
            host.Updates?.RecordDatabaseUploads(result.DatabaseUploadedKinds);
            if (!result.Success) throw new IOException(FriendlyError(result));
            if (host.Updates is { } state) state.CompleteDatabaseUpload(result.Changed,
                "zzzSoftware: " + (result.Message.Length > 0 ? result.Message : result.Changed ? "MSI-Upload abgeschlossen." : "geprüft; kein MSI hochgeladen."));
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
        HostState[] targets = selected.Where(host => MatchesSelectedSoftware(host) || IsDatabaseUploadServer(host)).Where(host => host.Updates is not null && host.Result != "Abgebrochen").ToArray();
        try
        {
            foreach (HostState host in targets)
            {
                if (host.Updates!.PosVersionBeforeUpdate.Length == 0)
                    host.Updates.PosVersionBeforeUpdate = host.PosBefore is not ("" or "–") ? host.PosBefore : host.PosVersion;
                if (host.UpdateFailed && host.Updates!.Pending != UpdateComponents.None && host.Updates.ErrorText.Length == 0 && host.Error.Length > 0)
                    foreach (UpdateComponents component in HostUpdateState.Each(host.Updates.Pending)) host.Updates.Fail(component, host.Error);
                if (!activeUploadToZzzSoftware) host.Updates!.DatabaseUploadError = "";
                host.Updates!.PosRestartSkipMessage = "";
                if (!activeRestartPosAfterUpdate) host.Updates.PosRestartError = "";
            }
            await ParallelPhaseAsync(targets, activeDatabaseUploadParallelism, async (host, token) =>
            {
                await PublishLocalSoftwarePackagesAsync(host, token);
            }, ct);
            bool uploadsFailed = activeUploadToZzzSoftware && msiPackages.Count > 0 && targets.Any(host =>
                !host.Updates!.DatabaseUploadCompleted || host.Updates.DatabaseUploadError.Length > 0);
            // Retain evidence of a committed upload for every host in this run,
            // including POS devices without IRIS and later POS-only retries.
            foreach (var run in targets.GroupBy(host => host.Updates!.RunId))
                if (run.Any(host => host.Updates!.SuccessfulPosUploadInRun))
                    foreach (HostState host in run) host.Updates!.SuccessfulPosUploadInRun = true;
            if (activeRestartPosAfterUpdate)
            {
                var remote = new List<HostState>();
                foreach (HostState host in targets.Where(host => !host.IsLocal && !host.Updates!.PosRestartCompleted))
                {
                    HostUpdateState state = host.Updates!;
                    state.PosRestartError = "";
                    string reason = PosRestartPrerequisite(host);
                    if (reason.Length == 0)
                        reason = uploadsFailed ? "POS-Neustart wartet auf erfolgreiche zzzSoftware-Uploads." :
                            !state.SuccessfulPosUploadInRun ? "POS-Neustart übersprungen: kein POS-MSI neu in zzzSoftware hochgeladen (z. B. gleiche/neue Versionen oder keine IRIS-Namespaces)." : "";
                    if (reason.Length == 0) remote.Add(host);
                    else
                    {
                        if (reason == "POS-Neustart wartet auf erfolgreiche zzzSoftware-Uploads.") state.PosRestartError = reason;
                        else state.PosRestartSkipMessage = reason;
                        Log(host.Host, reason);
                    }
                }
                await ParallelPhaseAsync(remote, activeDatabaseUploadParallelism, RestartPosAfterUpdateAsync, ct);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (HostState host in targets)
                Mark(host, h => { h.UpdateFailed = true; h.Result = "Abgebrochen"; h.Phase = "Update-Abschluss abgebrochen"; });
            throw;
        }
        foreach (HostState host in targets) FinishHostUpdate(host);
    }

    private string PosRestartPrerequisite(HostState host)
    {
        if (!activeUploadToZzzSoftware) return "POS-Neustart übersprungen: zzzSoftware-Upload ausgeschaltet.";
        if (!msiPackages.TryGetValue(UpdateComponents.Pos, out var package)) return "POS-Neustart übersprungen: kein POS-Paket ausgewählt.";
        string before = host.Updates!.PosVersionBeforeUpdate;
        if (!Version.TryParse(before, out var installed) || !Version.TryParse(package.ProductVersion, out var target))
            return "POS-Neustart übersprungen: installierte POS-Version vor dem Update nicht eindeutig vergleichbar (" + before + ").";
        static Version MsiVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
        return MsiVersion(installed) < MsiVersion(target) ? "" :
            "POS-Neustart übersprungen: POS war vor dem Update bereits gleich/neuer (" + before + ", Ziel " + package.ProductVersion + ").";
    }

    private async Task RestartPosAfterUpdateAsync(HostState host, CancellationToken ct)
    {
        if (host.IsLocal) return;
        ct.ThrowIfCancellationRequested();
        HostUpdateState state = host.Updates!;
        if (PosRestartPrerequisite(host).Length > 0 || !state.SuccessfulPosUploadInRun ||
            !state.DatabaseUploadCompleted || state.DatabaseUploadError.Length > 0)
            throw new InvalidOperationException("POS-Neustart benötigt einen erfolgreich abgeschlossenen zzzSoftware-Upload.");
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
