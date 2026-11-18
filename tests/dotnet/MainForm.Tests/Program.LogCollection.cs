using NovaTouchUpdater;

internal static partial class Program
{
    private static void LogCollectionTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "collect-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldContext = SynchronizationContext.Current;
        using var context = new WindowsFormsSynchronizationContext();
        try
        {
            var when = new DateTimeOffset(2026, 11, 18, 14, 32, 45, TimeSpan.FromHours(1));
            string first = LogCollectionFolder.Create(root, "host-one.invalid", when);
            File.WriteAllText(Path.Combine(first, "existing.log"), "preserve");
            string second = LogCollectionFolder.Create(root, "host-two.invalid", when);
            Assert(Path.GetFileName(first) == "2026-11-18 14-32" && Path.GetFileName(second) == "2026-11-18 14-32 (2)" &&
                File.ReadAllText(Path.Combine(first, "existing.log")) == "preserve", "same-minute collections never overwrite earlier logs");
            Assert(File.ReadAllText(Path.Combine(second, "Sammlung.txt")).Contains("host-two.invalid") &&
                File.ReadAllText(Path.Combine(second, "Sammlung.txt")).Contains("14:32:45 +01:00"), "collection metadata identifies the host and actual collection time");

            using var form = new MainForm(preview: true, new UpdateRunner(), new FakeRunner(), dataPaths: new AppDataPaths(Path.Combine(root, "data")));
            HostState[] remote = Populate(form);
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
            var local = Field<List<HostState>>(form, "hosts").Single(host => host.IsLocal);
            var runner = new LogCollectionRunner();
            var reports = new List<(string Text, bool Warning)>();
            SetField(form, "createLogCollectionRunner", new Func<IWorkerRunner>(() => runner));
            SetField(form, "logCollectionBaseDirectory", new Func<string>(() => root));
            SetField(form, "showLogCollectionReport", new Action<string, bool>((text, warning) => reports.Add((text, warning))));
            Field<TextBox>(form, "txtUser").Text = "SYNTHETIC\\admin";
            Field<TextBox>(form, "txtPassword").Text = "Synthetic secret, never logged";
            Field<CheckBox>(form, "chkSsl").Checked = true;
            Field<NumericUpDown>(form, "nHttpsPort").Value = 443;
            bool[] selected = Field<List<HostState>>(form, "hosts").Select(host => host.IsSelected).ToArray();
            var menu = Field<ContextMenuStrip>(form, "hostActionsMenu");
            foreach (var host in new[] { remote[1], local })
            {
                SynchronizationContext.SetSynchronizationContext(context);
                string before = host.Result;
                Call(form, "PrepareHostActionsMenu", host);
                menu.Items["hostCollectLogs"]!.PerformClick(); Complete(Field<Task>(form, "hostActionTask"));
                WorkerRequest request = runner.Requests.Last();
                Assert(request.Action == "collect-logs" && request.Host == host.Host && request.LocalExecution == host.IsLocal &&
                    (host.IsLocal ? request.Username.Length == 0 && request.Password.Length == 0 : request.Username == "SYNTHETIC\\admin" && request.Password == "Synthetic secret, never logged" && request.UseSsl && request.HttpsPort == 443),
                    "Collect Logs targets only the clicked host with the correct local/WinRM settings; local=" + host.IsLocal);
                string metadata = File.ReadAllText(Path.Combine(request.LogCollectionDirectory, "Sammlung.txt"));
                Assert(Path.GetDirectoryName(request.LogCollectionDirectory) == Path.Combine(root, "logs") &&
                    metadata.Contains("Ergebnis: 2 Logdatei(en) gesammelt") && !metadata.Contains(request.Password.Length == 0 ? "Synthetic secret" : request.Password) &&
                    host.Result == before && !Field<bool>(form, "busy") && !reports.Last().Warning,
                    "collection output lives beside the EXE and preserves update results without recording credentials; local=" + host.IsLocal);
            }
            Assert(selected.SequenceEqual(Field<List<HostState>>(form, "hosts").Select(host => host.IsSelected)), "collecting logs preserves checkbox selection");
            Assert(runner.Requests.Select(request => request.LogCollectionDirectory).Distinct().Count() == 2, "each host gets its own collection directory");

            remote[0].Connection = "Nicht erreichbar";
            Call(form, "PrepareHostActionsMenu", remote[0]);
            int previous = runner.Requests.Count;
            menu.Items["hostCollectLogs"]!.PerformClick();
            Assert(!menu.Items["hostCollectLogs"]!.Enabled && runner.Requests.Count == previous, "unreachable remotes disable WinRM log collection");
            runner.Result = new() { Success = false, Message = "1 Datei gesammelt; 1 nicht kopiert", LogFileCount = 1, LogCollectionWarnings = ["Gesperrte Testdatei"] };
            SynchronizationContext.SetSynchronizationContext(context);
            Complete((Task)Call(form, "CollectHostLogsAsync", local)!);
            Assert(reports.Last().Warning && reports.Last().Text.Contains("Gesperrte Testdatei") &&
                File.ReadAllText(Path.Combine(runner.Requests.Last().LogCollectionDirectory, "Sammlung.txt")).Contains("Gesperrte Testdatei"), "partial collections report their failures in the dialog and saved summary");

            runner.WaitForCancel = true;
            SynchronizationContext.SetSynchronizationContext(context);
            Task pending = (Task)Call(form, "CollectHostLogsAsync", local)!;
            Assert(Field<bool>(form, "busy") && Field<Button>(form, "btnCancel").Enabled, "log collection participates in the global cancel action");
            int reportCount = reports.Count;
            Call(form, "CancelCurrentOperation"); Complete(pending);
            Assert(!Field<bool>(form, "busy") && reports.Count == reportCount &&
                File.ReadAllText(Path.Combine(runner.Requests.Last().LogCollectionDirectory, "Sammlung.txt")).Contains("Abgebrochen"), "cancellation stops the collector and marks the saved collection incomplete");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(oldContext);
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid collection fixture path");
            Directory.Delete(root, true);
        }
    }

    private sealed class LogCollectionRunner : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal WorkerResult Result = new() { Success = true, Message = "2 Logdatei(en) gesammelt", LogFileCount = 2 };
        internal bool WaitForCancel;
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Add(request);
            progress(new() { Phase = "Collect Logs", Message = "Testdateien kopieren", ProgressPercent = 30 });
            if (WaitForCancel) await Task.Delay(Timeout.Infinite, ct);
            return Result;
        }
        public void Dispose() { }
    }
}
