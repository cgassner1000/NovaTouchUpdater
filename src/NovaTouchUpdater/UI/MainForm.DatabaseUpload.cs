namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Func<CancellationToken, Task<IrisUploadTarget[]>> prepareDatabaseTargets;
    private Func<CancellationToken, Task<IrisInventoryResult>> readIrisInventory = new IrisDatabaseInventory().ReadAsync;
    private IrisUploadTarget[]? activeDatabaseTargets;
    private bool preparingDatabaseUpload;

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
        if (!host.IsLocal || msiPackages.Count == 0 || host.Updates?.DatabaseUploadCompleted == true) return true;
        try
        {
            Mark(host, h => { h.Result = "Läuft"; h.Phase = "Lokale IRIS-Instanzen für zzzSoftware ermitteln"; });
            IrisUploadTarget[] targets = await prepareDatabaseTargets(ct);
            if (targets.Length == 0)
            {
                Log(host.Host, "zzzSoftware: keine lokalen IRIS-Namespaces gefunden; kein Datenbank-Upload erforderlich.");
                if (host.Updates is { } noDatabase) noDatabase.DatabaseUploadCompleted = true;
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
            var request = new WorkerRequest { Action = "publish-msi-packages", Host = host.Host, LocalExecution = true,
                MsiPackages = packages, DatabaseTargets = targets };
            WorkerResult result = await runner.RunAsync(request, ev =>
            {
                Log(host.Host, ev.Phase + ": " + ev.Message);
                Mark(host, h => h.ApplyProgress(ev));
            }, ct);
            if (!result.Success) throw new IOException(FriendlyError(result));
            if (host.Updates is { } state) state.DatabaseUploadCompleted = true;
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
            Mark(host, h => { h.Result = "Fehler"; h.UpdateFailed = true; h.Phase = "zzzSoftware-Upload fehlgeschlagen"; h.Error = ex.Message; });
            Log(host.Host, "zzzSoftware => FEHLER " + ex.Message);
            return false;
        }
    }
}
