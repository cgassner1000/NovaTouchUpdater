using System.Diagnostics;
using NovaTouchUpdater;

internal static partial class Program
{
    private static bool RefreshEnabled(MainForm form) => ((SizingColumnHeaderCell)Field<DataGridView>(form, "table").Columns["include"].HeaderCell).RefreshEnabled;

    private static void EnableAdvanced(MainForm form)
    {
        SetField(form, "showBetaActivated", new Action(() => { }));
        var gate = Field<BetaFeatureGate>(form, "betaFeatures");
        var image = All(form).OfType<AppMark>().Single();
        for (int click = 0; click < 5; click++) gate.RegisterClick(image, true);
    }

    private static void ActionLayoutTests()
    {
        var worker = new FakeRunner();
        using (var form = new MainForm(preview: true, worker, new FakeRunner()))
        {
            _ = form.Handle;
            HostState[] hosts = Populate(form);
            using var canvas = new Panel { Size = form.ClientSize };
            Control content = form.Controls[0];
            canvas.Controls.Add(content); canvas.CreateControl();
            foreach (Control control in All(canvas)) _ = control.Handle;
            var table = Field<DataGridView>(form, "table");
            var navigation = All(canvas).Single(control => control.Name == "configurationNavigation");
            var upload = Field<Button>(form, "btnTransfer");
            var report = Field<Button>(form, "btnExport");
            var retry = Field<Button>(form, "btnRetry");
            var cancel = Field<Button>(form, "btnCancel");
            Assert(upload.Parent == navigation && upload.Text.Length == 0 && report.Parent == navigation && report.Text.Length == 0 && !report.Visible,
                "upload and report use sidebar symbols and the report stays hidden until a run has completed");
            var hostsMenu = All(canvas).OfType<ExpandableSection>().Single(section => section.Name == "hostsCard");
            Assert(All(hostsMenu).Contains(Field<Button>(form, "btnWinRm")) && !All(canvas).OfType<Button>().Any(button => button.Text == "Versionen prüfen"),
                "WinRM configuration belongs to Remote Hosts and table refresh replaces the scan text button");
            Assert(!retry.Visible && !Field<Button>(form, "btnCompileSoftware").Visible &&
                Field<CheckBox>(form, "chkCompileAfterInstallation").Parent!.Name != "erweitertSettingsDivider",
                "manual compilation and retry are hidden before advanced activation");
            hosts[2].IsSelected = false;
            Call(form, "RebuildRows");
            Call(form, "UpdateButtons");
            bool[] selected = hosts.Select(host => host.IsSelected).ToArray();
            Assert(table.Columns.Cast<DataGridViewColumn>().Select(column => (SizingColumnHeaderCell)column.HeaderCell).Count(header => header.RefreshAction) == 1 &&
                ((SizingColumnHeaderCell)table.Columns["include"].HeaderCell).RefreshAction,
                "exactly one refresh icon belongs to the selection column header");
            Assert(!(bool)Call(form, "IsTableRefreshHit", 0, table.Columns["include"].Index, 10, 10)! &&
                !(bool)Call(form, "IsTableRefreshHit", -1, table.Columns["connection"].Index, 10, 10)!,
                "row checkboxes and the connection heading have no refresh action");
            worker.Requests.Clear();
            ClickRefresh();
            Assert(worker.Requests.Count == 4 && worker.Requests.All(request => request.Host != hosts[2].Host),
                "the selection header refresh checks all selected hosts");
            Assert(selected.SequenceEqual(hosts.Select(host => host.IsSelected)) &&
                selected.SequenceEqual(hosts.Select(host => host.Row!.Cells["include"].Value is true)) && report.Visible && report.Enabled,
                "header refresh leaves every selection checkbox unchanged and exposes the completed report");
            hosts[0].Updates = new(UpdateComponents.Payment);
            hosts[0].Updates!.Fail(UpdateComponents.Payment, "Synthetic failure");
            hosts[0].UpdateFailed = true; hosts[0].Result = "Fehler";
            Call(form, "UpdateButtons");
            Assert(!retry.Visible, "a failed run does not expose retry before advanced activation");
            canvas.Controls.Remove(content); form.Controls.Add(content);
            EnableAdvanced(form);
            form.Show(); Application.DoEvents();
            Assert(retry.Visible && Field<Button>(form, "btnCompileSoftware").Visible,
                "advanced activation exposes manual compilation and retry for the existing failure");
            hosts[0].ConnectionState.SetForegroundStatus("Verbunden", true);
            Call(form, "PaintRow", hosts[0]);
            foreach (string mode in new[] { "Light", "Dark" })
            {
                UiTheme.SetMode(mode);
                foreach (int width in new[] { 1710, 1000 })
                {
                    form.ClientSize = new Size(width, 980);
                    for (int pass = 0; pass < 3; pass++) Layout(form);
                    Call(form, "ApplyColumnWidths");
                    Rectangle footer = InForm(form, cancel);
                    Assert(footer.Top >= InForm(form, table).Bottom && InForm(form, retry).Right <= footer.Left && form.ClientRectangle.Contains(footer),
                        "cancel and advanced retry remain ordered under the table at " + width + "px in " + mode);
                    Assert(upload.Top > All(form).Single(control => control.Name == "hostsLauncher").Bottom,
                        "upload stays beneath the remote-host symbol");
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "actions-" + mode.ToLowerInvariant() + "-" + width + ".png"));
                }
            }
            UiTheme.SetMode("System");

