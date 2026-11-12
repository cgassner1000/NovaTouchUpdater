using NovaTouchUpdater;
using System.Collections.Concurrent;

internal static partial class Program
{
    private static void AutoCompilationTests()
    {
        string payload = Path.Combine(TestWorkspace.Artifacts, "auto-compile-" + Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllText(payload, "Synthetic NTC payload; never installed.");
        try
        {
            var worker = new AutoCompilationRunner();
            using var form = new MainForm(preview: true, worker, new FakeRunner());
            _ = form.Handle;
            using var canvas = new Panel { Size = form.ClientSize };
            canvas.Controls.Add(form.Controls[0]); canvas.CreateControl();
            var toggle = Field<CheckBox>(form, "chkCompileAfterInstallation");
            Assert(!toggle.Checked && !Field<BetaFeatureGate>(form, "betaFeatures").Enabled,
                "automatic compilation starts disabled and advanced activation is session-only");
            Call(form, "ApplyLoadedConfig", new LoadedConfig(new UpdaterConfig { CompileAfterInstallation = true, IrisUseTls = true }, "", null));
            Assert(toggle.Checked && ((UpdaterConfig)Call(form, "CaptureConfig")!).CompileAfterInstallation &&
                !Field<BetaFeatureGate>(form, "betaFeatures").Enabled,
                "the form restores and captures automatic compilation independently of advanced visibility");
            toggle.Checked = false;
            Assert(!((UpdaterConfig)Call(form, "CaptureConfig")!).CompileAfterInstallation,
                "the form captures the disabled automatic compilation setting too");
            toggle.Checked = true;
            Field<CheckBox>(form, "chkSkipLocalPackageCopy").Checked = true;
            Field<CheckBox>(form, "chkSkipZipTransfer").Checked = true;
            SetField(form, "isProcessElevated", new Func<bool>(() => true));
            Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.Ntc, payload, "2.0.0",
                new MsiPackageRequest { Kind = "ntc", ProductName = "NovaTouch Control", ProductVersion = "2.0.0", PayloadPath = payload }));
            HostState local = Targets(form).Single();
            local.Scanned = true;
            local.InstalledComponents = UpdateComponents.Ntc;
            local.NtcVersion = "1.0.0";
            var inventory = new IrisInventoryResult([
                new IrisInstanceInfo("IRIS-A", @"C:\IRIS-A", "", "2022.1", ["%SYS", "USER", "PROG-DEV"], "", 1972, ""),
                new IrisInstanceInfo("IRIS-B", @"C:\IRIS-B", "", "2022.1", ["%SYS", "TAC"], "", 56772, "")], []);
            IrisUploadTarget[] targets = IrisUploadPlan.Create(inventory, true);
            int preparations = 0;
            bool noTargets = false;
            SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ =>
            {
                preparations++;
                return Task.FromResult(noTargets ? Array.Empty<IrisUploadTarget>() : targets);
            }));
            SetField(form, "confirmUpdate", new Func<string, bool>(text =>
            {
                Assert(text.Contains("Softwarepakete automatisch"), "update confirmation describes automatic compilation and its local database scope");
                return true;
            }));
            worker.OnCompile = (request, progress, ct) =>
            {
                Assert(!ct.CanBeCanceled && !toggle.Enabled && local.Result == "Läuft" && local.Error.Length == 0,
                    "automatic compilation locks settings, clears old displayed errors and lets an accepted host finish");
                Assert(request.LocalExecution && request.Username.Length == 0 && request.IrisUseTls && request.MsiPackages.Length == 0 &&
                    request.DatabaseTargets.Length == 2 && request.DatabaseTargets[0].Namespaces.SequenceEqual(["USER", "PROG-DEV"]) &&
                    request.DatabaseTargets[1].Namespaces.SequenceEqual(["TAC"]) && request.DatabaseTargets.All(target => target.Ssl),
                    "automatic compilation uses every local application namespace and configured TLS without MSI operations");
                progress(new WorkerEvent { Phase = "Softwarepakete kompilieren", Message = "IRIS-A:1972/USER · Klasse 1 von 20 · Novacom-NT-BOH.xml", ProgressPercent = 5 });
                Call(form, "PaintRow", local);
                Assert(local.Row!.Cells["information"].Value?.ToString() == "5 % · IRIS-A:1972/USER · Klasse 1 von 20 · Novacom-NT-BOH.xml",
                    "automatic compilation retains the existing percentage, database, class count and XML progress display");
            };
            worker.FailCompilation = true;
            Complete((Task)Call(form, "RunUpdatesAsync", false)!);
            Assert(worker.Requests.Select(request => request.Action).SequenceEqual(["update", "compile-software-packages", "publish-msi-packages"]) && preparations == 1 &&
                local.Updates is { NtcInstalled: true, Pending: UpdateComponents.None, SoftwareCompilationCompleted: false, DatabaseUploadCompleted: true } &&
                local.CanRetryUpdate && local.Error.Contains("Synthetic compiler error") && local.Phase.Contains("kompilieren fehlgeschlagen"),
                "a real update run compiles only after verified NTC installation, preserves compilation failures and still completes the independent database upload");
            Assert(Field<Button>(form, "btnRetry").Visible && toggle.Enabled,
                "automatic compilation failures expose retry and unlock settings when the batch finishes");
            worker.Requests.Clear(); worker.FailCompilation = false;
            Complete((Task)Call(form, "RunUpdatesAsync", true)!);
            Assert(worker.Requests.ToArray() is [{ Action: "compile-software-packages" }] && local.Updates!.SoftwareCompilationCompleted &&
                !local.CanRetryUpdate && local.Error.Length == 0 && local.Phase.Contains("nach NTC-Installation kompiliert"),
                "retrying the full workflow compiles failed packages without reinstalling NTC or repeating the completed upload");
            worker.Requests.Clear();
            FinishUpdates(form, local);
            Assert(worker.Requests.Count == 0, "completed automatic compilation is not repeated by subsequent finalization");
            worker.OnCompile = null;

            // Without database uploads, a compiler-only failure must still survive
            // the update target filter and be reachable through the normal retry.
            Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked = false;
            local.Updates = InstalledNtc();
            worker.FailCompilation = true;
            Call(form, "CaptureCredentials");
            FinishUpdates(form, local);
            Assert(local.CanRetryUpdate && !local.Updates.DatabaseUploadCompleted, "automatic compilation runs even when zzzSoftware upload is disabled");
            worker.Requests.Clear(); worker.FailCompilation = false;
            Complete((Task)Call(form, "RunUpdatesAsync", true)!);
            Assert(worker.Requests.ToArray() is [{ Action: "compile-software-packages" }] && !local.CanRetryUpdate,
                "compiler-only retry works with database upload and POS restart both disabled");

            foreach (var test in new[] { (Changed: false, Skipped: false), (Changed: false, Skipped: true) })
            {
                local.Updates = new(UpdateComponents.Ntc);
                local.Updates.Complete(UpdateComponents.Ntc, new WorkerResult { Success = true, Changed = test.Changed, Skipped = test.Skipped });
                worker.Requests.Clear(); int before = preparations;
                FinishUpdates(form, local);
                Assert(worker.Requests.Count == 0 && preparations == before && !local.Updates.NtcInstalled,
                    "current or uninstalled NTC does not prepare databases or trigger compilation: skipped=" + test.Skipped);
            }
            local.Updates = new(UpdateComponents.Ntc | UpdateComponents.Payment);
            local.Updates.Complete(UpdateComponents.Payment, new WorkerResult { Success = true, Changed = true });
            local.Updates.Fail(UpdateComponents.Ntc, "NTC installation failed");
            worker.Requests.Clear();
            FinishUpdates(form, local);
            Assert(worker.Requests.Count == 0 && local.Error.Contains("NTC installation failed"),
                "a changed service cannot trigger compilation after a failed NTC installation");
            var remote = new HostState { Host = "REMOTE.invalid", Updates = InstalledNtc() };
            FinishUpdates(form, remote);
            Assert(worker.Requests.Count == 0, "remote hosts never compile as a side effect of local NTC installation");

            local.Updates = InstalledNtc();
            toggle.Checked = false; Call(form, "CaptureCredentials");
            int beforeDisabled = preparations;
            FinishUpdates(form, local);
            Assert(worker.Requests.Count == 0 && preparations == beforeDisabled, "the disabled option performs no compiler or database preparation");
            toggle.Checked = true; Call(form, "CaptureCredentials");
            noTargets = true;
            FinishUpdates(form, local);
            Assert(local.CanRetryUpdate && local.Error.Contains("Keine lokalen IRIS-Namespaces") && !local.Updates.SoftwareCompilationCompleted,
                "missing IRIS targets leave an actionable compiler retry instead of reporting successful compilation");
            noTargets = false;
            FinishUpdates(form, local);
            Assert(local.Updates.SoftwareCompilationCompleted && !local.CanRetryUpdate, "restoring IRIS targets completes the pending compilation without reinstalling NTC");

            local.Updates = InstalledNtc();
            worker.Requests.Clear();
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            bool cancelled = false;
            try { Complete((Task)Call(form, "CompleteUpdateRunAsync", new[] { local }, cancel.Token)!); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && worker.Requests.Count == 0 && !local.Updates.SoftwareCompilationCompleted && local.CanRetryUpdate,
                "cancellation before compilation preserves pending post-install work without launching a compiler");
            worker.OnCompile = (_, _, _) => throw new IOException("Synthetic compiler process failure");
            local.Result = "Läuft";
            FinishUpdates(form, local);
            Assert(local.CanRetryUpdate && local.Error.Contains("Synthetic compiler process failure"), "compiler process exceptions remain visible and retryable");
            toggle.Checked = false; Call(form, "CaptureCredentials");
            FinishUpdates(form, local);
            Assert(!local.CanRetryUpdate && local.Error.Length == 0, "disabling automatic compilation clears its previous error on retry");
        }
        finally { File.Delete(payload); }
    }

    private static HostUpdateState InstalledNtc()
    {
        var state = new HostUpdateState(UpdateComponents.Ntc);
        state.Complete(UpdateComponents.Ntc, new WorkerResult { Success = true, Changed = true });
        return state;
    }

    private sealed class AutoCompilationRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal bool FailCompilation;
        internal Action<WorkerRequest, Action<WorkerEvent>, CancellationToken>? OnCompile;
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            ct.ThrowIfCancellationRequested();
            if (request.Action == "compile-software-packages")
            {
                OnCompile?.Invoke(request, progress, ct);
                return Task.FromResult(new WorkerResult { Success = !FailCompilation, Changed = !FailCompilation,
                    Message = FailCompilation ? "Kompilierung fehlgeschlagen" : "kompiliert", Error = FailCompilation ? "Synthetic compiler error" : "" });
            }
            if (request.Action == "update")
            {
                Assert(request.LocalExecution && request.MsiPackages is [{ Kind: "ntc" }], "the installation worker receives exactly the local NTC package");
                return Task.FromResult(new WorkerResult { Success = true, Changed = true, NtcVersion = "2.0.0",
                    InstalledPrograms = [new InstalledProgramInfo { Kind = "ntc", Version = "2.0.0" }] });
            }
            if (request.Action == "publish-msi-packages") return Task.FromResult(new WorkerResult { Success = true, Changed = true });
            throw new InvalidOperationException("Unexpected automatic compilation action: " + request.Action);
        }
        public void Dispose() { }
    }
}
