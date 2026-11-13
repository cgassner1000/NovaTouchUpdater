using NovaTouchUpdater;

internal static partial class Program
{
    private static void RunStatusTests()
    {
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        _ = form.Handle;
        EnableAdvanced(form);
        using var canvas = new Panel { Size = form.ClientSize };
        Control content = form.Controls[0];
        canvas.Controls.Add(content); canvas.CreateControl();
        foreach (Control control in All(canvas)) _ = control.Handle;
        try
        {
            HostState[] hosts = [new() { Host = "FIRST.invalid" }, new() { Host = "SECOND.invalid" }];
            Field<List<HostState>>(form, "hosts").AddRange(hosts);
            Call(form, "RebuildRows"); Call(form, "UpdateButtons");
            var retry = Field<Button>(form, "btnRetry");
            Assert(!retry.Visible && !retry.Enabled, "retry is hidden before a run has produced a retryable failure");

            var finish = new TaskCompletionSource<bool>();
            Func<CancellationToken, Task> failOneHost = async _ =>
            {
                Assert(hosts.All(host => host.Result == "Läuft") && !retry.Visible,
                    "all accepted hosts show running immediately, including hosts waiting for their turn");
                hosts[0].Updates = new(UpdateComponents.Payment);
                hosts[0].Updates!.Fail(UpdateComponents.Payment, "simulated installation failure");
                hosts[0].UpdateFailed = true; hosts[0].Result = "Fehler"; hosts[0].Error = "simulated installation failure";
                await finish.Task;
                hosts[1].Result = "Erfolgreich";
            };
            Task run = (Task)Call(form, "InBatchAsync", hosts, failOneHost)!;
            Call(form, "UpdateButtons");
            Assert(!retry.Visible, "a failure cannot expose the retry action while the current run is still active");
            finish.SetResult(true); Complete(run);
            Assert(retry.Visible && retry.Enabled && hosts[0].Result == "Fehler" && hosts[1].Result == "Erfolgreich",
                "a completed failed run exposes retry and retains each host's actual final result");

            hosts[0].IsSelected = false; Call(form, "HostSelectionChanged");
            Assert(retry.Visible && !retry.Enabled, "an unselected failed host keeps retry visible without enabling an invalid selection");
            hosts[0].IsSelected = true; Call(form, "HostSelectionChanged");
            var repair = new TaskCompletionSource<bool>();
            Func<CancellationToken, Task> retryHost = async _ =>
            {
                Call(form, "PaintRow", hosts[0]);
                Assert(hosts[0].Result == "Läuft" && hosts[0].Row!.Cells["result"].Value?.ToString() == "Läuft" &&
                    hosts[0].Error == "" && hosts[0].ProgressPercent is null && hosts[0].CanRetryUpdate &&
                    hosts[0].Updates!.Pending == UpdateComponents.Payment && hosts[0].Updates!.ErrorText.Contains("simulated installation failure"),
                    "retry immediately replaces the old displayed error while preserving pending components and their stored failures");
                Assert(hosts[1].Result == "Erfolgreich", "starting a retry leaves unprocessed hosts' results alone");
                await repair.Task;
                hosts[0].Updates!.Complete(UpdateComponents.Payment, new WorkerResult { Success = true, Changed = true });
                hosts[0].UpdateFailed = false; hosts[0].Result = "Erfolgreich";
            };
            run = (Task)Call(form, "InBatchAsync", new[] { hosts[0] }, retryHost)!;
            Assert(!retry.Visible, "retry is hidden during the next run");
            repair.SetResult(true); Complete(run);
            Assert(!retry.Visible && hosts.All(host => host.Result == "Erfolgreich"), "successful repair removes the retry action");

            hosts[0].Result = "Fehler"; hosts[0].Error = "earlier scan failure";
            Complete((Task)Call(form, "InBatchAsync", hosts, new Func<CancellationToken, Task>(_ => Task.CompletedTask))!);
            Assert(hosts.All(host => host.Result == "Abgeschlossen") && hosts[0].CanRetryUpdate,
                "operations without a specialized result finish cleanly without erasing an earlier update or scan retry");
            var cancelGate = new TaskCompletionSource<bool>();
            run = (Task)Call(form, "InBatchAsync", hosts, new Func<CancellationToken, Task>(async ct =>
            {
                await cancelGate.Task;
                ct.ThrowIfCancellationRequested();
            }))!;
            Field<CancellationTokenSource>(form, "cancellation").Cancel(); cancelGate.SetResult(true); Complete(run);
            Assert(hosts.All(host => host.Result == "Abgebrochen") && hosts[0].CanRetryUpdate,
                "cancellation clears running status for every unfinished host and preserves earlier retry eligibility");
        }
        finally { canvas.Controls.Remove(content); form.Controls.Add(content); }
    }
}
