using System.Text.Json;
using NovaTouchUpdater;

internal static class HostColumnConfigTests
{
    internal static void Run(Action<bool, string> assert)
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "column-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "config.json");
        try
        {
            File.WriteAllText(path, """
                {"Hosts":["SAVED.invalid"],"Username":"saved-user","EncryptedPassword":"opaque-existing-credential",
                 "PasswordProtectionScope":"LocalMachine","FutureSetting":{"keep":"Grüße ✓"},"visiblehostcolumns":["hostname"]}
                """);
            AppConfigStore.SaveVisibleHostColumns(path, ["hostname", "NTV", " dotnet48 ", "hostname", "unknown"]);
            using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement config = document.RootElement;
                assert(config.GetProperty("VisibleHostColumns").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["hostname", "ntv", "dotnet48"]) &&
                    config.EnumerateObject().Count(property => property.Name.Equals("VisibleHostColumns", StringComparison.OrdinalIgnoreCase)) == 1,
                    "automatic column saving normalizes known IDs and replaces case-insensitive old preferences without duplicate keys");
                assert(config.GetProperty("Username").GetString() == "saved-user" && config.GetProperty("Hosts")[0].GetString() == "SAVED.invalid" &&
                    config.GetProperty("EncryptedPassword").GetString() == "opaque-existing-credential" &&
                    config.GetProperty("PasswordProtectionScope").GetString() == "LocalMachine" &&
                    config.GetProperty("FutureSetting").GetProperty("keep").GetString() == "Grüße ✓" && config.EnumerateObject().Last().Name == "Hosts",
                    "column-only autosave retains stored hosts, opaque credentials and unknown settings without decrypting or rewriting them");
            }
            AppConfigStore.SaveVisibleHostColumns(path, []);
            assert(AppConfigStore.Load(path).Settings.VisibleHostColumns is { Length: 0 }, "an explicitly empty column selection survives loading instead of restoring defaults");
            File.WriteAllText(path, """{"Username":"first","username":"second","VisibleHostColumns":["hostname"],"visiblehostcolumns":["ntv"]}""");
            AppConfigStore.SaveVisibleHostColumns(path, ["ksm"]);
            assert(AppConfigStore.Load(path).Settings.Username == "second" && AppConfigStore.Load(path).Settings.VisibleHostColumns!.SequenceEqual(["ksm"]),
                "case-insensitive preference aliases are replaced without changing unrelated key casing or precedence");
            byte[] saved = File.ReadAllBytes(path);
            bool failed = false;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { AppConfigStore.SaveVisibleHostColumns(path, ["result"]); }
                catch (IOException) { failed = true; }
                assert(failed && File.ReadAllBytes(path).SequenceEqual(saved), "a failed atomic column replacement preserves the complete previous config");
            }
            assert(Directory.GetFiles(root, "*.tmp").Length == 0, "failed automatic column saves leave no temporary files");
            foreach (string malformed in new[] { "[]", "null", "{broken", "{\"Username\":\"first\",\"Username\":\"second\"}" })
            {
                File.WriteAllText(path, malformed); failed = false;
                try { AppConfigStore.SaveVisibleHostColumns(path, ["hostname"]); }
                catch (JsonException) { failed = true; }
                assert(failed && File.ReadAllText(path) == malformed, "unreadable configs cannot be replaced by column preferences");
            }
            File.Delete(path);
            AppConfigStore.SaveVisibleHostColumns(path, ["hostname", "ksm", "ntv"]);
            var fresh = AppConfigStore.Load(path);
            assert(fresh.Settings.VisibleHostColumns!.SequenceEqual(["hostname", "ksm", "ntv"]) && fresh.Password.Length == 0 && fresh.Settings.Hosts is { Length: 0 },
                "the first column choice creates a usable preferences-only config without capturing unsaved form fields");
            AppConfigStore.Save(path, new UpdaterConfig { VisibleHostColumns = ["ntv", "ksm"] }, "");
            assert(AppConfigStore.Load(path).Settings.VisibleHostColumns!.SequenceEqual(["ntv", "ksm"]), "normal config saves also retain the current column preferences");
            string fallback = Path.Combine(root, "legacy.json");
            File.WriteAllText(fallback, """{"Hosts":["LEGACY.invalid"],"Username":"legacy-user"}""");
            File.Delete(path);
            AppConfigStore.SaveVisibleHostColumns(path, ["hostname"], fallback);
            assert(AppConfigStore.Load(path).Settings.Username == "legacy-user" && File.Exists(fallback),
                "a migration fallback config supplies saved settings when the central config does not yet exist");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Column fixtures outside test artifacts.");
            Directory.Delete(root, recursive: true);
        }
    }
}
