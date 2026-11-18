using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void WinRmPreparationTests()
    {
        SettingsAvailabilityTests();
        foreach ((bool success, bool throws) in new[] { (true, false), (false, false), (false, true) })
        {
            var worker = new DiagnosticRunner { PreparationSucceeds = success, PreparationThrows = throws, FailedHost = success ? "" : "POS02.invalid" };
            using var dialog = new WinRmTestForm(["POS01.invalid", "POS02.invalid"], "test-user", "test-only", 2, true, 443,
                ["POS01.invalid", "POS02.invalid", "UNSELECTED.invalid"]);
            _ = dialog.Handle;
            typeof(WinRmTestForm).GetField("isAuthorityTrusted", Private)!.SetValue(dialog, (Func<WinRmCertificateAuthority, bool>)(_ => true));
            typeof(WinRmTestForm).GetField("trustAuthority", Private)!.SetValue(dialog, (Action<WinRmCertificateAuthority>)(_ => throw new InvalidOperationException("A diagnostic fixture must never change a real trust store.")));
            Task test = dialog.RunDiagnosticsAsync(worker);
            Assert(worker.Requests.Count == 1 && worker.Requests.Single().Action == "winrm-prepare" &&
                worker.Requests.Single().Hosts.SequenceEqual(["POS01.invalid", "POS02.invalid", "UNSELECTED.invalid"]),
                "local preparation receives all loaded remote hosts once, including unselected hosts");
            Assert(worker.Requests.Single().Password.Length == 0 && worker.Requests.Single().Username.Length == 0,
                "local preparation does not receive remote credentials");
            Assert(worker.Requests.Single().UseSsl && worker.Requests.Single().HttpsPort == 443,
                "local preparation receives the selected transport so HTTPS does not configure HTTP TrustedHosts");
            Assert(!test.IsCompleted, "remote diagnostics wait for local WinRM and TrustedHosts preparation");
            worker.Prepared.SetResult(true);
            Complete(test);
            var grid = (DataGridView)All(dialog).Single(c => c.Name == "winRmResults");
            var details = (TextBox)All(dialog).Single(c => c.Name == "winRmDetails");
            Assert(worker.Requests.Count == 3 && grid.Rows[0].Cells["result"].Value is "OK" &&
                (success ? grid.Rows[1].Cells["result"].Value is "OK" : grid.Rows[1].Cells["result"].Value is "Fehler"),
                "every selected host gets its own real diagnostic result even when preparation fails or throws");
            Assert(WinRmField<Label>(dialog, "summary").Text.Contains("2 von 2") &&
                WinRmField<Label>(dialog, "summary").Text.Contains("Hinweis") == !success,
                "completed remote tests and local preparation warnings are reported independently");
            if (!success) Assert(grid.Rows[1].Cells["clientSetup"].Value is WinRmSetupState.Ready && grid.Rows[0].Cells["clientSetup"].Value is WinRmSetupState.None,
                "actual failed hosts offer the setup-copy icon after a local preparation warning");
            Assert(details.Text.Contains(success ? "TrustedHosts" : "Administrator"),
                "local preparation results and actionable failures remain visible in the details");
            Assert(worker.Requests.Where(r => r.Action == "winrm-test").All(r => r.Host != "UNSELECTED.invalid"),
                "only selected hosts receive remote diagnostics");
            if (!throws)
            {
                foreach (Control control in All(dialog)) _ = control.Handle;
                for (int pass = 0; pass < 3; pass++) Layout(dialog);
                var status = All(dialog).Single(c => c.Name == "localWinRmStatus");
                Assert(status.Bounds.Bottom <= grid.Bounds.Top,
                    "local preparation status has its own row above the remote diagnostics");
                using var preview = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                preview.Save(Path.Combine(TestWorkspace.Artifacts, success ? "winrm-preparation.png" : "winrm-preparation-warning.png"));
            }
        }

        var cancelledWorker = new DiagnosticRunner { PreparationSucceeds = true };
        using var cancelled = new WinRmTestForm(["POS01.invalid"], "", "", 1, false);
        _ = cancelled.Handle;
        Task operation = cancelled.RunDiagnosticsAsync(cancelledWorker);
        ((CancellationTokenSource)typeof(WinRmTestForm).GetField("cancellation", Private)!.GetValue(cancelled)!).Cancel();
        Complete(operation);
        Assert(cancelledWorker.Requests.Count == 1 && All(cancelled).OfType<DataGridView>().Single().Rows[0].Cells["result"].Value is "Abgebrochen",
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
        using var visibility = new Panel();
        var winRm = Field<Button>(form, "btnWinRm");
        visibility.Controls.Add(winRm);
        var upload = Field<Button>(form, "btnTransfer");
        visibility.Controls.Add(upload);
        var hostBar = Field<FlowLayoutPanel>(form, "hostSelectionBar");
        visibility.Controls.Add(hostBar);
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
            Field<CheckBox>(form, "chkUploadToZzzSoftware"), Field<CheckBox>(form, "chkRestartPosAfterUpdate")];
        var filter = Field<ComboBox>(form, "statusFilter");
        filter.SelectedIndex = 1;
        foreach (int execution in new[] { 0, 1, 2 })
        {
            mode.SelectedIndex = execution;
            Call(form, "UpdateButtons");
            Assert(hostBar.Visible == (execution != (int)UpdateExecutionMode.Local) && filter.SelectedIndex == 1,
                "the host selection strip is hidden only in Local mode and preserves the remote filter");
            if (execution == (int)UpdateExecutionMode.Local)
                Assert(Field<DataGridView>(form, "table").Rows.Cast<DataGridViewRow>().Single().Tag is HostState { IsLocal: true },
                    "a hidden remote filter cannot hide the local computer");
            Assert(winRm.Visible == (execution != (int)UpdateExecutionMode.Local) && upload.Visible == winRm.Visible &&
                winRm.Text == "WinRM\nkonfigurieren" && ((SizingColumnHeaderCell)Field<DataGridView>(form, "table").Columns["include"].HeaderCell).RefreshAction,
                $"the two-line WinRM configuration button and upload follow local/remote mode {execution}; the scan button is named Versionen prüfen");
            Assert(settings.All(control => control.Enabled) && port.Enabled && Field<Button>(form, "btnWinRm").Enabled,
                $"all settings remain editable in execution mode {execution}");
            var config = (UpdaterConfig)Call(form, "CaptureConfig")!;
            Assert(config.Username == "settings-test-user" && config.UseSsl && config.HttpsPort == 443 &&
                Field<TextBox>(form, "txtPassword").Text == "test-only",
                "switching execution mode retains connection settings for configuration capture");
        }
        filter.SelectedIndex = 0;
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
        internal bool PreparationThrows { get; init; }
        internal string FailedHost { get; init; } = "";
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action == "winrm-prepare")
            {
                await Prepared.Task.WaitAsync(ct);
                if (PreparationThrows) throw new InvalidOperationException("Lokale Vorbereitung: Administratorrechte fehlen.");
                return new() { Success = PreparationSucceeds, Message = "Lokales WinRM und TrustedHosts geprüft.",
                    Error = PreparationSucceeds ? "" : "TrustedHosts: Bitte als Administrator ausführen." };
            }
            Assert(Prepared.Task.IsCompletedSuccessfully, "remote requests wait for preparation to finish, without requiring a local listener");
            Assert(request.UseSsl && request.HttpsPort == 443 && request.Username == "test-user" && request.Password == "test-only",
                "remote diagnostics preserve the selected transport, custom port and credentials");
            bool connected = request.Host != FailedHost;
            return new() { Success = connected, Tcp = connected ? "OK" : "Fehler", Session = connected ? "OK" : "Fehler", Command = connected ? "OK" : "Übersprungen (keine Sitzung)", Error = connected ? "" : "Synthetischer Remote-Verbindungsfehler." };
        }
        public void Dispose() { }
    }
}
