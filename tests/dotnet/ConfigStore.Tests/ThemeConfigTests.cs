using System.Text.Json;
using System.Text.Json.Nodes;
using NovaTouchUpdater;

internal static class ThemeConfigTests
{
    internal static void Run(Action<bool, string> assert)
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "theme-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "config.json");
        try
        {
            File.WriteAllText(path, "{}");
            assert(AppConfigStore.Load(path).Settings.Theme == "System", "older configs default to the Windows system theme");
            foreach (string mode in new[] { "Light", "Dark", "System" })
            {
                AppConfigStore.Save(path, new UpdaterConfig { Theme = mode, Hosts = ["THEME.invalid"] }, "Test theme secret");
                assert(AppConfigStore.Load(path).Settings.Theme == mode, mode + " theme survives normal configuration save/load");
            }
            JsonObject config = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            string encrypted = config["EncryptedPassword"]!.GetValue<string>();
            config.Remove("Theme"); config["tHeMe"] = "Light";
            config["FutureField"] = new JsonObject { ["keep"] = 42 };
            File.WriteAllText(path, config.ToJsonString());
            AppConfigStore.SaveTheme(path, "dunkel");
            config = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            assert(config["Theme"]!.GetValue<string>() == "Dark" && !config.ContainsKey("tHeMe") && config["EncryptedPassword"]!.GetValue<string>() == encrypted &&
                config["FutureField"]!["keep"]!.GetValue<int>() == 42 && config.Last().Key == "Hosts", "selective theme save preserves raw credentials, future fields and host ordering");
            AppConfigStore.SaveVisibleHostColumns(path, ["hostname", "ksm"]);
            assert(AppConfigStore.Load(path).Settings.Theme == "Dark", "automatic column saves retain the chosen design");
            File.WriteAllText(path, "{\"Theme\":\"future-mode\"}");
            assert(AppConfigStore.Load(path).Settings.Theme == "System", "unknown design values safely use System");
            File.WriteAllText(path, "{invalid");
            byte[] invalid = File.ReadAllBytes(path);
            bool rejected = false;
            try { AppConfigStore.SaveTheme(path, "Light"); } catch (JsonException) { rejected = true; }
            assert(rejected && invalid.SequenceEqual(File.ReadAllBytes(path)), "a malformed config is retained when an automatic theme save fails");
            string fresh = Path.Combine(root, "new-config.json");
            AppConfigStore.SaveTheme(fresh, "Dark");
            assert(AppConfigStore.Load(fresh).Settings.Theme == "Dark", "theme selection can create a first configuration");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Theme fixture outside artifacts.");
            Directory.Delete(root, recursive: true);
        }
    }
}
