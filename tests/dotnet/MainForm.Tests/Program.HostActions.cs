using NovaTouchUpdater;

internal static partial class Program
{
    private static void HostActionsTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "host-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        try
        {
            var updates = new UpdateRunner();
            var commands = new HostActionRunner();
            using var form = new MainForm(preview: true, updates, new FakeRunner(), dataPaths: paths);
            _ = form.Handle;
            HostState[] remote = Populate(form);
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
            SetField(form, "createCommandRunner", new Func<IWorkerRunner>(() => commands));
            SetField(form, "confirmHostAction", new Func<string, string, bool>((_, _) => true));
            SetField(form, "showHostCommandReport", new Action<string, string>((_, _) => { }));
            SetField(form, "isProcessElevated", new Func<bool>(() => true));
            SetField(form, "confirmUpdate", new Func<string, bool>(_ => true));
            Field<CheckBox>(form, "chkSkipLocalPackageCopy").Checked = true;
            Field<CheckBox>(form, "chkSkipRemotePackageCopy").Checked = true;
            Field<CheckBox>(form, "chkSkipZipTransfer").Checked = true;
            Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked = false;
            Field<CheckBox>(form, "chkRestartPosAfterUpdate").Checked = false;
            HostState local = Field<List<HostState>>(form, "hosts").Single(host => host.IsLocal);
            remote[0].IsSelected = false; // Context actions must ignore the checkbox selection.
            foreach (HostState host in new[] { remote[0], local })
            {
                host.Scanned = true;
                host.InstalledComponents = UpdateComponents.ZipServices;
                host.Services = [new() { Kind = "payment", Name = "nt.payment.1", ProductVersion = "1.0.0" },
                    new() { Kind = "fiscal", Name = "nt.fiscal.1", ProductVersion = "1.0.0" },
                    new() { Kind = "serversuite", Name = "nt3_server_suite_A", ProductVersion = "1.0.0" }];
                host.PosVersion = "1.0.0";
            }
            Call(form, "RebuildRows");
            var table = Field<DataGridView>(form, "table");
            var columnMenu = Field<ContextMenuStrip>(form, "hostColumnsMenu");
            var rowMenu = Field<ContextMenuStrip>(form, "hostActionsMenu");
            using var context = new WindowsFormsSynchronizationContext();
            var oldContext = SynchronizationContext.Current;
            using var canvas = new Form { Size = new Size(1500, 600), ShowInTaskbar = false };
            Control parent = table.Parent!;
            canvas.Controls.Add(table); canvas.Show(); Application.DoEvents();
            try
            {
                bool[] selected = Field<List<HostState>>(form, "hosts").Select(host => host.IsSelected).ToArray();
                OpenContext(-1, "hostname");
                Assert(columnMenu.Visible && !rowMenu.Visible && table.ContextMenuStrip is null, "only a heading right-click opens the column selection");
                columnMenu.Close();
                foreach (string column in new[] { "include", "hostname", "payment", "information", "result" })
                {
                    OpenContext(remote[0].Row!.Index, column);
                    Assert(rowMenu.Visible && !columnMenu.Visible && ReferenceEquals(Field<HostState>(form, "contextHost"), remote[0]),
                        "every cell of the row opens the actions for that exact host: " + column);
                    rowMenu.Close();
                }
                Assert(selected.SequenceEqual(Field<List<HostState>>(form, "hosts").Select(host => host.IsSelected)), "right-clicking the selection cell does not change any checkbox");
                Assert(rowMenu.Items.OfType<ToolStripMenuItem>().Select(item => item.Text).SequenceEqual(new[]
                    { "Update", "Eingabeaufforderung", "NovaTouch POS neustarten", "Nt.Payment neustarten", "Nt.Fiskal neustarten", "NT3.Server.Suite neustarten", "Client entfernen" }) &&
                    rowMenu.Items.OfType<ToolStripSeparator>().Count() == 2, "host actions have the requested order and separators");
                Rectangle last = table.GetRowDisplayRectangle(table.Rows.Count - 1, false);
                ShowContext(new Point(20, last.Bottom + 30));
                Assert(!rowMenu.Visible && !columnMenu.Visible, "empty table space opens neither the columns nor a stale host menu");

                Call(form, "PrepareHostActionsMenu", remote[1]);
                Assert(new[] { "hostRestartPos", "hostRestartPayment", "hostRestartFiscal", "hostRestartServerSuite" }.All(name => !rowMenu.Items[name]!.Enabled),
                    "unknown or missing installations disable all restart actions");
                remote[1].Services = [new() { Kind = "serversuite", ServiceExists = false }];
                Call(form, "PrepareHostActionsMenu", remote[1]);
                Assert(!rowMenu.Items["hostRestartServerSuite"]!.Enabled, "a registry-only Suite entry does not enable a nonexistent service restart");
                SetField(form, "busy", true);
                Call(form, "PrepareHostActionsMenu", remote[0]);
                Assert(rowMenu.Items.OfType<ToolStripMenuItem>().All(item => !item.Enabled), "a running operation disables conflicting host actions");
                SetField(form, "busy", false);

                remote[0].Connection = "Nicht erreichbar";
                Call(form, "PrepareHostActionsMenu", remote[0]);
                rowMenu.Items["hostUpdate"]!.PerformClick(); rowMenu.Items["hostCommand"]!.PerformClick();
                Assert(!rowMenu.Items["hostUpdate"]!.Enabled && !rowMenu.Items["hostCommand"]!.Enabled && rowMenu.Items["hostRemove"]!.Enabled &&
                    updates.Requests.Count == 0 && commands.Requests.Count == 0,
                    "an unreachable remote disables update and command without starting workers or preventing removal");
                remote[0].Connection = "Verbunden";
                Call(form, "PrepareHostActionsMenu", remote[0]);
                Assert(rowMenu.Items["hostUpdate"]!.Enabled && rowMenu.Items["hostCommand"]!.Enabled, "a reachable remote regains update and command actions");
                local.Connection = "Nicht erreichbar";
                Call(form, "PrepareHostActionsMenu", local);
                Assert(rowMenu.Items["hostUpdate"]!.Enabled && rowMenu.Items["hostCommand"]!.Enabled, "local actions do not depend on a WinRM connection");
                local.Connection = "Lokal";

                foreach (HostState host in new[] { remote[0], local })
                {
                    Call(form, "PrepareHostActionsMenu", host);
                    Assert(rowMenu.Items["hostRemove"]!.Enabled == !host.IsLocal, "the automatic local row cannot be removed");
                    foreach (string action in new[] { "hostRestartPayment", "hostRestartFiscal", "hostRestartServerSuite", "hostRestartPos" })
                    {
                        SynchronizationContext.SetSynchronizationContext(context);
                        commands.Requests.Clear();
                        rowMenu.Items[action]!.PerformClick(); WaitIdle();
                        WorkerRequest request = commands.Requests.Single();
                        Assert(request.Host == host.Host && request.LocalExecution == host.IsLocal &&
                            request.RestartPayment == (action == "hostRestartPayment") && request.RestartFiscal == (action == "hostRestartFiscal") &&
                            request.StartServerSuite == (action == "hostRestartServerSuite") && request.Action == (action == "hostRestartPos" ? "restart-pos" : "restart-services"),
                            action + " targets only the clicked host and service; local=" + host.IsLocal);
                    }
                    commands.Requests.Clear();
                    const string command = "echo \"A&B\" & whoami";
                    Complete((Task<WorkerResult>)Call(form, "RunHostCommandAsync", host, command, CancellationToken.None)!);
                    WorkerRequest cmd = commands.Requests.Single();
                    Assert(cmd.Host == host.Host && cmd.LocalExecution == host.IsLocal && cmd.Action == "command" && cmd.CommandText == command,
                        "the command preserves text and targets exactly one host; local=" + host.IsLocal);
                }

                string zip = Path.Combine(root, "nt.payment_2.0.0.zip"); File.WriteAllText(zip, "Synthetic package, never installed");
                SetField(form, "paymentZip", zip); SetField(form, "paymentVersion", "2.0.0");
                foreach (HostState host in new[] { remote[0], local })
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    updates.Requests.Clear();
                    var updateTask = (Task)Call(form, "UpdateSingleHostAsync", host)!;
                    try { Complete(updateTask); }
                    catch { Console.WriteLine("Update fixture: " + host.Host + "; " + host.Phase + "; requests=" + string.Join(",", updates.Requests.Select(request => request.Action))); throw; }
                    Assert(updates.Requests.Any(request => request.Action == "update") && updates.Requests.All(request => request.Host == host.Host && request.LocalExecution == host.IsLocal),
                        "single-host updates never include other checked hosts; local=" + host.IsLocal);
                }
                Assert(selected.SequenceEqual(Field<List<HostState>>(form, "hosts").Select(host => host.IsSelected)), "commands and updates preserve the complete checkbox selection");

                commands.Requests.Clear(); commands.Fail = true;
                SynchronizationContext.SetSynchronizationContext(context);
                Complete((Task<WorkerResult>)Call(form, "RunHostCommandAsync", remote[0], "exit /b 5", CancellationToken.None)!);
                Assert(remote[0].Result == "Fehler" && remote[0].Error.Contains("Exitcode 5") && commands.Requests.Count == 1, "command failures remain on their own host with output and error details");
                commands.Fail = false; commands.WaitForCancellation = true;
                using (var cancel = new CancellationTokenSource())
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    var task = (Task<WorkerResult>)Call(form, "RunHostCommandAsync", remote[0], "synthetic wait", cancel.Token)!;
                    cancel.Cancel(); Complete(task);
                    Assert(commands.Cancelled && !Field<bool>(form, "busy") && remote[0].Result == "Abgebrochen", "cancelling a command stops its worker and releases the operation state");
                }

