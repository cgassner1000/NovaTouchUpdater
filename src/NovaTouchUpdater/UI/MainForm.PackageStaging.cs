namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private async Task<TransferFileRequest[]> PackageFilesAsync(CancellationToken ct)
    {
        var candidates = new List<TransferFileRequest>();
        foreach (string path in new[] { paymentZip, fiscalZip, serverSuiteZip }.Where(path => !string.IsNullOrWhiteSpace(path)))
            candidates.Add(new() { LocalPath = path, RelativePath = Path.GetFileName(path) });
        foreach (MsiPackageRequest package in msiPackages.Values)
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

    private async Task StagePackagesAfterScanAsync(HostState[] scanned, CancellationToken ct)
    {
        if (SelectedUpdateComponents() == UpdateComponents.None || scanned.Length == 0) return;
        foreach (HostState host in scanned.Where(h => !h.IsLocal && activeSkipZipTransfer))
        {
            Log(host.Host, "Paketbereitstellung übersprungen: Installationsdateien nicht übertragen ist aktiviert.");
            if (host.Scanned) Mark(host, h => h.Phase = "Scan abgeschlossen · Remote-Paketübertragung deaktiviert");
        }
        HostState[] targets = scanned.Where(h => h.IsLocal || !activeSkipZipTransfer).ToArray();
        if (targets.Length == 0) return;
        TransferFileRequest[] files;
        try { files = await PackageFilesAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            foreach (HostState host in targets)
            {
                Mark(host, h => { h.Result = "Fehler"; h.Phase = "Paketvorbereitung fehlgeschlagen"; h.Error = string.IsNullOrWhiteSpace(h.Error) ? ex.Message : h.Error + " | " + ex.Message; });
                Log(host.Host, "PAKETVORBEREITUNG => FEHLER " + ex.Message);
            }
            return;
        }
        ResetProgress(targets.Length);
        await ParallelPhaseAsync(targets, (int)nUpdate.Value, async (host, token) =>
        {
            Mark(host, h => { h.Result = "Läuft"; h.Phase = "Update-Pakete werden bereitgestellt"; });
            try
            {
                WorkerRequest request = Request("stage-packages", host, UpdateComponents.None);
                request.TransferFiles = files;
                WorkerResult result = await runner.RunAsync(request, ev =>
                {
                    Log(host.Host, ev.Phase + ": " + ev.Message);
                    Mark(host, h => h.ApplyProgress(ev));
                }, token);
                Mark(host, h =>
                {
                    h.Result = result.Success && h.Scanned ? "Bereit" : "Fehler";
                    h.Phase = result.Success ? (h.Scanned ? "Scan abgeschlossen" : "Scan fehlgeschlagen") + " · Update-Pakete bereitgestellt und geprüft" : "Paketbereitstellung fehlgeschlagen";
                    if (!result.Success) h.Error = string.IsNullOrWhiteSpace(h.Error) ? FriendlyError(result) : h.Error + " | " + FriendlyError(result);
                    if (result.Success) h.SetProgress(100);
                });
                Log(host.Host, "PAKETBEREITSTELLUNG => " + (result.Success ? "OK " + result.Message : "FEHLER " + FriendlyError(result)));
            }
            catch (OperationCanceledException)
            {
                Mark(host, h => { h.Result = "Abgebrochen"; h.Phase = "Paketbereitstellung abgebrochen"; });
                throw;
            }
            catch (Exception ex)
            {
                Mark(host, h => { h.Result = "Fehler"; h.Phase = "Paketbereitstellung fehlgeschlagen"; h.Error = string.IsNullOrWhiteSpace(h.Error) ? ex.Message : h.Error + " | " + ex.Message; });
                Log(host.Host, "PAKETBEREITSTELLUNG => FEHLER " + ex.Message);
            }
            finally { CountCompleted(); }
        }, ct);
    }
}
