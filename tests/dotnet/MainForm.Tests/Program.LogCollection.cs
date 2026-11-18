using NovaTouchUpdater;
using System.Diagnostics;
using System.IO.Compression;

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
            Assert(Path.GetFileName(first) == "NovaTouch LOG 2026-11-18 14-32" && Path.GetFileName(second) == "NovaTouch LOG 2026-11-18 14-32 (2)" &&
                File.ReadAllText(Path.Combine(first, "existing.log")) == "preserve", "same-minute collections never overwrite earlier logs");
            Assert(File.ReadAllText(Path.Combine(second, "Sammlung.txt")).Contains("host-two.invalid") &&
                File.ReadAllText(Path.Combine(second, "Sammlung.txt")).Contains("14:32:45 +01:00"), "collection metadata identifies the host and actual collection time");
            LogCollectionArchiveTests(root, when.AddHours(1));

            using var form = new MainForm(preview: true, new UpdateRunner(), new FakeRunner(), dataPaths: new AppDataPaths(Path.Combine(root, "data")));
            HostState[] remote = Populate(form);
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
            var local = Field<List<HostState>>(form, "hosts").Single(host => host.IsLocal);
            var runner = new LogCollectionRunner();
            var reports = new List<(string Text, bool Warning)>();
            var explorers = new List<ProcessStartInfo>();
            SetField(form, "createLogCollectionRunner", new Func<IWorkerRunner>(() => runner));
            SetField(form, "logCollectionBaseDirectory", new Func<string>(() => root));
            SetField(form, "showLogCollectionReport", new Action<string, bool>((text, warning) => reports.Add((text, warning))));
            SetField(form, "launchHostExplorer", new Action<ProcessStartInfo>(explorers.Add));
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
                string metadata = ReadLogZipText(request.LogCollectionDirectory + ".zip", "Sammlung.txt");
                Assert(Path.GetDirectoryName(request.LogCollectionDirectory) == Path.Combine(root, "logs") &&
                    metadata.Contains("Ergebnis: 2 Logdatei(en) gesammelt") && !metadata.Contains(request.Password.Length == 0 ? "Synthetic secret" : request.Password) &&
                    host.Result == before && !Field<bool>(form, "busy") && reports.Count == 0 && !Directory.Exists(request.LogCollectionDirectory),
                    "successful collection produces only a ZIP beside the EXE, preserves update results and shows no success popup; local=" + host.IsLocal);
                Assert(explorers.Last().UseShellExecute && explorers.Last().ArgumentList.Single() == Path.Combine(root, "logs"),
                    "successful collection opens the logs directory in Explorer; local=" + host.IsLocal);
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
                ReadLogZipText(runner.Requests.Last().LogCollectionDirectory + ".zip", "Sammlung.txt").Contains("Gesperrte Testdatei"), "partial collections retain their logs and error summary in a ZIP and report the failure");

            runner.WaitForCancel = true;
            SynchronizationContext.SetSynchronizationContext(context);
            Task pending = (Task)Call(form, "CollectHostLogsAsync", local)!;
            Assert(Field<bool>(form, "busy") && Field<Button>(form, "btnCancel").Enabled, "log collection participates in the global cancel action");
            int reportCount = reports.Count;
            int explorerCount = explorers.Count;
            Call(form, "CancelCurrentOperation"); Complete(pending);
            Assert(!Field<bool>(form, "busy") && reports.Count == reportCount && explorers.Count == explorerCount &&
                File.ReadAllText(Path.Combine(runner.Requests.Last().LogCollectionDirectory, "Sammlung.txt")).Contains("Abgebrochen"), "cancellation stops the collector and marks the saved collection incomplete");

            runner.WaitForCancel = false;
            runner.Result = new() { Success = true, Message = "2 Logdatei(en) gesammelt", LogFileCount = 2 };
            SetField(form, "launchHostExplorer", new Action<ProcessStartInfo>(_ => throw new IOException("Synthetic Explorer failure")));
            SynchronizationContext.SetSynchronizationContext(context);
            Complete((Task)Call(form, "CollectHostLogsAsync", local)!);
            Assert(File.Exists(runner.Requests.Last().LogCollectionDirectory + ".zip") && reports.Last().Warning &&
                reports.Last().Text.Contains("ZIP wurde erstellt"), "an Explorer failure keeps the completed ZIP and reports only the folder-opening problem");
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
            string pos = Path.Combine(request.LogCollectionDirectory, "NovaTouch POS");
            Directory.CreateDirectory(pos);
            File.WriteAllBytes(Path.Combine(pos, "KassaPreh.ept"), [0, 255, 1, 128, 42]);
            File.WriteAllText(Path.Combine(pos, "Server.log"), "Synthetic log");
            progress(new() { Phase = "Collect Logs", Message = "Testdateien kopieren", ProgressPercent = 30 });
            if (WaitForCancel) await Task.Delay(Timeout.Infinite, ct);
            return Result;
        }
        public void Dispose() { }
    }

    private static string ReadLogZipText(string path, string relative)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(relative)!.Open());
        return reader.ReadToEnd();
    }

    private static void LogCollectionArchiveTests(string root, DateTimeOffset when)
    {
        string directory = LogCollectionFolder.Create(root, "archive.invalid", when);
        string pos = Path.Combine(directory, "NovaTouch POS"); Directory.CreateDirectory(pos);
        byte[] binary = Enumerable.Range(0, 300000).Select(index => (byte)(index % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(pos, "KassaPreh.ept"), binary);
        LogCollectionArchiveResult? result = null;
        Complete(Task.Run(async () => result = await LogCollectionArchive.CreateAsync(root, directory, CancellationToken.None)));
        Assert(result is { CleanupWarning.Length: 0 } && File.Exists(result.ArchivePath) && !Directory.Exists(directory) && !File.Exists(result.ArchivePath + ".partial"),
            "a completed collection is atomically published as ZIP and removes only its own filled working directory");
        using (var zip = ZipFile.OpenRead(result!.ArchivePath))
        {
            var entry = zip.GetEntry("NovaTouch POS/KassaPreh.ept");
            using var data = new MemoryStream(); using var input = entry!.Open(); input.CopyTo(data);
            Assert(data.ToArray().SequenceEqual(binary) && zip.GetEntry("Sammlung.txt") is not null &&
                zip.Entries.All(item => !item.FullName.StartsWith("NovaTouch LOG ", StringComparison.Ordinal) && !item.FullName.Contains("NovaTouch POS.")),
                "ZIP contains the collected files and folders directly, retaining binary content without a timestamp wrapper");
        }
        string next = LogCollectionFolder.Create(root, "next.invalid", when);
        Assert(Path.GetFileName(next).EndsWith(" (2)"), "existing ZIP files also reserve their timestamp name");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool interrupted = false;
        try { Complete(LogCollectionArchive.CreateAsync(root, next, cancelled.Token)); } catch (OperationCanceledException) { interrupted = true; }
        Assert(interrupted && Directory.Exists(next) && !File.Exists(next + ".zip") && !File.Exists(next + ".zip.partial"),
            "cancelled ZIP creation preserves collected files without publishing an incomplete archive");

        string blocked = Path.Combine(next, "blocked.log"); File.WriteAllText(blocked, "keep on compression failure");
        using (var held = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool failed = false;
            try { Complete(Task.Run(() => LogCollectionArchive.CreateAsync(root, next, CancellationToken.None))); } catch (IOException) { failed = true; }
            Assert(failed && Directory.Exists(next) && !File.Exists(next + ".zip") && !File.Exists(next + ".zip.partial"),
                "failed compression releases partial ZIP handles and preserves the original collection");
        }
    }
}
