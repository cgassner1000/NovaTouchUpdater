using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void WinRmPreparationTests()
    {
        foreach (bool success in new[] { true, false })
        {
            var worker = new DiagnosticRunner { PreparationSucceeds = success };
            using var dialog = new WinRmTestForm(["POS01.invalid", "POS02.invalid"], "test-user", "test-only", 2, true, 443,
                ["POS01.invalid", "POS02.invalid", "UNSELECTED.invalid"]);
            _ = dialog.Handle;
            Task test = dialog.RunDiagnosticsAsync(worker);
            Assert(worker.Requests.Count == 1 && worker.Requests.Single().Action == "winrm-prepare" &&
                worker.Requests.Single().Hosts.SequenceEqual(["POS01.invalid", "POS02.invalid", "UNSELECTED.invalid"]),
                "local preparation receives all loaded remote hosts once, including unselected hosts");
            Assert(worker.Requests.Single().Password.Length == 0 && worker.Requests.Single().Username.Length == 0,
                "local preparation does not receive remote credentials");
            Assert(!test.IsCompleted, "remote diagnostics wait for local WinRM and TrustedHosts preparation");
            worker.Prepared.SetResult(true);
            Complete(test);
            var grid = (DataGridView)All(dialog).Single(c => c.Name == "winRmResults");
            var details = (TextBox)All(dialog).Single(c => c.Name == "winRmDetails");
            Assert(success ? worker.Requests.Count == 3 && grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells[5].Value is "OK")
                : worker.Requests.Count == 1 && grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells[5].Value is "Nicht gestartet"),
                "remote diagnostics start only after successful preparation and otherwise remain explicitly untested");
            Assert(details.Text.Contains(success ? "TrustedHosts" : "Administrator"),
                "local preparation results and actionable failures remain visible in the details");
            Assert(worker.Requests.Where(r => r.Action == "winrm-test").All(r => r.Host != "UNSELECTED.invalid"),
                "only selected hosts receive remote diagnostics");
            if (success)
            {
                foreach (Control control in All(dialog)) _ = control.Handle;
                for (int pass = 0; pass < 3; pass++) Layout(dialog);
                var status = All(dialog).Single(c => c.Name == "localWinRmStatus");
                Assert(status.Bounds.Bottom <= grid.Bounds.Top,
                    "local preparation status has its own row above the remote diagnostics");
                using var preview = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                preview.Save(Path.Combine(TestWorkspace.Artifacts, "winrm-preparation.png"));
            }
        }

        var cancelledWorker = new DiagnosticRunner { PreparationSucceeds = true };
        using var cancelled = new WinRmTestForm(["POS01.invalid"], "", "", 1, false);
        _ = cancelled.Handle;
        Task operation = cancelled.RunDiagnosticsAsync(cancelledWorker);
        ((CancellationTokenSource)typeof(WinRmTestForm).GetField("cancellation", Private)!.GetValue(cancelled)!).Cancel();
        Complete(operation);
        Assert(cancelledWorker.Requests.Count == 1 && All(cancelled).OfType<DataGridView>().Single().Rows[0].Cells[5].Value is "Abgebrochen",
            "cancelling local preparation prevents all remote tests and marks pending rows cancelled");
    }

    private sealed class DiagnosticRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal TaskCompletionSource<bool> Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool PreparationSucceeds { get; init; }
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action == "winrm-prepare")
            {
                await Prepared.Task.WaitAsync(ct);
                return new() { Success = PreparationSucceeds, Message = "Lokales WinRM und TrustedHosts geprüft.",
                    Error = PreparationSucceeds ? "" : "TrustedHosts: Bitte als Administrator ausführen." };
            }
            Assert(Prepared.Task.IsCompletedSuccessfully && PreparationSucceeds, "remote requests follow successful local preparation");
            Assert(request.UseSsl && request.HttpsPort == 443 && request.Username == "test-user" && request.Password == "test-only",
                "remote diagnostics preserve the selected transport, custom port and credentials");
            return new() { Success = true, Tcp = "OK", Session = "OK", Command = "OK" };
        }
        public void Dispose() { }
    }
}
