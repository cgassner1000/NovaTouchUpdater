using System.Text.Json;
using System.Text.Json.Nodes;
using NovaTouchUpdater;

internal static class AutoSaveConfigTests
{
    internal static void Run(Action<bool, string> assert)
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "autosave-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "config.json");
        try
        {
            File.WriteAllText(path, "{\"Username\":\"saved\",\"EncryptedPassword\":\"unreadable-synthetic\",\"PasswordProtectionScope\":\"future\",\"FutureOption\":42,\"Hosts\":[\"KEEP.invalid\"]}");
            AppConfigStore.SaveChangedSettings(path, new UpdaterConfig { Username = "edited", Hosts = [], ScanParallelism = 17 }, [nameof(UpdaterConfig.Username), nameof(UpdaterConfig.ScanParallelism)]);
            var raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            assert(raw["Username"]!.GetValue<string>() == "edited" && raw["ScanParallelism"]!.GetValue<int>() == 17 && raw["EncryptedPassword"]!.GetValue<string>() == "unreadable-synthetic" &&
                raw["PasswordProtectionScope"]!.GetValue<string>() == "future" && raw["FutureOption"]!.GetValue<int>() == 42 && raw["Hosts"]!.AsArray().Count == 1 && raw.Last().Key == "Hosts",
                "automatic settings saves preserve unreadable credentials, saved hosts, unknown fields and host ordering");
            AppConfigStore.SaveChangedSettings(path, new UpdaterConfig(), [], "Synthetic replacement");
            assert(AppConfigStore.Load(path).Password == "Synthetic replacement" && !File.ReadAllText(path).Contains("Synthetic replacement"), "only an edited password replaces its stored encrypted value");
            AppConfigStore.SaveChangedSettings(path, new UpdaterConfig(), [], "");
            assert(AppConfigStore.Load(path).Password == "", "explicitly clearing the password is persisted");
            File.WriteAllText(path, "{\"Password\":\"Synthetic legacy password\",\"Hosts\":[\"LEGACY.invalid\"]}");
            AppConfigStore.SaveChangedSettings(path, new UpdaterConfig { ForceUpdate = true }, [nameof(UpdaterConfig.ForceUpdate)]);
            raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            assert(!raw.ContainsKey("Password") && AppConfigStore.Load(path).Password == "Synthetic legacy password", "the first automatic save migrates legacy plaintext credentials without losing them");
            File.WriteAllText(path, "{invalid");
            bool failed = false;
            try { AppConfigStore.SaveChangedSettings(path, new UpdaterConfig(), [nameof(UpdaterConfig.UseSsl)]); } catch (JsonException) { failed = true; }
            assert(failed && File.ReadAllText(path) == "{invalid", "automatic saving never replaces malformed configuration files");
        }
        finally { File.Delete(path); Directory.Delete(root); }
    }
}
