using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void ScanPipelineTests()
    {
        var worker = new PipelineRunner();
        using (var form = new MainForm(preview: true, worker, new FakeRunner()))
        {
            _ = form.Handle;
            HostState[] hosts = [
                new() { Host = "SLOW.invalid" }, new() { Host = "FAST.invalid" },
                new() { Host = "OFFLINE.invalid" }, new() { Host = "SCAN-ERROR.invalid" },
                new() { Host = "EXCLUDED.invalid", IsSelected = false }
            ];
            Field<List<HostState>>(form, "hosts").AddRange(hosts);
            Call(form, "RebuildRows");
            Field<NumericUpDown>(form, "nScan").Value = 2;
            Task scan = (Task)Call(form, "RunScansAsync")!;
            try
            {
                Complete(worker.FastScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert(!worker.SlowProbeRelease.Task.IsCompleted && !scan.IsCompleted &&
                    worker.Requests.Any(r => r.Host == "FAST.invalid" && r.Action == "scan"),
                    "a connected host starts reading versions while another host is still connecting");
                Assert(worker.Requests.Count == 3 && Field<int>(form, "completed") == 0,
                    "the two-host limit covers the complete connection/version workflow and progress counts completed hosts");

                worker.FastScanRelease.TrySetResult(true);
                Complete(WaitForPipelineAsync(() => Field<int>(form, "completed") == 3));
                Assert(!scan.IsCompleted && hosts[1].Scanned && hosts[1].Payment == "2.3.4" && hosts[1].Result == "Bereit",
                    "a completed host immediately displays its versions without waiting for a slow connection");
                Assert(hosts[2].Result == "Fehler" && !hosts[2].Scanned &&
                    !worker.Requests.Any(r => r.Host == "OFFLINE.invalid" && r.Action == "scan"),
                    "an unreachable host fails independently and never starts a version read");
                Assert(hosts[3].Result == "Fehler" && !hosts[3].Scanned && worker.Requests.Any(r => r.Host == "SCAN-ERROR.invalid" && r.Action == "scan"),
                    "a version-read exception stays local to its host and releases its parallel slot");
            }
            finally
            {
                worker.ReleaseAll();
                Complete(scan);
            }
            Assert(worker.Peak == 2 && hosts[0].Scanned && hosts[0].Result == "Bereit" &&
                Field<int>(form, "completed") == 4 && Field<ProgressBar>(form, "progress").Value == 4 &&
                Field<ProgressBar>(form, "progress").Maximum == 4,
                "the entire scan respects the parallel limit and counts every successful or failed host exactly once");
            Assert(worker.Requests.All(r => r.Host != "EXCLUDED.invalid") &&
                worker.Requests.All(r => r.Action is "probe" or "scan"),
                "the parallel workflow preserves host selection and remains read-only");
        }

        var cancelledWorker = new PipelineRunner();
        using var cancelledForm = new MainForm(preview: true, cancelledWorker, new FakeRunner());
        _ = cancelledForm.Handle;
        Field<List<HostState>>(cancelledForm, "hosts").AddRange([
            new() { Host = "SLOW.invalid" }, new() { Host = "NOT-STARTED.invalid" }
        ]);
        Call(cancelledForm, "RebuildRows");
        Field<NumericUpDown>(cancelledForm, "nScan").Value = 1;
        Task cancelledScan = (Task)Call(cancelledForm, "RunScansAsync")!;
        Complete(cancelledWorker.SlowProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Field<CancellationTokenSource>(cancelledForm, "cancellation").Cancel();
        Complete(cancelledScan);
        Assert(cancelledWorker.Requests.Count == 1 && cancelledWorker.Requests.Single().Action == "probe" &&
            !Field<bool>(cancelledForm, "busy") && !Field<DataGridView>(cancelledForm, "table").Columns["include"].ReadOnly &&
            !Field<bool>(cancelledForm, "connectionScanPaused"),
            "cancellation stops an active connection without starting versions or queued hosts, and restores the UI and monitoring");
    }

    private static async Task WaitForPipelineAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class PipelineRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal TaskCompletionSource<bool> SlowProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> SlowProbeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> FastScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> FastScanRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private int peak;
        internal int Peak => Volatile.Read(ref peak);

        internal void ReleaseAll()
        {
            SlowProbeRelease.TrySetResult(true);
            FastScanRelease.TrySetResult(true);
        }

        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            int count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); }
            while (count > previous && Interlocked.CompareExchange(ref peak, count, previous) != previous);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (request.Action == "probe" && request.Host == "SLOW.invalid")
                {
                    SlowProbeStarted.TrySetResult(true);
                    await SlowProbeRelease.Task.WaitAsync(ct);
                }
                if (request.Action == "probe" && request.Host == "OFFLINE.invalid")
                    return new WorkerResult { Success = false, Error = "Simulated connection timeout" };
                if (request.Action == "scan" && request.Host == "FAST.invalid")
                {
                    FastScanStarted.TrySetResult(true);
                    await FastScanRelease.Task.WaitAsync(ct);
                }
                if (request.Action == "scan" && request.Host == "SCAN-ERROR.invalid")
                    throw new InvalidOperationException("Simulated inventory error");
                return new WorkerResult
                {
                    Success = true, DotNet48Installed = true, PosVersion = "2026.4.1",
                    Services = [new() { Name = "nt.payment", Kind = "payment", ProductVersion = "2.3.4", State = "Running" }]
                };
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public void Dispose() { }
    }
}
