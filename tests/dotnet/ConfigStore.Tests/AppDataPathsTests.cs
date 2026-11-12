using NovaTouchUpdater;

internal static class AppDataPathsTests
{
    internal static void Run(Action<bool, string> assert)
    {
        string fixture = Path.Combine(TestWorkspace.Artifacts, "app-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var paths = new AppDataPaths(Path.Combine(fixture, "local"));
            string exe = Path.Combine(fixture, "exe"), newer = Path.Combine(fixture, "newer-exe");
            Directory.CreateDirectory(exe); Directory.CreateDirectory(newer);
            AppDataMigration empty = paths.InitializeAndMigrate(exe);
            assert(paths.RootDirectory == Path.Combine(fixture, "local", "novacom software gmbh", "NovaTouchUpdater") &&
                empty.ConfigLoadPath == paths.ConfigFile && Directory.Exists(paths.LogsDirectory) && !File.Exists(paths.ConfigFile),
                "user data initializes under the vendor/application directory without manufacturing a config");
            string first = Path.Combine(exe, "config.json"), second = Path.Combine(newer, "config.json");
            AppConfigStore.Save(first, new UpdaterConfig { Hosts = ["OLDER"], IrisUseTls = true }, "Synthetic migration password ✓");
            AppConfigStore.Save(second, new UpdaterConfig { Hosts = ["NEWEST"], CompileAfterInstallation = true }, "Synthetic newer password");
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(-1));
            byte[] olderBytes = File.ReadAllBytes(first), newerBytes = File.ReadAllBytes(second);
            string legacyLogs = Path.Combine(paths.LegacyDirectory, "Logs");
            Directory.CreateDirectory(legacyLogs);
            File.WriteAllText(Path.Combine(legacyLogs, "Update-old.log"), "legacy log");
            File.WriteAllText(Path.Combine(legacyLogs, "Update-same-name.log"), "different legacy log");
            File.WriteAllText(Path.Combine(paths.LogsDirectory, "Update-same-name.log"), "new location log");
            AppDataMigration moved = paths.InitializeAndMigrate(exe, newer, exe);
            assert(moved.ConfigLoadPath == paths.ConfigFile && File.ReadAllBytes(paths.ConfigFile).SequenceEqual(newerBytes) &&
                !File.Exists(first) && !File.Exists(second), "the newest legacy config becomes primary, byte-for-byte, and originals move away from both EXEs");
            string[] backups = Directory.GetFiles(paths.MigratedConfigsDirectory);
            assert(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(olderBytes) &&
                AppConfigStore.Load(backups[0]).Password == "Synthetic migration password ✓" && AppConfigStore.Load(paths.ConfigFile).Password == "Synthetic newer password",
                "different older configurations retain their exact encrypted bytes and decryptable credentials in MigratedConfigs");
            string[] logs = Directory.GetFiles(paths.LogsDirectory);
            assert(logs.Length == 3 && Directory.GetFiles(legacyLogs).Length == 0 &&
                logs.Select(File.ReadAllText).ToHashSet().SetEquals(["legacy log", "different legacy log", "new location log"]),
                "existing logs move under the vendor directory and filename collisions preserve both files");
            paths.InitializeAndMigrate(exe, newer);
            assert(Directory.GetFiles(paths.LogsDirectory).Length == 3 && Directory.GetFiles(paths.MigratedConfigsDirectory).Length == 1,
                "repeated startup migration leaves completed configs and logs unchanged");
            File.WriteAllBytes(first, newerBytes);
            paths.InitializeAndMigrate(exe);
            assert(!File.Exists(first) && Directory.GetFiles(paths.MigratedConfigsDirectory).Length == 1,
                "identical legacy config copies are removed without creating redundant backups");
            File.WriteAllBytes(first, olderBytes);
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
            paths.InitializeAndMigrate(exe);
            assert(File.ReadAllBytes(paths.ConfigFile).SequenceEqual(newerBytes) && Directory.GetFiles(paths.MigratedConfigsDirectory).Length == 1,
                "the central config stays authoritative even when another EXE's legacy copy is newer; existing identical backups are reused");
            paths.InitializeAndMigrate(paths.RootDirectory);
            assert(File.ReadAllBytes(paths.ConfigFile).SequenceEqual(newerBytes), "an EXE in the new data directory cannot migrate or delete its own central config");

            var failures = new AppDataPaths(Path.Combine(fixture, "failed-local"));
            File.WriteAllBytes(first, newerBytes);
            string lockedLogDirectory = Path.Combine(failures.LegacyDirectory, "Logs");
            Directory.CreateDirectory(lockedLogDirectory);
            string lockedLog = Path.Combine(lockedLogDirectory, "locked.log");
            File.WriteAllText(lockedLog, "locked log contents");
            using (File.Open(first, FileMode.Open, FileAccess.Read, FileShare.None))
            using (File.Open(lockedLog, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                AppDataMigration failed = failures.InitializeAndMigrate(exe);
                assert(failed.ConfigLoadPath == first && File.Exists(first) && !File.Exists(failures.ConfigFile) &&
                    File.Exists(lockedLog) && failed.Messages.Length >= 2,
                    "locked originals remain intact, report migration failures and keep the legacy config as startup fallback");
            }
            failures.InitializeAndMigrate(exe);
            assert(File.Exists(failures.ConfigFile) && !File.Exists(first) && !File.Exists(lockedLog) &&
                File.ReadAllText(Path.Combine(failures.LogsDirectory, "locked.log")) == "locked log contents",
                "a later startup retries files that were temporarily locked");

            var readOnly = new AppDataPaths(Path.Combine(fixture, "readonly-local"));
            File.WriteAllBytes(first, olderBytes);
            File.SetAttributes(first, FileAttributes.ReadOnly);
            try
            {
                AppDataMigration copied = readOnly.InitializeAndMigrate(exe);
                assert(copied.ConfigLoadPath == readOnly.ConfigFile && File.Exists(first) &&
                    File.ReadAllBytes(readOnly.ConfigFile).SequenceEqual(olderBytes),
                    "a readable config is adopted even when its original cannot be deleted");
                AppConfigStore.Save(readOnly.ConfigFile, new UpdaterConfig { Hosts = ["EDITABLE"] }, "");
                readOnly.InitializeAndMigrate(exe);
                readOnly.InitializeAndMigrate(exe);
                assert(AppConfigStore.Load(readOnly.ConfigFile).Settings.Hosts!.SequenceEqual(["EDITABLE"]) &&
                    Directory.GetFiles(readOnly.MigratedConfigsDirectory).Length == 1,
                    "the adopted config remains writable and an undeletable old copy creates only one conflicting backup");
            }
            finally { File.SetAttributes(first, FileAttributes.Normal); }

            var legacyData = new AppDataPaths(Path.Combine(fixture, "legacy-local"));
            Directory.CreateDirectory(legacyData.LegacyDirectory);
            string legacyConfig = Path.Combine(legacyData.LegacyDirectory, "config.json");
            File.WriteAllBytes(legacyConfig, olderBytes);
            legacyData.InitializeAndMigrate(Path.Combine(fixture, "missing-exe-directory"));
            assert(!File.Exists(legacyConfig) && File.ReadAllBytes(legacyData.ConfigFile).SequenceEqual(olderBytes),
                "configs already in the old LocalAppData directory are migrated too");
        }
        finally
        {
            if (!Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Migration fixtures must remain in test artifacts.");
            Directory.Delete(fixture, recursive: true);
        }
    }
}
