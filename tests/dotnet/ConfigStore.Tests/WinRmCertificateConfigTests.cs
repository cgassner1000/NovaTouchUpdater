using System.Text.Json;
using System.Text.Json.Nodes;
using NovaTouchUpdater;

internal static class WinRmCertificateConfigTests
{
    internal static void Run(Action<bool, string> assert)
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "winrm-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "config.json");
        const string certificate = @"C:\Synthetic certificates\ca.pem", key = @"C:\Synthetic certificates\key.pem";
        try
        {
            AppConfigStore.Save(path, new UpdaterConfig { WinRmCertificatePath = certificate, WinRmPrivateKeyPath = key, Hosts = ["POS01.invalid"] }, "synthetic-test-password");
            var loaded = AppConfigStore.Load(path);
            assert(loaded.Settings.WinRmCertificatePath == certificate && loaded.Settings.WinRmPrivateKeyPath == key && loaded.Password == "synthetic-test-password",
                "PEM paths round-trip with existing credentials; no certificate or private-key content is stored");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            string encrypted = json["EncryptedPassword"]!.GetValue<string>();
            json.Remove("WinRmCertificatePath"); json["winrmcertificatepath"] = "old.pem";
            json["FutureField"] = 42;
            File.WriteAllText(path, json.ToJsonString());
            AppConfigStore.SaveWinRmCertificatePaths(path, certificate, key);
            json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            assert(json["WinRmCertificatePath"]!.GetValue<string>() == certificate && !json.ContainsKey("winrmcertificatepath") &&
                json["EncryptedPassword"]!.GetValue<string>() == encrypted && json["FutureField"]!.GetValue<int>() == 42 && json.Last().Key == "Hosts",
                "automatic PEM-path save preserves encrypted credentials, unknown settings and host ordering");
            File.WriteAllText(path, "{invalid");
            bool rejected = false;
            try { AppConfigStore.SaveWinRmCertificatePaths(path, certificate, key); } catch (JsonException) { rejected = true; }
            assert(rejected && File.ReadAllText(path) == "{invalid", "a malformed config is never replaced by certificate-path persistence");
        }
        finally { File.Delete(path); Directory.Delete(root); }
    }
}
