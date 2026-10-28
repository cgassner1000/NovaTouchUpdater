namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private IReadOnlyDictionary<string, string>? preparedPackageHashes;

    private TransferFileRequest[] PackageFileCandidates(UpdateComponents components)
    {
        var candidates = new List<TransferFileRequest>();
        foreach (var (component, path) in new[] { (UpdateComponents.Payment, paymentZip), (UpdateComponents.Fiscal, fiscalZip), (UpdateComponents.ServerSuite, serverSuiteZip) })
            if ((components & component) != 0 && !string.IsNullOrWhiteSpace(path)) candidates.Add(new() { LocalPath = path, RelativePath = Path.GetFileName(path) });
        foreach (MsiPackageRequest package in msiPackages.Where(pair => (pair.Key & components) != 0).Select(pair => pair.Value))
        {
            candidates.Add(new() { LocalPath = package.PayloadPath, RelativePath = Path.GetFileName(package.PayloadPath) });
            candidates.AddRange(package.CompanionFiles.Select(file => new TransferFileRequest { LocalPath = file.LocalPath, RelativePath = file.RelativePath }));
        }
        return candidates.ToArray();
    }

    private async Task PrepareUpdatePackageHashesAsync(UpdateComponents components, CancellationToken ct,
        Func<string, CancellationToken, Task<string>>? hashFile = null)
    {
        preparedPackageHashes = null;
        paymentHash = fiscalHash = serverSuiteHash = "";
        string[] paths = PackageFileCandidates(components).Select(file => Path.GetFullPath(file.LocalPath))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var calculateHash = hashFile ?? WorkerRunner.Sha256Async;
        await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (path, token) =>
        {
            hashes[path] = await calculateHash(path, token);
            Log("SYSTEM", "Paketdatei geprüft: " + Path.GetFileName(path));
        });
        ct.ThrowIfCancellationRequested();
        preparedPackageHashes = hashes;
        string Hash(UpdateComponents component, string path) => (components & component) == 0 ? "" : hashes[Path.GetFullPath(path)];
        paymentHash = Hash(UpdateComponents.Payment, paymentZip);
        fiscalHash = Hash(UpdateComponents.Fiscal, fiscalZip);
        serverSuiteHash = Hash(UpdateComponents.ServerSuite, serverSuiteZip);
        foreach (var (component, package) in msiPackages.Where(pair => (pair.Key & components) != 0))
        {
            package.Sha256 = hashes[Path.GetFullPath(package.PayloadPath)];
            foreach (var file in package.CompanionFiles) file.Sha256 = hashes[Path.GetFullPath(file.LocalPath)];
            Log("SYSTEM", $"MSI {component}: {package.ProductName} {package.ProductVersion}");
        }
    }

    private async Task<TransferFileRequest[]> PackageFilesAsync(UpdateComponents components, CancellationToken ct)
    {
        var files = new Dictionary<string, TransferFileRequest>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NovaTouchUpdater", "PackagePaths")) + Path.DirectorySeparatorChar;
        foreach (TransferFileRequest file in PackageFileCandidates(components))
        {
            ct.ThrowIfCancellationRequested();
            string relative = string.IsNullOrWhiteSpace(file.RelativePath) ? Path.GetFileName(file.LocalPath) : file.RelativePath.Replace('/', '\\');
            if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Ungültiger Paketpfad: " + relative);
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Paketpfad verlässt den Zielordner: " + relative);
            file.RelativePath = Path.GetRelativePath(root, full);
            string local = Path.GetFullPath(file.LocalPath);
            if (!hashes.TryGetValue(local, out string? hash))
            {
                if (preparedPackageHashes is null || !preparedPackageHashes.TryGetValue(local, out hash))
                    hash = await WorkerRunner.Sha256Async(local, ct);
                hashes.Add(local, hash);
            }
            file.Sha256 = hash;
            if (files.TryGetValue(file.RelativePath, out var existing) && existing.Sha256 != hash)
                throw new InvalidDataException("Mehrere Pakete benötigen unterschiedliche Dateien unter demselben Zielnamen: " + file.RelativePath);
            files[file.RelativePath] = file;
        }
        return files.Values.ToArray();
    }

    private async Task ExecuteHostUpdateAsync(HostState host, CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (host.IsLocal && !await StageLocalUpdatePackagesAsync(host, ct)) return;
        await UpdateHostAsync(host, ct, delay);
    }

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
