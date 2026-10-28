namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private async Task<TransferFileRequest[]> PackageFilesAsync(UpdateComponents components, CancellationToken ct)
    {
        var candidates = new List<TransferFileRequest>();
        foreach (var (component, path) in new[] { (UpdateComponents.Payment, paymentZip), (UpdateComponents.Fiscal, fiscalZip), (UpdateComponents.ServerSuite, serverSuiteZip) })
            if ((components & component) != 0 && !string.IsNullOrWhiteSpace(path)) candidates.Add(new() { LocalPath = path, RelativePath = Path.GetFileName(path) });
        foreach (MsiPackageRequest package in msiPackages.Where(pair => (pair.Key & components) != 0).Select(pair => pair.Value))
        {
            candidates.Add(new() { LocalPath = package.PayloadPath, RelativePath = Path.GetFileName(package.PayloadPath) });
            candidates.AddRange(package.CompanionFiles.Select(file => new TransferFileRequest { LocalPath = file.LocalPath, RelativePath = file.RelativePath }));
        }
        var files = new Dictionary<string, TransferFileRequest>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NovaTouchUpdater", "PackagePaths")) + Path.DirectorySeparatorChar;
        foreach (TransferFileRequest file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string relative = string.IsNullOrWhiteSpace(file.RelativePath) ? Path.GetFileName(file.LocalPath) : file.RelativePath.Replace('/', '\\');
            if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Ungültiger Paketpfad: " + relative);
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Paketpfad verlässt den Zielordner: " + relative);
            file.RelativePath = Path.GetRelativePath(root, full);
            string local = Path.GetFullPath(file.LocalPath);
            if (!hashes.TryGetValue(local, out string? hash)) hashes.Add(local, hash = await WorkerRunner.Sha256Async(local, ct));
            file.Sha256 = hash;
            if (files.TryGetValue(file.RelativePath, out var existing) && existing.Sha256 != hash)
                throw new InvalidDataException("Mehrere Pakete benötigen unterschiedliche Dateien unter demselben Zielnamen: " + file.RelativePath);
            files[file.RelativePath] = file;
        }
        return files.Values.ToArray();
    }

    private async Task ExecuteHostUpdateAsync(HostState host, CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (host.IsLocal && !await PublishLocalMsiPackagesAsync(host, ct)) return;
        if (host.IsLocal && !await StageLocalUpdatePackagesAsync(host, ct)) return;
        await UpdateHostAsync(host, ct, delay);
    }

    private async Task<bool> PublishLocalMsiPackagesAsync(HostState host, CancellationToken ct)
    {
        if (!NeedsDatabasePublication(host)) return true;
        string selection = MsiDatabaseSelection();
        try
        {
            ct.ThrowIfCancellationRequested();
            Mark(host, h => { h.Result = "Läuft"; h.Phase = "MSI-Pakete werden in zzzSoftware bereitgestellt"; });
            // Publish every selected MSI, including products absent on this computer.
            WorkerRequest request = Request("publish-msi-packages", host, UpdateComponents.Msi);
            WorkerResult result = await runner.RunAsync(request, ev =>
            {
                Log(host.Host, ev.Phase + ": " + ev.Message);
                Mark(host, h => h.ApplyProgress(ev));
            }, ct);
            if (!result.Success) throw new IOException(FriendlyError(result));
            if (host.Updates is not null)
            {
                host.Updates.DatabasePackagesPublished = true;
                host.Updates.DatabasePackageSelection = selection;
            }
            Log(host.Host, "ZZZSOFTWARE => OK " + result.Message);
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
            Log(host.Host, "ZZZSOFTWARE => FEHLER " + ex.Message);
            return false;
        }
    }

    private bool NeedsDatabasePublication(HostState host) => msiPackages.Count > 0 &&
        (host.Updates?.DatabasePackagesPublished != true || host.Updates.DatabasePackageSelection != MsiDatabaseSelection());

    private string MsiDatabaseSelection() => System.Text.Json.JsonSerializer.Serialize(msiPackages.OrderBy(p => p.Key).Select(p => new
    {
        p.Value.Kind, p.Value.PayloadPath, p.Value.RelativeMsiPath, p.Value.ProductName,
        p.Value.ProductVersion, p.Value.ProductCode, p.Value.Sha256
    }));

    private async Task<bool> StageLocalUpdatePackagesAsync(HostState host, CancellationToken ct)
    {
        if (!host.IsLocal || activeSkipLocalPackageCopy) return true;
        try
        {
            ct.ThrowIfCancellationRequested();
            Mark(host, h => { h.Result = "Läuft"; h.Phase = "Lokale Update-Pakete werden vorbereitet"; });
            TransferFileRequest[] files = await PackageFilesAsync(host.Updates?.Pending ?? SelectedUpdateComponents(), ct);
            if (files.Length == 0) return true;
            WorkerRequest request = Request("stage-packages", host, UpdateComponents.None);
            request.TransferFiles = files;
            WorkerResult result = await runner.RunAsync(request, ev =>
            {
                Log(host.Host, ev.Phase + ": " + ev.Message);
                Mark(host, h => h.ApplyProgress(ev));
            }, ct);
            if (!result.Success) throw new IOException(FriendlyError(result));
            Mark(host, h => { h.Phase = "Lokale Update-Pakete bereitgestellt und geprüft"; h.SetProgress(100); });
            Log(host.Host, "LOKALE PAKETBEREITSTELLUNG => OK " + result.Message);
            return true;
        }
        catch (OperationCanceledException)
        {
            Mark(host, h => { h.Result = "Abgebrochen"; h.Phase = "Lokale Paketbereitstellung abgebrochen"; });
            throw;
        }
        catch (Exception ex)
        {
            Mark(host, h =>
            {
                h.Result = "Fehler"; h.UpdateFailed = true; h.Phase = "Lokale Paketbereitstellung fehlgeschlagen";
                h.Error = string.IsNullOrWhiteSpace(h.Error) ? ex.Message : h.Error + " | " + ex.Message;
            });
            Log(host.Host, "LOKALE PAKETBEREITSTELLUNG => FEHLER " + ex.Message);
            return false;
        }
    }
}
