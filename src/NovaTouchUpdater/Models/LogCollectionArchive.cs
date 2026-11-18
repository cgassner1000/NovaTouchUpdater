using System.IO.Compression;

namespace NovaTouchUpdater;

internal sealed record LogCollectionArchiveResult(string ArchivePath, string CleanupWarning);

internal static class LogCollectionArchive
{
    internal static async Task<LogCollectionArchiveResult> CreateAsync(string executableDirectory, string collectionDirectory, CancellationToken ct)
    {
        string root = Path.GetFullPath(collectionDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string logs = Path.GetFullPath(Path.Combine(executableDirectory, "logs")).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(root), logs, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith("NovaTouch LOG ", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(root, "Sammlung.txt")))
            throw new InvalidOperationException("Ungültiger Arbeitsordner für die Logsammlung.");
        ct.ThrowIfCancellationRequested();
        string archivePath = root + ".zip";
        string temporary = archivePath + ".partial";
        if (File.Exists(archivePath)) throw new IOException("Die ZIP-Datei existiert bereits: " + archivePath);
        bool ownsTemporary = false;
        try
        {
            var entries = new List<(string Path, string Name, bool Directory)>();
            var pending = new Stack<string>(); pending.Push(root);
            string prefix = Path.GetFileName(root) + "/";
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Verknüpfung im Log-Sammelordner ist nicht erlaubt.");
                string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                entries.Add((directory, prefix + (relative == "." ? "" : relative + "/"), true));
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Verknüpfung im Log-Sammelordner ist nicht erlaubt.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else entries.Add((path, prefix + Path.GetRelativePath(root, path).Replace('\\', '/'), false));
                }
            }
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsTemporary = true;
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var item in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = zip.CreateEntry(item.Name, CompressionLevel.Fastest);
                    DateTime time = item.Directory ? Directory.GetLastWriteTime(item.Path) : File.GetLastWriteTime(item.Path);
                    if (time.Year is >= 1980 and <= 2107) entry.LastWriteTime = time;
                    if (item.Directory) continue;
                    await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using Stream destination = entry.Open();
                    await input.CopyToAsync(destination, 131072, ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            using (var check = ZipFile.OpenRead(temporary))
                if (check.Entries.Count != entries.Count) throw new InvalidDataException("Die Log-ZIP wurde nicht vollständig erstellt.");
            File.Move(temporary, archivePath, overwrite: false);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        string cleanupWarning = "";
        // root was validated as this collection's direct child of the intended logs directory.
        try { Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cleanupWarning = "ZIP erstellt; der Arbeitsordner konnte nicht entfernt werden: " + root + " – " + ex.Message;
        }
        return new(archivePath, cleanupWarning);
    }
}
