using NovaTouchUpdater;

internal static partial class Program
{
    private static void UserDataConfigTests()
    {
        string fixture = Path.Combine(TestWorkspace.Artifacts, "user-data-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(Path.Combine(fixture, "local"));
        try
        {
            paths.InitializeAndMigrate(Path.Combine(fixture, "exe"));
            AppConfigStore.Save(paths.ConfigFile, new UpdaterConfig { Hosts = ["CENTRAL.invalid"], Username = "central-user",
                CompileAfterInstallation = true, ScanOnStartup = false, ExecutionMode = "LocalAndRemote" }, "");
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner(), dataPaths: paths);
            _ = form.Handle;
            Call(form, "LoadStartupConfig");
            var settings = (UpdaterConfig)Call(form, "CaptureConfig")!;
            Assert(Field<bool>(form, "startupConfigLoaded") && settings.Hosts!.SequenceEqual(["CENTRAL.invalid"]) &&
                settings.Username == "central-user" && settings.CompileAfterInstallation && !settings.ScanOnStartup && settings.ExecutionMode == "LocalAndRemote",
                "startup loads hosts, credentials and options from the central user-data config without requiring a config beside the EXE");
            Field<CheckBox>(form, "chkCompileAfterInstallation").Checked = false;
            AppConfigStore.Save(paths.ConfigFile, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
            Call(form, "LoadStartupConfig");
            Assert(!Field<CheckBox>(form, "chkCompileAfterInstallation").Checked && Field<string>(form, "logFile").Length == 0,
                "central config changes round-trip through the form while design previews keep logging disabled");
        }
        finally
        {
            if (!Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("User-data UI fixtures must remain in test artifacts.");
            Directory.Delete(fixture, recursive: true);
        }
    }
}