            void ClickRefresh()
            {
                var column = table.Columns["include"];
                Rectangle bounds = table.GetCellDisplayRectangle(column.Index, -1, false);
                Assert((bool)Call(form, "IsTableRefreshHit", -1, column.Index, bounds.Width / 2, bounds.Height / 2)!,
                    "the center of the selection header activates refresh");
                var args = new DataGridViewCellMouseEventArgs(column.Index, -1, bounds.Width / 2, bounds.Height / 2,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                typeof(DataGridView).GetMethod("OnCellMouseClick", Private)!.Invoke(table, [args]);
                Complete(Field<Task>(form, "hostScanTask"));
            }
        }
        var blocked = new FakeRunner { Gate = new TaskCompletionSource<bool>() };
        using (var form = new MainForm(preview: true, blocked, new FakeRunner()))
        {
            _ = form.Handle;
            HostState host = Populate(form)[0];
            Task operation = (Task)Call(form, "InBatchAsync", new[] { host }, (Func<CancellationToken, Task>)(ct => Task.WhenAll(
                (Task)Call(form, "DoWorkerAsync", host, "update", ct, UpdateComponents.Payment, false)!,
                (Task)Call(form, "RunSoftwareCompilationAsync", host, Array.Empty<IrisUploadTarget>(), ct)!)))!;
            Assert(blocked.Requests.Count == 2 && Field<Button>(form, "btnCancel").Enabled,
                "an installation and compilation can both be interrupted while active");
            var elapsed = Stopwatch.StartNew();
            typeof(Button).GetMethod("OnClick", Private)!.Invoke(Field<Button>(form, "btnCancel"), [EventArgs.Empty]);
            Complete(operation);
            Assert(elapsed.Elapsed < TimeSpan.FromSeconds(2) && host.Result == "Abgebrochen" && !Field<Button>(form, "btnCancel").Enabled,
                "the cancel action interrupts both active workers promptly and marks the host cancelled");
        }
        using (var workerProcess = new WorkerRunner())
        using (var cancel = new CancellationTokenSource())
        {
            // This isolated worker only reads its input, announces readiness and
            // sleeps. It never probes hosts or changes services, certificates or data.
            string path = (string)typeof(WorkerRunner).GetField("scriptFile", Private)!.GetValue(workerProcess)!;
            File.WriteAllText(path, "$null=[Console]::In.ReadLine(); [Console]::Out.WriteLine('{\"Event\":\"progress\",\"Phase\":\"Fixture ready\"}'); Start-Sleep -Seconds 30");
            var ready = new TaskCompletionSource<bool>();
            Task execution = workerProcess.RunAsync(new WorkerRequest { Action = "cancel-fixture" }, _ => ready.TrySetResult(true), cancel.Token);
            Complete(ready.Task);
            var elapsed = Stopwatch.StartNew();
            cancel.Cancel();
            bool cancelled = false;
            try { Complete(execution); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(2), "cancellation terminates an actual isolated PowerShell worker promptly");
            cancelled = false;
            try { Complete(workerProcess.RunAsync(new WorkerRequest { Action = "cancel-fixture" }, _ => { }, cancel.Token)); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "an already cancelled request never starts another worker");
        }
    }
}