                foreach (string mode in new[] { "Light", "Dark" })
                {
                    UiTheme.SetMode(mode);
                    Call(form, "PrepareHostActionsMenu", remote[0]);
                    rowMenu.Show(table, new Point(80, 60)); Application.DoEvents();
                    using var bitmap = new Bitmap(rowMenu.Width, rowMenu.Height);
                    rowMenu.DrawToBitmap(bitmap, rowMenu.ClientRectangle);
                    bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "host-actions-" + mode.ToLowerInvariant() + ".png"));
                    rowMenu.Close();
                    using var dialog = new HostCommandDialog("PC-A.invalid", false, (_, _) => Task.FromResult(new WorkerResult { Success = true, Message = "Synthetic output" }));
                    dialog.Show(); Application.DoEvents();
                    var input = (TextBox)dialog.Controls.Find("command", true).Single();
                    var execute = (Button)dialog.Controls.Find("execute", true).Single();
                    var close = (Button)dialog.Controls.Find("close", true).Single();
                    Assert(!execute.Enabled, "an empty command cannot be sent; theme=" + mode);
                    input.Text = "synthetic echo"; execute.PerformClick(); Application.DoEvents();
                    Assert(((TextBox)dialog.Controls.Find("output", true).Single()).Text == "Synthetic output" && input.Enabled && execute.Enabled,
                        "the command dialog displays output and permits the next command; theme=" + mode);
                    Assert(dialog.RectangleToScreen(dialog.ClientRectangle).Contains(close.RectangleToScreen(close.ClientRectangle)), "command dialog buttons fit inside the window; theme=" + mode);
                    using var preview = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(preview, new Rectangle(Point.Empty, dialog.Size));
                    preview.Save(Path.Combine(TestWorkspace.Artifacts, "host-command-" + mode.ToLowerInvariant() + ".png"));
                    dialog.Close();
                }
                UiTheme.SetMode("System");
                Call(form, "PrepareHostActionsMenu", remote[0]);
                rowMenu.Items["hostRemove"]!.PerformClick();
                Assert((bool)Call(form, "FlushConfigChanges")! && !Field<List<HostState>>(form, "hosts").Contains(remote[0]) &&
                    Field<List<HostState>>(form, "hosts").Contains(local) && (AppConfigStore.Load(paths.ConfigFile).Settings.Hosts ?? []).SequenceEqual(remote.Skip(1).Select(host => host.Host)),
                    "removing a remote client preserves other rows and immediately persists the host list");

                void OpenContext(int row, string column)
                {
                    Rectangle bounds = table.GetCellDisplayRectangle(table.Columns[column].Index, row, true);
                    ShowContext(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
                }
                void ShowContext(Point point)
                {
                    Point screen = table.PointToScreen(point);
                    var message = Message.Create(table.Handle, 0x007B, table.Handle, new IntPtr((screen.X & 0xffff) | ((screen.Y & 0xffff) << 16)));
                    typeof(HostTable).GetMethod("WndProc", Private)!.Invoke(table, [message]); Application.DoEvents();
                }
            }
            finally { canvas.Controls.Remove(table); parent.Controls.Add(table); SynchronizationContext.SetSynchronizationContext(oldContext); }

            void WaitIdle()
            {
                Complete(Field<Task>(form, "hostActionTask"));
            }
        }
        finally
        {
            UiTheme.SetMode("System");
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Host fixtures outside artifacts");
            Directory.Delete(root, true);
        }
    }

    private sealed class HostActionRunner : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal bool Fail, WaitForCancellation, Cancelled;
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Add(request);
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new() { Success = !Fail, Changed = !Fail, Message = "Synthetic output", Error = Fail ? "Exitcode 5" : "" };
        }
        public void Dispose() { }
    }
}
