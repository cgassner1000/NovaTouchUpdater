using System.Collections.Concurrent;
using System.Reflection;
using NovaTouchUpdater;

internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            if (args.Contains("--toggles")) { TogglePaintingTests(); return 0; }
            if (args.Contains("--formatting")) { FormattingTests(); return 0; }
            if (args.Contains("--updates")) { UpdateRetryTests(); return 0; }
            if (args.Contains("--staging")) { PackageCopyOptionsTests(); PackageStagingTests(); return 0; }
            if (args.Contains("--local")) { Complete(PackageCatalogTests()); PackageSelectionTests(); LocalExecutionTests(); return 0; }
            if (args.Contains("--columns")) { ColumnSizingTests(); return 0; }
            if (args.Contains("--parallel")) { ScanTests(); ScanPipelineTests(); return 0; }
            if (args.Contains("--startup")) { StartupChecksTests(); return 0; }
            if (args.Contains("--winrm")) { WinRmPreparationTests(); return 0; }
            if (args.Contains("--sections")) { ExpandableSectionTests(); return 0; }
            if (args.Contains("--hosts")) { HostManagementTests(); SelectionTests(); return 0; }
            if (!args.Contains("--layout"))
            {
                TogglePaintingTests();
                ExpandableSectionTests();
                FormattingTests();
                SelectionTests();
                HostManagementTests();
                StartupChecksTests();
                WinRmPreparationTests();
                ScanTests();
                ScanPipelineTests();
                UpdateRetryTests();
                Complete(PackageCatalogTests());
                PackageSelectionTests();
                LocalExecutionTests();
                PackageStagingTests();
                PackageCopyOptionsTests();
                ColumnSizingTests();
            }
            LayoutTests();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static object? Call(MainForm form, string method, params object[] arguments) =>
        typeof(MainForm).GetMethod(method, Private)!.Invoke(form, arguments);

    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;

    private static void Complete(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(5); }
        Assert(task.IsCompleted, "the simulated operation completes without a real remote worker");
        task.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static HostState[] Populate(MainForm form)
    {
        _ = form.Handle; // preview mode: no config, timers, release query or actual workers
        HostState[] hosts = [
            new() { Host = "PC-A.invalid", Scanned = true, Payment = "1.2.3", Result = "Bereit" },
            new() { Host = "PC-B.invalid", Scanned = true, Payment = "1.2.3", Result = "Fehler", UpdateFailed = true },
            new() { Host = "PC-C.invalid" }
        ];
        Field<List<HostState>>(form, "hosts").AddRange(hosts);
        Call(form, "RebuildRows");
        Call(form, "UpdateSummary");
        Call(form, "UpdateButtons");
        return hosts;
    }

    private static HostState[] Targets(MainForm form) => (HostState[])Call(form, "SelectedHosts")!;
    private static HostState[] Updates(MainForm form, bool retry) => (HostState[])Call(form, "UpdateTargets", retry)!;

    private static void EditSelection(MainForm form, HostState host, bool selected)
    {
        var table = Field<DataGridView>(form, "table");
        table.CurrentCell = host.Row!.Cells["include"];
        Assert(table.BeginEdit(false), "host checkboxes are individually editable");
        ((IDataGridViewEditingCell)table.CurrentCell).EditingCellFormattedValue = selected;
        table.NotifyCurrentCellDirty(true);
        table.EndEdit();
        Application.DoEvents(); // Finish deferred row changes after checkbox commits.
    }

    private static void FormattingTests()
    {
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        var table = Field<DataGridView>(form, "table");
        var errors = new List<string>();
        table.DataError += (_, e) =>
        {
            errors.Add($"row={e.RowIndex}, column={table.Columns[e.ColumnIndex].Name}, context={e.Context}: {e.Exception}");
            e.ThrowException = false; // Record failures instead of opening the native error dialog in a test.
        };
        Control content = form.Controls["mainScrollArea"]!;
        using var canvas = new Panel { Size = form.ClientSize, Font = form.Font };
        canvas.Controls.Add(content);
        canvas.CreateControl();
        foreach (Control control in All(canvas)) _ = control.Handle;
        try
        {
            HostState[] hosts = Populate(form);
            // A row can be formatted before its values are assigned during loading or rebuilding.
            int empty = table.Rows.Add();
            Assert(table.Rows[empty].Cells["include"].FormattedValue is false,
                "a temporarily empty selection cell formats as an unchecked checkbox");
            table.Rows.RemoveAt(empty);
            EditSelection(form, hosts[0], false);
            Call(form, "PaintRow", hosts[0]);
            Call(form, "RebuildRows");
            Call(form, "SetAllHostsSelected", true);
            Call(form, "SetAllHostsSelected", false);
            Assert(table.Rows.Cast<DataGridViewRow>().SelectMany(row => row.Cells.Cast<DataGridViewCell>())
                .All(cell => cell.FormattedValue is { } value && cell.FormattedValueType.IsInstanceOfType(value)),
                "every host cell has the correct display type after rebuilding and changing selection");
            Assert(errors.Count == 0, "loading, rebuilding and formatting host rows raises no DataGridView errors: " + string.Join("\n", errors));
        }
        finally
        {
            canvas.Controls.Remove(content);
            form.Controls.Add(content);
        }
    }

    private static void SelectionTests()
    {
        var worker = new FakeRunner();
        var monitor = new FakeRunner();
        using var form = new MainForm(preview: true, worker, monitor);
        HostState[] hosts = Populate(form);
        var table = Field<DataGridView>(form, "table");
        Assert(hosts.All(h => h.IsSelected) && Targets(form).Length == 3, "all newly loaded hosts are selected by default");
        Assert(Updates(form, false).SequenceEqual(hosts.Take(2)) && Updates(form, true).SequenceEqual(hosts.Skip(1).Take(1)),
            "normal updates require a scan and retries retain their existing eligibility");
        EditSelection(form, hosts[0], false);
        EditSelection(form, hosts[1], false);
        Assert(Targets(form).SequenceEqual(hosts.Skip(2)) && Updates(form, false).Length == 0 && Updates(form, true).Length == 0,
            "unchecked scanned or failed hosts never become update targets");
        Assert(!Field<Button>(form, "btnUpdate").Enabled && !Field<Button>(form, "btnRetry").Enabled,
            "update and retry buttons respect the selected hosts' eligibility");
        EditSelection(form, hosts[1], true);
        Assert(Targets(form).SequenceEqual(hosts.Skip(1)) && Updates(form, false).SequenceEqual(hosts.Skip(1).Take(1)),
            "individual checks form an explicit subset, independent of the focused row");

        var filter = Field<ComboBox>(form, "statusFilter");
        filter.SelectedIndex = 1;
        Assert(table.Rows.Count == 1 && Targets(form).SequenceEqual(hosts.Skip(1)), "a status filter preserves selection of hidden hosts");
        Call(form, "SetAllHostsSelected", true);
        Assert(hosts.All(h => h.IsSelected), "select all includes loaded hosts hidden by the display filter");
        Call(form, "SetAllHostsSelected", false);
        Application.DoEvents();
        Assert(Targets(form) is [{ IsLocal: true }] && hosts.All(h => !h.IsSelected) && Field<Label>(form, "lblSelection").Text == "1 von 4 ausgewählt",
            "clearing all remote checkboxes, including hidden hosts, selects the local fallback and updates the visible count");
        filter.SelectedIndex = 0;
        Assert(hosts.All(h => h.Row!.Cells["include"].Value is false), "rebuilding the table preserves unchecked remote states");
        table.Sort(table.Columns["hostname"], System.ComponentModel.ListSortDirection.Descending);
        HostState sortedHost = table.Rows.Cast<DataGridViewRow>().Select(row => (HostState)row.Tag!).First(h => !h.IsLocal);
        EditSelection(form, sortedHost, true);
        Assert(Targets(form).SequenceEqual([sortedHost]), "checkboxes remain attached to the correct host after sorting");
        table.CurrentCell = sortedHost.Row!.Cells["hostname"];
        typeof(DataGridView).GetMethod("OnKeyDown", Private)!.Invoke(table, [new KeyEventArgs(Keys.Space)]);
        Application.DoEvents();
        Assert(Targets(form) is [{ IsLocal: true }], "Space toggles the last remote checkbox and restores the local target");
        Assert(new[] { "btnWinRm", "btnTransfer", "btnSendCommand", "btnUpdate", "btnRetry" }
            .All(name => !Field<Button>(form, name).Enabled) && Field<Button>(form, "btnScan").Enabled,
            "without selected remote hosts, remote actions are disabled and local checking is available");
        Complete((Task)Call(form, "RunScansAsync")!);
        Complete((Task)Call(form, "TransferFilesAsync")!);
        Complete((Task)Call(form, "SendCommandAsync")!);
        Complete((Task)Call(form, "RefreshConnectionsAsync")!);
        Assert(worker.Requests.Count == 2 && worker.Requests.All(r => r.LocalExecution && r.Action is "probe" or "scan") && monitor.Requests.IsEmpty,
            "empty remote selection checks only the local computer and starts no remote monitoring or command");
        Assert(Field<Button>(form, "btnSaveConfig").Enabled && Field<Button>(form, "btnExport").Enabled,
            "configuration and reporting remain available for the loaded list");
        Call(form, "SetAllHostsSelected", true);
        Assert(Targets(form).Length == 3, "all hosts can be enabled again without reloading the list");
    }

    private static void ScanTests()
    {
        var worker = new FakeRunner { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var monitor = new FakeRunner();
        using var form = new MainForm(preview: true, worker, monitor);
        HostState[] hosts = Populate(form);
        EditSelection(form, hosts[1], false);
        HostState excluded = hosts[1];
        string originalPhase = excluded.Phase, originalPayment = excluded.Payment, originalResult = excluded.Result;
        Task scan = (Task)Call(form, "RunScansAsync")!;
        Assert(Field<DataGridView>(form, "table").Columns["include"].ReadOnly && !Field<Button>(form, "btnSelectAll").Enabled,
            "running work locks host checkboxes and bulk-selection controls");
        Call(form, "SetAllHostsSelected", false);
        Assert(Targets(form).SequenceEqual([hosts[0], hosts[2]]), "the active host snapshot cannot be changed during a batch");
        worker.Gate.SetResult(true);
        Complete(scan);
        Assert(worker.Requests.Count(r => r.Action == "probe") == 2 && worker.Requests.Count(r => r.Action == "scan") == 2 &&
            worker.Requests.All(r => r.Host != excluded.Host), "both connectivity and version phases run only for selected computers");
        Assert(excluded.Phase == originalPhase && excluded.Payment == originalPayment && excluded.Result == originalResult && excluded.Log.Count == 0,
            "unselected host inventory, result and log remain untouched");
        Assert(monitor.Requests.Count == 1 && monitor.Requests.Single().Hosts.SequenceEqual(new[] { hosts[0].Host, hosts[2].Host }),
            "automatic monitoring also receives only selected hosts");
        Assert(!Field<DataGridView>(form, "table").Columns["include"].ReadOnly && Field<Button>(form, "btnSelectAll").Enabled,
            "selection controls unlock after the operation finishes");
    }

    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control item in All(child)) yield return item;
    }

    private static void Layout(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls) Layout(child);
    }

    private static void LayoutTests()
    {
        foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            HostState[] hosts = Populate(form);
            hosts[1].IsSelected = false;
            Call(form, "RebuildRows");
            var table = Field<DataGridView>(form, "table");
            var fonts = All(form).ToDictionary(c => c, c => c.Font);
            form.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * scale, font.Style);
            table.ColumnHeadersDefaultCellStyle.Font = new Font(table.Font, FontStyle.Bold);
            form.MinimumSize = Size.Empty;
            foreach (int width in new[] { 1710, 1000 })
            {
                form.ClientSize = new Size((int)(width * scale), (int)(980 * scale));
                Control content = form.Controls["mainScrollArea"]!;
                using var canvas = new Panel { Size = form.ClientSize, Font = form.Font };
                canvas.Controls.Add(content);
                canvas.CreateControl();
                foreach (Control control in All(canvas)) _ = control.Handle;
                for (int pass = 0; pass < 5; pass++) Layout(canvas);
                Call(form, "ApplyColumnWidths");
                for (int pass = 0; pass < 3; pass++) Layout(canvas);
                Application.DoEvents();
                Rectangle checkbox = table.Rows[0].Cells["include"].ContentBounds;
                Assert(!checkbox.IsEmpty && table.Columns["include"].HeaderText.Length == 0 && table.Columns["include"].Width <= checkbox.Width + 16 * table.DeviceDpi / 96F,
                    $"the untitled selection column has only room for its checkbox at {scale:P0}: header='{table.Columns["include"].HeaderText}', width={table.Columns["include"].Width}, checkbox={checkbox}");
                Rectangle status = table.GetCellDisplayRectangle(table.Columns["result"].Index, 0, false);
                int rightEdge = table.ClientSize.Width - table.Controls.OfType<VScrollBar>().Where(scrollbar => scrollbar.Visible).Sum(scrollbar => scrollbar.Width);
                Assert(status.Width > 0 && Math.Abs(status.Right - rightEdge) <= 1,
                    $"Status reaches the right table edge at {width}px / {scale:P0}: expected={rightEdge}, actual={status.Right}");
                if (width == 1710)
                {
                    Assert(table.ColumnHeadersDefaultCellStyle.WrapMode == DataGridViewTriState.False &&
                        table.Columns.Cast<DataGridViewColumn>().Where(c => c.Name != "include").All(c => c.Width >= c.GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true)),
                        $"all default table headings fit on one line at {scale:P0}");
                    Assert(new[] { "payment", "fiscal", "serversuite" }.All(name => !table.Columns[name].HeaderText.Contains("Ist")),
                        "software headings omit Ist");
                    if (scale == 1F)
                    {
                        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
                        canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "main-host-selection.png"));
                    }
                }
                canvas.Controls.Remove(content);
                form.Controls.Add(content);
            }
        }
    }

    private sealed class FakeRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal TaskCompletionSource<bool>? Gate { get; init; }
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            foreach (string host in request.Hosts) onProgress(new() { Host = host, Connected = true, Message = "Simulated connection" });
            return new WorkerResult { Success = true, DotNet48Installed = true, PosVersion = "2026.4.1",
                Services = [new() { Name = "nt.payment", Kind = "payment", ProductVersion = "1.2.3", State = "Running" }] };
        }
        public void Dispose() { }
    }
}
