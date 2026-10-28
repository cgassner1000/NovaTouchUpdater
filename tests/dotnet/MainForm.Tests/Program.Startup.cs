using NovaTouchUpdater;

internal static partial class Program
{
    private static void StartupChecksTests()
    {
        foreach (bool enabled in new[] { true, false })
        foreach (string mode in new[] { "Remote", "Local", "LocalAndRemote" })
        {
            string path = Path.Combine(TestWorkspace.Artifacts, "startup-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using (var source = new MainForm(preview: true, new FakeRunner(), new FakeRunner()))
                {
                    Call(source, "ApplyLoadedConfig", new LoadedConfig(new UpdaterConfig
                    {
                        Hosts = ["START-A.invalid", "START-B.invalid"], ExecutionMode = mode, ScanOnStartup = enabled
                    }, "", null));
                    AppConfigStore.Save(path, (UpdaterConfig)Call(source, "CaptureConfig")!, "");
                }
                var worker = new FakeRunner { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                var monitor = new FakeRunner();
                using var form = new MainForm(preview: true, worker, monitor);
                _ = form.Handle;
                Call(form, "ApplyLoadedConfig", AppConfigStore.Load(path));
                Assert(Field<CheckBox>(form, "chkScanOnStartup").Checked == enabled,
                    $"startup switch survives form capture, config save/load and startup apply ({mode}, {enabled})");
                HostState[] hosts = Field<List<HostState>>(form, "hosts").ToArray();
                if (mode != "Local")
                    EditSelection(form, hosts.Single(h => h.Host == "START-B.invalid"), false);
                HostState[] targets = Targets(form);
                Task startup = (Task)Call(form, "RunStartupChecksAsync")!;
                if (enabled)
                    Assert(!Field<CheckBox>(form, "chkScanOnStartup").Enabled,
                        "startup scan locks its setting while processing");
                worker.Gate.SetResult(true);
                Complete(startup);
                Assert(enabled
                    ? worker.Requests.Count(r => r.Action == "probe") == targets.Length &&
                      worker.Requests.Count(r => r.Action == "scan") == targets.Length &&
                      targets.All(h => h.Scanned) && Field<Button>(form, "btnUpdate").Enabled
                    : worker.Requests.IsEmpty && hosts.All(h => !h.Scanned) && !Field<Button>(form, "btnUpdate").Enabled,
                    $"startup scanning follows the saved setting and prepares selected targets for updates ({mode}, {enabled})");
                Assert(worker.Requests.All(r => r.Action is "probe" or "scan" && r.Host != "START-B.invalid"),
                    "startup only checks selected hosts and never starts an update");
                Assert(Field<CheckBox>(form, "chkScanOnStartup").Enabled,
                    "startup setting is editable after checks complete");
                if (!enabled)
                {
                    Complete((Task)Call(form, "RunScansAsync")!);
                    Assert(targets.All(h => h.Scanned) && Field<Button>(form, "btnUpdate").Enabled,
                        "manual version checking remains available when automatic scanning is disabled");
                }
            }
            finally { File.Delete(path); }
        }

        var noConfigWorker = new FakeRunner();
        using (var noConfig = new MainForm(preview: true, noConfigWorker, new FakeRunner()))
        {
            Populate(noConfig);
            Complete((Task)Call(noConfig, "RunStartupChecksAsync")!);
            Assert(noConfigWorker.Requests.IsEmpty, "startup does not read versions without a loaded config");
        }
        var emptyWorker = new FakeRunner();
        using var empty = new MainForm(preview: true, emptyWorker, new FakeRunner());
        _ = empty.Handle;
        Call(empty, "ApplyLoadedConfig", new LoadedConfig(new UpdaterConfig { Hosts = [] }, "", null));
        Complete((Task)Call(empty, "RunStartupChecksAsync")!);
        Assert(emptyWorker.Requests.IsEmpty, "an enabled startup scan with no targets starts no workers");
    }
}
