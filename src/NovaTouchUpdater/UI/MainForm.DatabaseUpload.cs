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
    private Lazy<Task<IrisUploadTarget[]>>? installationDatabaseTargets;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<HostState, Task> installationUploads = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<HostState, UpdateComponents> scheduledInstallationUploads = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<HostState, Task> installationCompilations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<HostState, Lazy<Task<IrisUploadTarget[]>>> remoteInstallationTargets = new();
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
            if (!IsDisposed) AppMessageBox.Show(this, ex.Message, "zzzSoftware-Vorbereitung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally { preparingDatabaseUpload = false; if (!IsDisposed) UpdateButtons(); }
    }

    private UpdateComponents SelectedDatabasePackages => msiPackages.Keys.Aggregate(UpdateComponents.None, (all, component) => all | component);

    private static HostUpdateState UploadInstallationPrerequisite(HostState host) =>
        host.Updates ?? throw new InvalidOperationException("zzzSoftware benötigt den bestätigten MSI-Installationsschritt auf " + host.DisplayName + ".");

    private void QueueInstallationUpload(HostState host, UpdateComponents component, CancellationToken ct)
    {
        if (cancellation is null || !activeUploadToZzzSoftware || !msiPackages.ContainsKey(component)) return;
        if ((host.Updates!.VerifiedMsiPackages & component) == 0) return;
        if ((host.Updates!.CompletedDatabasePackages & component) != 0) return;
        UpdateComponents scheduled = scheduledInstallationUploads.GetValueOrDefault(host);
        if ((scheduled & component) != 0) return;
        scheduledInstallationUploads[host] = scheduled | component;
        CancellationToken runToken = cancellation?.Token ?? ct;
        installationDatabaseTargets ??= new Lazy<Task<IrisUploadTarget[]>>(() => prepareDatabaseTargets(runToken));
        Task previous = installationUploads.GetValueOrDefault(host) ?? Task.CompletedTask;
        installationUploads[host] = UploadInstalledPackageAsync(previous, host, component, runToken, installationDatabaseTargets);
    }

    private async Task UploadInstalledPackageAsync(Task previous, HostState host, UpdateComponents component, CancellationToken ct,
        Lazy<Task<IrisUploadTarget[]>> localTargets)
    {
        // Serialize writes to zzzSoftware on this host, independently of the MSI installer.
        await previous;
        ct.ThrowIfCancellationRequested();
        await PublishDatabasePackagesAsync(host, component, ct, localTargets);
    }

    private async Task<bool> PublishLocalSoftwarePackagesAsync(HostState host, CancellationToken ct, Lazy<Task<IrisUploadTarget[]>> localTargets)
    {
        if (installationUploads.TryGetValue(host, out Task? uploads)) await uploads;
        HostUpdateState prerequisite = UploadInstallationPrerequisite(host);
        if (activeUploadToZzzSoftware)
        {
            UpdateComponents skipped = SelectedDatabasePackages & prerequisite.SkippedMsiPackages & ~host.Updates!.CompletedDatabasePackages;
            if (skipped != UpdateComponents.None)
            {
                string reason = "zzzSoftware: " + HostUpdateState.Label(skipped) + " nicht installiert – kein Upload.";
                Mark(host, h => h.Updates!.CompleteDatabasePackages(skipped, SelectedDatabasePackages, false, reason));
                Log(host.Host, reason);
            }
        }
        UpdateComponents packages = SelectedDatabasePackages & ~host.Updates!.CompletedDatabasePackages;
        // Failed installs must be recovered before publishing their MSI. Already
        // attempted uploads are retried only in a later retry run.
        packages &= ~scheduledInstallationUploads.GetValueOrDefault(host);
        return await PublishDatabasePackagesAsync(host, packages, ct, localTargets);
    }

    private async Task<bool> PublishDatabasePackagesAsync(HostState host, UpdateComponents components, CancellationToken ct, Lazy<Task<IrisUploadTarget[]>> localTargets)
    {
        if (msiPackages.Count == 0)
        {
            // Service and Server Suite updates have no MSI publication step.
            // Keep their verified update result free of unrelated upload notices.
            if (host.Updates is { } skipped) skipped.DatabaseUploadMessage = "";
            Log(host.Host, "zzzSoftware: Für die ausgewählten Updates ist kein Datenbank-Upload erforderlich. Nur MSI-Installationspakete werden in zzzSoftware gespeichert.");
            return true;
        }
        if (!activeUploadToZzzSoftware)
        {
            const string reason = "zzzSoftware: Upload ausgeschaltet.";
            if (host.Updates is { } skipped) skipped.DatabaseUploadMessage = reason;
            Log(host.Host, reason);
            return true;
        }
        if (host.Updates?.DatabaseUploadCompleted == true)
        {
            Log(host.Host, "zzzSoftware: bereits in diesem Durchlauf abgeschlossen; " + host.Updates.DatabaseUploadMessage);
            return true;
        }
        if (components == UpdateComponents.None) return host.Updates!.DatabaseUploadError.Length == 0;
        HostUpdateState prerequisite = UploadInstallationPrerequisite(host);
        UpdateComponents blocked = components & ~prerequisite.VerifiedMsiPackages;
        if (blocked != UpdateComponents.None)
        {
            string reason = "zzzSoftware wartet auf die erfolgreiche MSI-Installation: " + HostUpdateState.Label(blocked);
            Mark(host, h => h.Updates!.FailDatabasePackages(blocked, reason));
            Log(host.Host, reason);
        }
        components &= prerequisite.VerifiedMsiPackages;
        if (components == UpdateComponents.None) return false;
        if (!host.IsLocal && host.HasIrisInstances == false)
        {
            const string reason = "zzzSoftware: keine IRIS-Instanz auf diesem Zielrechner; kein Upload.";
            host.Updates?.CompleteDatabasePackages(components, SelectedDatabasePackages, false, reason);
            Log(host.Host, reason);
            return true;
        }
        try
        {
            Mark(host, h =>
            {
                h.Updates!.DatabaseUploadRunning = true;
                if (h.Updates.SoftwareCompilationRunning) return;
                h.Result = "Läuft"; h.Error = ""; h.Attempt = 0; h.Phase = "IRIS-Instanzen auf " + host.DisplayName + " für zzzSoftware ermitteln";
            });
            IrisUploadTarget[] targets = host.IsLocal ? await localTargets.Value : [];
            MsiPackageRequest[] packages = msiPackages.Where(pair => (pair.Key & components) != 0).Select(pair => pair.Value).ToArray();
            Log(host.Host, "zzzSoftware-Vorbereitung: " + string.Join(", ", packages.Select(package => package.ProductName)) + "; " +
                (host.IsLocal ? targets.Sum(target => target.Namespaces.Length) + " lokale IRIS-Namespaces." : "IRIS-Namespaces werden auf dem Remote-Host ermittelt."));
            if (host.IsLocal && targets.Length == 0)
            {
                Log(host.Host, "zzzSoftware: keine lokalen IRIS-Namespaces gefunden; kein Datenbank-Upload erforderlich.");
                Mark(host, h => h.Updates!.CompleteDatabasePackages(components, SelectedDatabasePackages, false, "zzzSoftware: keine lokalen IRIS-Anwendungsnamespaces gefunden; kein Upload."));
                return true;
            }
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
                if (ev.Phase == "zzzSoftware-Zeiten") return;
                Mark(host, h => { if (!h.Updates!.SoftwareCompilationRunning) h.ApplyProgress(ev); });
            }, ct);
            Mark(host, h => h.Updates!.RecordDatabaseUploads(result.DatabaseUploadedKinds));
            if (!result.Success) throw new IOException(FriendlyError(result));
            Mark(host, h =>
            {
                h.Updates!.CompleteDatabasePackages(components, SelectedDatabasePackages, result.Changed,
                    "zzzSoftware: " + (result.Message.Length > 0 ? result.Message : result.Changed ? "MSI-Upload abgeschlossen." : "geprüft; kein MSI hochgeladen."));
                if (h.Updates.SoftwareCompilationRunning) return;
                h.Error = ""; h.Phase = "zzzSoftware-Upload abgeschlossen"; h.SetProgress(100);
            });
            Log(host.Host, "zzzSoftware => OK " + result.Message);
            return true;
        }
        catch (OperationCanceledException)
        {
            Mark(host, h => { if (!h.Updates!.SoftwareCompilationRunning) { h.Result = "Abgebrochen"; h.Phase = "zzzSoftware-Upload abgebrochen"; } });
            throw;
        }
        catch (Exception ex)
        {
            Mark(host, h =>
            {
                h.Updates!.FailDatabasePackages(components, DisplayText.DatabaseUploadError(ex.Message));
                if (h.Updates.SoftwareCompilationRunning) return;
                h.Result = "Fehler"; h.UpdateFailed = true; h.Phase = "zzzSoftware-Upload fehlgeschlagen"; h.Error = h.Updates.DatabaseUploadError;
            });
            Log(host.Host, "zzzSoftware => FEHLER " + ex.Message);
            return false;
        }
        finally { Mark(host, h => h.Updates!.DatabaseUploadRunning = false); }
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
                if (!activeCompileAfterInstallation) host.Updates!.SoftwareCompilationError = "";
                host.Updates!.PosRestartSkipMessage = "";
                if (!activeRestartPosAfterUpdate) host.Updates.PosRestartError = "";
            }
            // All selected MSI installation attempts have settled. Compilation
            // can overlap the remaining uploads; both must settle before POS restarts.
            var localTargets = installationDatabaseTargets ?? new Lazy<Task<IrisUploadTarget[]>>(() => prepareDatabaseTargets(ct));
            Task compilation = Task.WhenAll(targets.Select(host => installationCompilations.TryGetValue(host, out Task? started)
                ? started : CompileAfterNtcInstallationAsync(host, ct, host.IsLocal ? localTargets : InstallationDatabaseTargets(host, ct))));
            Task uploads = ParallelPhaseAsync(targets, activeDatabaseUploadParallelism, async (host, token) =>
            {
                await PublishLocalSoftwarePackagesAsync(host, token, localTargets);
            }, ct);
            await Task.WhenAll(compilation, uploads);
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
                { Log(host.Host, ev.Phase + ": " + ev.Message); Mark(host, h => h.ApplyProgress(ev)); }, ct);
            if (!result.Success) throw new IOException(FriendlyError(result));
            state.CompletePosRestart(result);
            Log(host.Host, "POS nach Update => " + (result.Skipped ? "ÜBERSPRUNGEN " : "OK ") + result.Message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            state.PosRestartError = "POS-Neustart: " + ex.Message;
            Log(host.Host, "POS nach Update => FEHLER " + ex.Message);
        }
    }
}
