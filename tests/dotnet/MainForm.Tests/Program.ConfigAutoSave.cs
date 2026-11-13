using System.Text.Json.Nodes;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void ConfigAutoSaveTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "config-autosave-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        Directory.CreateDirectory(paths.RootDirectory);
        try
        {
            AppConfigStore.Save(paths.ConfigFile, new UpdaterConfig { Hosts = ["SAVED.invalid"], Username = "saved-user", ScanOnStartup = false }, "Synthetic existing password");
            var raw = JsonNode.Parse(File.ReadAllText(paths.ConfigFile))!.AsObject();
            string encrypted = raw["EncryptedPassword"]!.GetValue<string>();
            raw["FutureOption"] = "retain";
            File.WriteAllText(paths.ConfigFile, raw.ToJsonString());
            var worker = new FakeRunner(); var monitor = new FakeRunner();
            using var form = new MainForm(preview: true, worker, monitor, dataPaths: paths);
            _ = form.Handle;
            byte[] beforeLoad = File.ReadAllBytes(paths.ConfigFile);
            Call(form, "LoadStartupConfig");
            Assert(beforeLoad.SequenceEqual(File.ReadAllBytes(paths.ConfigFile)), "loading configuration never triggers auto-save or overwrites saved values");
            Assert(!All(form).OfType<Button>().Any(button => button.Text == "Konfiguration speichern") && typeof(MainForm).GetField("btnSaveConfig", Private) is null,
                "the manual configuration-save button is removed");
            foreach (var (name, property) in new[]
            {
                ("chkSsl", "UseSsl"), ("chkIrisTls", "IrisUseTls"), ("chkCompileAfterInstallation", "CompileAfterInstallation"),
                ("chkUploadToZzzSoftware", "UploadToZzzSoftware"), ("chkRestartPosAfterUpdate", "RestartPosAfterUpdate"), ("chkScanOnStartup", "ScanOnStartup"),
                ("chkSkipLocalPackageCopy", "SkipLocalPackageCopy"), ("chkSkipRemotePackageCopy", "SkipRemotePackageCopy"), ("chkSkipZipTransfer", "SkipZipTransfer"), ("chkForceUpdate", "ForceUpdate")
            })
            {
                var toggle = Field<CheckBox>(form, name); toggle.Checked = !toggle.Checked;
                var settings = AppConfigStore.Load(paths.ConfigFile).Settings;
                Assert((bool)typeof(UpdaterConfig).GetProperty(property)!.GetValue(settings)! == toggle.Checked, property + " persists immediately after changing its switch");
            }
            Field<NumericUpDown>(form, "nScan").Value = 17;
            Field<NumericUpDown>(form, "nUpdate").Value = 3;
            Field<NumericUpDown>(form, "nHttpsPort").Value = 443;
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
            Field<ComboBox>(form, "useBuild").SelectedItem = "BETA";
            var saved = AppConfigStore.Load(paths.ConfigFile);
            Assert(saved.Settings is { ScanParallelism: 17, UpdateParallelism: 3, HttpsPort: 443, ForceUpdate: true, ExecutionMode: "LocalAndRemote", UseBuild: "BETA" },
                "parallelism, HTTPS port, force update, execution mode and build channel persist without a save action");
            raw = JsonNode.Parse(File.ReadAllText(paths.ConfigFile))!.AsObject();
            Assert(raw["EncryptedPassword"]!.GetValue<string>() == encrypted && raw["FutureOption"]!.GetValue<string>() == "retain",
                "ordinary settings changes retain encrypted password bytes and unknown configuration fields");
            Field<TextBox>(form, "txtUser").Text = "automatic-user";
            Field<TextBox>(form, "txtPassword").Text = "Synthetic changed password";
            Field<TextBox>(form, "txtRemotePath").Text = @"C:\Synthetic\Destination";
            Field<TextBox>(form, "softwarePath").Text = @"C:\Synthetic\Packages";
            Complete(Task.Delay(550));
            saved = AppConfigStore.Load(paths.ConfigFile);
            Assert(saved.Settings.Username == "automatic-user" && saved.Password == "Synthetic changed password" &&
                saved.Settings.RemotePath == @"C:\Synthetic\Destination" && saved.Settings.SoftwarePath == @"C:\Synthetic\Packages" &&
                !File.ReadAllText(paths.ConfigFile).Contains("Synthetic changed password"), "text input is automatically saved after a short pause and passwords remain encrypted");
            Call(form, "ApplyRemoteHosts", new[] { "HOST-A.invalid", "HOST-B.invalid" }, "synthetic editor");
            Assert(AppConfigStore.Load(paths.ConfigFile).Settings.Hosts!.SequenceEqual(["HOST-A.invalid", "HOST-B.invalid"]), "editing the remote host list persists it immediately");
            Call(form, "ApplyRemoteHosts", new[] { "HOST-B.invalid", "HOST-A.invalid" }, "synthetic reorder");
            Assert(AppConfigStore.Load(paths.ConfigFile).Settings.Hosts!.SequenceEqual(["HOST-B.invalid", "HOST-A.invalid"]), "reordering hosts is automatically persisted");
            Call(form, "ApplyRemoteHosts", Array.Empty<string>(), "synthetic clear");
            Assert(AppConfigStore.Load(paths.ConfigFile).Settings.Hosts is { Length: 0 }, "clearing the host list is automatically persisted without saving the managed local host");
            byte[] original = File.ReadAllBytes(paths.ConfigFile);
            var upload = Field<CheckBox>(form, "chkUploadToZzzSoftware");
            using (File.Open(paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read)) upload.Checked = !upload.Checked;
            Assert(original.SequenceEqual(File.ReadAllBytes(paths.ConfigFile)) && Field<HashSet<string>>(form, "pendingConfigFields").Count > 0 &&
                All(form).Single(control => control.Name == "configSaveStatus").Text.Contains("nicht gespeichert"), "a failed save preserves the file, retains pending changes and reports the failure");
            Assert((bool)Call(form, "FlushConfigChanges")! && Field<HashSet<string>>(form, "pendingConfigFields").Count == 0 &&
                AppConfigStore.Load(paths.ConfigFile).Settings.UploadToZzzSoftware == upload.Checked, "pending edits can be saved after the file becomes writable again");
            Field<TextBox>(form, "txtUser").Text = "typed-before-close";
            form.Close();
            Assert(AppConfigStore.Load(paths.ConfigFile).Settings.Username == "typed-before-close", "closing flushes text edits before their debounce interval expires");
            Assert(worker.Requests.IsEmpty && monitor.Requests.IsEmpty, "auto-save verification changes only isolated local configuration fixtures and starts no workers");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Auto-save fixture outside test artifacts.");
            Directory.Delete(root, recursive: true);
        }
    }
}
