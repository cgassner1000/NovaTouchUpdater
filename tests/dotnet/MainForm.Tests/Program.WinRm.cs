using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void WinRmPreparationTests()
    {
        SettingsAvailabilityTests();
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

        var localWorker = new DiagnosticRunner { PreparationSucceeds = true };
        using var localDialog = new WinRmTestForm([], "", "", 1, false);
        _ = localDialog.Handle;
        Task localTest = localDialog.RunDiagnosticsAsync(localWorker);
        localWorker.Prepared.SetResult(true);
        Complete(localTest);
        Assert(localWorker.Requests.ToArray() is [{ Action: "winrm-prepare", Hosts.Length: 0 }] &&
            All(localDialog).OfType<DataGridView>().Single().Rows.Count == 0 &&
            All(localDialog).OfType<TextBox>().Single().Text.Contains("TrustedHosts"),
            "WinRM can be prepared and diagnosed locally without any remote host or remote credential request");
    }

    private static void SettingsAvailabilityTests()
    {
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        HostState[] hosts = Populate(form);
        var mode = Field<ComboBox>(form, "executionMode");
        var https = Field<CheckBox>(form, "chkSsl");
        var port = Field<NumericUpDown>(form, "nHttpsPort");
        Field<TextBox>(form, "txtUser").Text = "settings-test-user";
        Field<TextBox>(form, "txtPassword").Text = "test-only";
        https.Checked = true;
        port.Value = 443;
        Control[] settings = [Field<TextBox>(form, "txtUser"), Field<TextBox>(form, "txtPassword"), https,
            Field<NumericUpDown>(form, "nScan"), Field<NumericUpDown>(form, "nUpdate"), Field<TextBox>(form, "txtRemotePath"),
            Field<CheckBox>(form, "chkForceUpdate"), Field<CheckBox>(form, "chkScanOnStartup"),
            Field<CheckBox>(form, "chkSkipLocalPackageCopy"), Field<CheckBox>(form, "chkSkipRemotePackageCopy"),
            Field<CheckBox>(form, "chkSkipZipTransfer"), Field<CheckBox>(form, "chkIrisTls"),
            Field<CheckBox>(form, "chkUploadToZzzSoftware"), Field<CheckBox>(form, "chkRestartPosAfterUpdate"),
            Field<Button>(form, "btnSaveConfig")];
        foreach (int execution in new[] { 0, 1, 2 })
        {
            mode.SelectedIndex = execution;
            Assert(settings.All(control => control.Enabled) && port.Enabled && Field<Button>(form, "btnWinRm").Enabled,
                $"all settings remain editable in execution mode {execution}");
            var config = (UpdaterConfig)Call(form, "CaptureConfig")!;
            Assert(config.Username == "settings-test-user" && config.UseSsl && config.HttpsPort == 443 &&
                Field<TextBox>(form, "txtPassword").Text == "test-only",
                "switching execution mode retains connection settings for configuration capture");
        }
        foreach (HostState host in hosts) host.IsSelected = false;
        Call(form, "UpdateButtons");
        Assert(Targets(form) is [{ IsLocal: true }] && settings.All(control => control.Enabled),
            "all settings remain editable when every remote host is unchecked");
        SetField(form, "busy", true);
        Call(form, "UpdateButtons");
        Assert(settings.All(control => !control.Enabled) && !port.Enabled && !Field<Button>(form, "btnWinRm").Enabled,
            "settings and WinRM preparation remain locked during an active operation");
        SetField(form, "busy", false);
        https.Checked = false;
        Call(form, "UpdateButtons");
        Assert(settings.All(control => control.Enabled) && !port.Enabled,
            "settings unlock after an operation while the HTTPS port follows its switch");
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
