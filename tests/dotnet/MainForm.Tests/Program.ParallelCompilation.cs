using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void ParallelCompilationTests()
    {
        string payload = Path.Combine(TestWorkspace.Artifacts, "parallel-compile-" + Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllText(payload, "Synthetic MSI; never installed or uploaded.");
        try
        {
            // Keep each scenario's message pump outside the lifetime of its form.
            foreach (var test in new[] {
                (UploadFirst: true, CompileFails: false, UploadFails: false),
                (UploadFirst: false, CompileFails: false, UploadFails: false),
                (UploadFirst: true, CompileFails: true, UploadFails: true),
                (UploadFirst: false, CompileFails: false, UploadFails: true) })
                Complete(ParallelCompilationCaseAsync(payload, test));
            Complete(ParallelCompilationCancellationAsync(payload));
            Complete(ParallelCompilationDiscoveryFailureAsync(payload));
        }
        finally { File.Delete(payload); }
    }

    private static async Task ParallelCompilationCaseAsync(string payload, (bool UploadFirst, bool CompileFails, bool UploadFails) test)
    {
        var worker = new ParallelCompilationRunner();
        using var form = ParallelCompilationForm(worker, payload);
        HostState local = Targets(form).Single();
        var remote = new HostState { Host = "REMOTE-POS.invalid", Scanned = true, PosVersion = "1.0.0",
            Updates = new(UpdateComponents.None) { RunId = local.Updates!.RunId } };
        var targets = new[] { local, remote };
        int preparations = 0;
        var discovery = new TaskCompletionSource<IrisUploadTarget[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ =>
        {
            Interlocked.Increment(ref preparations);
            return discovery.Task;
        }));
        Task run = (Task)Call(form, "CompleteUpdateRunAsync", targets, CancellationToken.None)!;
        try
        {
            discovery.SetResult([new() { Instance = "IRIS", Port = 1972, Namespaces = ["USER"] }]);
            await Task.WhenAll(worker.CompilationStarted.Task, worker.LocalUploadStarted.Task, worker.RemoteUploadStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert(preparations == 1 && !run.IsCompleted && local.Updates is { SoftwareCompilationRunning: true, DatabaseUploadRunning: true },
                "compilation and local/remote uploads really overlap, sharing one IRIS discovery request");
            worker.CompilationProgress!(new WorkerEvent { Phase = "Softwarepakete kompilieren",
                Message = "IRIS:1972/USER · Klasse 10 von 20 · Novacom-NT-BOH.xml", ProgressPercent = 50 });
            worker.UploadProgress!(new WorkerEvent { Phase = "zzzSoftware", Message = "parallel upload: 300 / 500 Bytes", ProgressPercent = 60 });
            Assert(local.Result == "Läuft" && local.ProgressPercent == 50 && local.Phase.EndsWith("Novacom-NT-BOH.xml") &&
                local.Log.Any(line => line.Contains("parallel upload: 300 / 500 Bytes")),
                "upload progress remains logged without replacing the compiler's XML progress or percentage");
            var compilation = new WorkerResult { Success = !test.CompileFails, Changed = !test.CompileFails,
                Error = test.CompileFails ? "Parallel compiler failure" : "" };
            var upload = new WorkerResult { Success = !test.UploadFails, Changed = !test.UploadFails,
                Error = test.UploadFails ? "Parallel upload failure" : "", DatabaseUploadedKinds = test.UploadFails ? [] : ["pos"] };
            if (test.UploadFirst)
            {
                worker.Upload.TrySetResult(upload);
                await WaitForPipelineAsync(() => !local.Updates!.DatabaseUploadRunning);
                Assert(!run.IsCompleted && local.Result == "Läuft" && local.ProgressPercent == 50 &&
                    worker.Requests.All(request => request.Action != "restart-pos"),
                    "an upload finishing or failing first cannot finish the host or restart POS while compilation is running");
                worker.Compilation.TrySetResult(compilation);
            }
            else
            {
                worker.Compilation.TrySetResult(compilation);
                await WaitForPipelineAsync(() => !local.Updates!.SoftwareCompilationRunning);
                Assert(!run.IsCompleted && local.Result == "Läuft" && local.Phase == "zzzSoftware-Upload läuft" &&
                    worker.Requests.All(request => request.Action != "restart-pos"),
                    "compilation finishing first hands the information line back to the pending upload");
                worker.UploadProgress(new WorkerEvent { Phase = "zzzSoftware", Message = "IRIS:1972/USER · Setup.msi", ProgressPercent = 80 });
                Assert(local.ProgressPercent == 80 && local.Phase.Contains("Setup.msi"), "upload progress resumes once compilation finishes");
                worker.Upload.TrySetResult(upload);
            }
            await run;
            Assert(local.Updates!.SoftwareCompilationCompleted == !test.CompileFails && local.Updates.DatabaseUploadCompleted == !test.UploadFails &&
                !local.Updates.SoftwareCompilationRunning && !local.Updates.DatabaseUploadRunning && local.CanRetryUpdate == (test.CompileFails || test.UploadFails),
                "parallel completion retains independent successes and failures in either completion order");
            Assert(local.Error.Contains("Parallel compiler failure") == test.CompileFails && local.Error.Contains("Parallel upload failure") == test.UploadFails,
                "neither parallel worker can erase the other's error");
            Assert(worker.Requests.Count(request => request.Action == "restart-pos") == (test.UploadFails ? 0 : 1),
                "POS restarts run only after both phases settle and all required uploads succeed");
            worker.Requests.Clear();
            worker.ImmediateSuccess = true;
            // Direct finalization simulates retrying only outstanding work.
            await (Task)Call(form, "CompleteUpdateRunAsync", targets, CancellationToken.None)!;
            Assert(worker.Requests.Count(request => request.Action == "compile-software-packages") == (test.CompileFails ? 1 : 0) &&
                worker.Requests.Count(request => request.Action == "publish-msi-packages") == (test.UploadFails ? 2 : 0) && !local.CanRetryUpdate,
                "a parallel retry repeats only failed compilation/uploads, preserving every successful step");
        }
        finally
        {
            discovery.TrySetResult([]);
            worker.Upload.TrySetResult(new WorkerResult { Success = true });
            worker.Compilation.TrySetResult(new WorkerResult { Success = true });
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    private static async Task ParallelCompilationCancellationAsync(string payload)
    {
        var cancelling = new ParallelCompilationRunner();
        using (var form = ParallelCompilationForm(cancelling, payload))
        using (var cancel = new CancellationTokenSource())
        {
            HostState local = Targets(form).Single();
            Task run = (Task)Call(form, "CompleteUpdateRunAsync", new[] { local }, cancel.Token)!;
            try
            {
                await Task.WhenAll(cancelling.CompilationStarted.Task, cancelling.LocalUploadStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                await WaitForPipelineAsync(() => !local.Updates!.DatabaseUploadRunning);
                Assert(!run.IsCompleted && local.Updates!.SoftwareCompilationRunning,
                    "cancelling the upload still waits for an accepted compilation to finish");
                cancelling.Compilation.SetResult(new WorkerResult { Success = true, Changed = true });
                bool cancelled = false;
                try { await run; } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && local.Result == "Abgebrochen" && local.Updates is { SoftwareCompilationCompleted: true, DatabaseUploadCompleted: false } && local.CanRetryUpdate,
                    "parallel cancellation preserves completed compilation and the unfinished upload for a targeted retry");
            }
            finally
            {
                cancelling.Upload.TrySetResult(new WorkerResult { Success = true });
                cancelling.Compilation.TrySetResult(new WorkerResult { Success = true });
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task ParallelCompilationDiscoveryFailureAsync(string payload)
    {
        var discoveryFailure = new ParallelCompilationRunner();
        using (var form = ParallelCompilationForm(discoveryFailure, payload))
        {
            int preparations = 0;
            SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ =>
            {
                preparations++;
                return Task.FromException<IrisUploadTarget[]>(new IOException("Shared discovery failed"));
            }));
            HostState local = Targets(form).Single();
            await (Task)Call(form, "CompleteUpdateRunAsync", new[] { local }, CancellationToken.None)!;
            Assert(preparations == 1 && discoveryFailure.Requests.IsEmpty && local.CanRetryUpdate &&
                local.Updates!.SoftwareCompilationError.Contains("Shared discovery failed") && local.Updates.DatabaseUploadError.Contains("Shared discovery failed"),
                "one failed IRIS discovery is shared and recorded for both pending operations without launching workers");
        }
    }

    private static MainForm ParallelCompilationForm(ParallelCompilationRunner worker, string payload)
    {
        var form = new MainForm(preview: true, worker, new FakeRunner());
        _ = form.Handle;
        Field<CheckBox>(form, "chkCompileAfterInstallation").Checked = true;
        Field<CheckBox>(form, "chkRestartPosAfterUpdate").Checked = true;
        Field<NumericUpDown>(form, "nUpdate").Value = 2;
        Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages").Add(UpdateComponents.Pos,
            new() { Kind = "pos", ProductName = "NovaTouch POS", ProductVersion = "2.0.0", PayloadPath = payload });
        HostState local = Targets(form).Single();
        local.Updates = InstalledNtc();
        local.Scanned = true;
        SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(new[] {
            new IrisUploadTarget { Instance = "IRIS", Port = 1972, Namespaces = ["USER"] }
        })));
        SetField(form, "createPosUpdateRunner", new Func<IWorkerRunner>(() => worker));
        Call(form, "CaptureCredentials");
        return form;
    }

    private sealed class ParallelCompilationRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal TaskCompletionSource<bool> CompilationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> LocalUploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> RemoteUploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<WorkerResult> Compilation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<WorkerResult> Upload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<WorkerEvent>? CompilationProgress, UploadProgress;
        internal bool ImmediateSuccess;
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (ImmediateSuccess || request.Action == "restart-pos") return Task.FromResult(new WorkerResult { Success = true, Changed = true, DatabaseUploadedKinds = ["pos"] });
            if (request.Action == "compile-software-packages")
            {
                Assert(!ct.CanBeCanceled, "parallel compilation retains accepted-host cancellation semantics");
                CompilationProgress = progress;
                CompilationStarted.TrySetResult(true);
                return Compilation.Task;
            }
            if (request.Action == "publish-msi-packages")
            {
                if (request.LocalExecution) { UploadProgress = progress; LocalUploadStarted.TrySetResult(true); }
                else RemoteUploadStarted.TrySetResult(true);
                return Upload.Task.WaitAsync(ct);
            }
            throw new InvalidOperationException("Unexpected parallel compilation action: " + request.Action);
        }
        public void Dispose() { }
    }
}
