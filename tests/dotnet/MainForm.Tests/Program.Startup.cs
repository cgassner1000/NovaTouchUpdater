using System.Net;
using System.Net.Http;
using System.Text.Json;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void StartupChecksTests()
    {
        StartupReleaseOrderingTests();
        StartupUpdateInterruptionTests();
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
        Assert(emptyWorker.Requests.Count == 2 && emptyWorker.Requests.All(r => r.LocalExecution && r.Action is "probe" or "scan") &&
            Targets(empty) is [{ IsLocal: true, Scanned: true }], "an enabled startup scan with an empty remote list checks the local computer");
    }

    private static void StartupReleaseOrderingTests()
    {
        string folder = Path.Combine(TestWorkspace.Artifacts, "startup-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var handler = new StartupReleaseHandler();
            var worker = new FakeRunner { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var form = new MainForm(preview: true, worker, new FakeRunner(), new HttpClient(handler));
            Call(form, "ApplyLoadedConfig", new LoadedConfig(new UpdaterConfig { ExecutionMode = "Local", SoftwarePath = folder, ScanOnStartup = true }, "", null));
            _ = form.Handle;
            string initialPackageStatus = Field<Label>(form, "packageStatus").Text;
            handler.OnRequest = () => Assert(worker.Requests.IsEmpty && !Field<bool>(form, "loadingPackages") &&
                Field<Label>(form, "packageStatus").Text == initialPackageStatus,
                "the updater release request starts before package discovery and local/remote host checks");
            Task startup = (Task)Call(form, "RunApplicationStartupAsync")!;
            try
            {
                Assert(handler.Requests == 1, "the update search begins immediately when application startup starts");
                Complete(WaitForPipelineAsync(() => !worker.Requests.IsEmpty));
                Assert(!handler.Response.Task.IsCompleted && !startup.IsCompleted,
                    "package discovery and host checks proceed while the release endpoint is still responding");
                handler.Response.SetResult(StartupReleaseResponse());
                Complete(WaitForPipelineAsync(() => Field<AppRelease?>(form, "availableRelease") is not null));
                Assert(Field<ToolTip>(form, "updateToolTip").GetToolTip(Field<LinkLabel>(form, "updateNotice"))?.Contains("9999.0.0") == true &&
                    Field<LinkLabel>(form, "updateNotice").Enabled && !worker.Gate!.Task.IsCompleted && !startup.IsCompleted,
                    "the newer-version notice is clickable before a slow host check has finished");
                int confirmations = 0;
                SetField(form, "confirmApplicationUpdate", new Func<AppRelease, bool>(_ => { confirmations++; return false; }));
                Complete((Task)Call(form, "UpdateApplicationAsync")!);
                Assert(confirmations == 1 && !startup.IsCompleted && !Field<CancellationTokenSource>(form, "cancellation").IsCancellationRequested,
                    "clicking the updater notice opens its confirmation during a scan; declining keeps the scan running");
            }
            finally
            {
                handler.Response.TrySetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                worker.Gate!.TrySetResult(true);
                Complete(startup);
            }
            Assert(Targets(form).All(host => host.Scanned), "the independent release check preserves automatic startup host scanning");

            var offline = new StartupReleaseHandler { Fail = true };
            var offlineWorker = new FakeRunner();
            using var offlineForm = new MainForm(preview: true, offlineWorker, new FakeRunner(), new HttpClient(offline));
            Call(offlineForm, "ApplyLoadedConfig", new LoadedConfig(new UpdaterConfig { ExecutionMode = "Local", ScanOnStartup = true }, "", null));
            Complete((Task)Call(offlineForm, "RunApplicationStartupAsync")!);
            Assert(offline.Requests == 1 && offlineWorker.Requests.Count == 2 && Targets(offlineForm).All(host => host.Scanned) &&
                Field<AppRelease?>(offlineForm, "availableRelease") is null,
                "a failed updater release request does not prevent local/remote startup checks");
        }
        finally { Directory.Delete(folder); }
    }

    private static void StartupUpdateInterruptionTests()
    {
        var handler = new StartupReleaseHandler();
        handler.Response.SetResult(StartupReleaseResponse());
        var worker = new FakeRunner { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var form = new MainForm(preview: true, worker, new FakeRunner(), new HttpClient(handler));
        _ = form.Handle;
        Call(form, "ApplyLoadedConfig", new LoadedConfig(new UpdaterConfig { ExecutionMode = "Local", ScanOnStartup = true }, "", null));
        Task startup = (Task)Call(form, "RunApplicationStartupAsync")!;
        try
        {
            Complete(WaitForPipelineAsync(() => !worker.Requests.IsEmpty));
            Assert(Field<bool>(form, "busy") && Field<LinkLabel>(form, "updateNotice").Enabled,
                "read-only startup scanning does not disable the updater link");
            SetField(form, "selfUpdating", true);
            Complete((Task)Call(form, "StopDiscoveryForSelfUpdateAsync", CancellationToken.None)!);
            Complete(startup);
            Assert(!worker.Gate!.Task.IsCompleted && !Field<bool>(form, "busy") && worker.Requests.All(request => request.Action == "probe") &&
                Field<CancellationTokenSource?>(form, "cancellation") is null,
                "self-update preparation cancels and awaits startup workers without waiting for hosts to finish naturally");
        }
        finally { worker.Gate!.TrySetResult(true); Complete(startup); SetField(form, "selfUpdating", false); }

        SetField(form, "loadingPackages", true);
        Call(form, "UpdateButtons");
        Assert(!Field<LinkLabel>(form, "updateNotice").Enabled, "manual package selection remains protected from self-update");
        SetField(form, "startupPackageSearch", true);
        Call(form, "UpdateButtons");
        Assert(Field<LinkLabel>(form, "updateNotice").Enabled, "startup package discovery also permits clicking the updater link");
        SetField(form, "startupPackageSearch", false); SetField(form, "loadingPackages", false);

        var finishOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        Task installation = (Task)Call(form, "InBatchAsync", Array.Empty<HostState>(), (Func<CancellationToken, Task>)(ct => { operationToken = ct; return finishOperation.Task; }))!;
        int confirmations = 0;
        SetField(form, "confirmApplicationUpdate", new Func<AppRelease, bool>(_ => { confirmations++; return true; }));
        try
        {
            Assert(!Field<LinkLabel>(form, "updateNotice").Enabled, "installation and other non-scan batches still disable self-update");
            Complete((Task)Call(form, "UpdateApplicationAsync")!);
            Assert(confirmations == 0 && !operationToken.IsCancellationRequested && !installation.IsCompleted,
                "an updater request cannot confirm or cancel a running installation");
        }
        finally { finishOperation.TrySetResult(); Complete(installation); }
    }

    private static HttpResponseMessage StartupReleaseResponse()
    {
        const string tag = "v9999.0.0";
        string file = $"NovaTouchUpdater-{tag}.exe";
        string json = JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease = false, assets = new[] {
            new { name = file, size = 42, browser_download_url = $"https://github.com/{AppReleaseClient.Repository}/releases/download/{tag}/{file}", digest = "sha256:" + new string('0', 64) }
        }});
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    private sealed class StartupReleaseHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? OnRequest;
        internal int Requests;
        internal bool Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert(request.RequestUri == AppReleaseClient.LatestReleaseUrl, "startup release checks use the configured GitHub release endpoint");
            OnRequest?.Invoke();
            return Fail ? Task.FromException<HttpResponseMessage>(new HttpRequestException("simulated offline endpoint")) : Response.Task.WaitAsync(cancellationToken);
        }
    }
}
