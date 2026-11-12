using System.Security.Cryptography;

namespace NovaTouchUpdater;

internal sealed record AppDataMigration(string ConfigLoadPath, string[] Messages);

internal sealed class AppDataPaths
{
    internal string RootDirectory { get; }
    internal string ConfigFile => Path.Combine(RootDirectory, "config.json");
    internal string LogsDirectory => Path.Combine(RootDirectory, "Logs");
    internal string MigratedConfigsDirectory => Path.Combine(RootDirectory, "MigratedConfigs");
    internal string LegacyDirectory { get; }

    internal AppDataPaths(string localAppData)
    {
        string local = Path.GetFullPath(localAppData);
        RootDirectory = Path.Combine(local, "novacom software gmbh", "NovaTouchUpdater");
        LegacyDirectory = Path.Combine(local, "NovaTouchUpdater");
    }

    internal static AppDataPaths ForCurrentUser() => new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal AppDataMigration InitializeAndMigrate(params string[] executableDirectories)
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        var messages = new List<string>();
        string[] sources = executableDirectories.Append(LegacyDirectory)
            .Select(directory => Path.GetFullPath(Path.Combine(directory, "config.json")))
            .Where(path => !path.Equals(ConfigFile, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        foreach (string source in sources)
        {
            try
            {
                string target = ConfigFile;
                if (File.Exists(target) && !FilesEqual(source, target))
                {
                    // Keep the current config authoritative. A stable name also
                    // avoids repeated backups when an old copy cannot be deleted.
                    Directory.CreateDirectory(MigratedConfigsDirectory);
                    target = Path.Combine(MigratedConfigsDirectory, "config-" + FileHash(source) + ".json");
                }
                if (!File.Exists(target)) CopyConfigAtomically(source, target);
                if (!FilesEqual(source, target)) throw new IOException("Die Konfiguration wurde zwischenzeitlich geändert oder die Sicherung weicht ab: " + target);
                File.Delete(source);
                messages.Add("Konfiguration übernommen: " + source + " → " + target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A complete copy may already be available even if the EXE's
                // directory does not allow deleting its original config.
                messages.Add("Konfigurationsmigration: " + source + " bleibt erhalten. " + ex.Message);
            }
        }

        int movedLogs = 0;
        string legacyLogs = Path.Combine(LegacyDirectory, "Logs");
        try
        {
            if (Directory.Exists(legacyLogs))
                foreach (string source in Directory.GetFiles(legacyLogs))
                {
                    try
                    {
                        string target = Path.Combine(LogsDirectory, Path.GetFileName(source));
                        if (File.Exists(target)) target = Path.Combine(LogsDirectory,
                            Path.GetFileNameWithoutExtension(source) + "-migrated-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source));
                        File.Move(source, target);
                        movedLogs++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        messages.Add("Logmigration: " + source + " bleibt erhalten. " + ex.Message);
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add("Logmigration: " + ex.Message);
        }
        if (movedLogs > 0) messages.Add(movedLogs + " bestehende Logdatei(en) nach " + LogsDirectory + " verschoben.");
        string loadPath = File.Exists(ConfigFile) ? ConfigFile : sources.FirstOrDefault(File.Exists) ?? ConfigFile;
        return new(loadPath, messages.ToArray());
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool FilesEqual(string first, string second) =>
        new FileInfo(first).Length == new FileInfo(second).Length && FileHash(first) == FileHash(second);

    private static void CopyConfigAtomically(string source, string target)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".config-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            File.Move(temporary, target); // Publish a complete copy; never overwrite.
        }
        finally { File.Delete(temporary); }
    }
}
