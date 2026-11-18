using System.Diagnostics;
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
            foreach ((string host, string server) in new[] { ("10.1.80.11", "10.1.80.11"), ("PC_A.invalid", "PC_A.invalid"),
                ("2001:db8::1", "2001-db8--1.ipv6-literal.net"), ("fe80::1%12", "fe80--1s12.ipv6-literal.net") })
                Assert(HostExplorer.CreateStartInfo(new() { Host = host }).ArgumentList.Single() == @"\\" + server + @"\C$",
                    "remote Explorer supports hostnames, IPv4 and UNC-encoded IPv6: " + host);
            foreach (string host in new[] { "", @"server\share", @"\\server\C$", "server /root,C:\\", "server\" /select,C:\\" })
            {
                bool rejected = false;
                try { HostExplorer.CreateStartInfo(new() { Host = host }); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "imported host values cannot inject paths or Explorer switches");
            }
            var updates = new UpdateRunner();
            var commands = new HostActionRunner();
            var consoles = new List<HostConsoleOptions>();
            var explorers = new List<ProcessStartInfo>();
            using var form = new MainForm(preview: true, updates, new FakeRunner(), dataPaths: paths);
            _ = form.Handle;
            HostState[] remote = Populate(form);
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
            SetField(form, "createCommandRunner", new Func<IWorkerRunner>(() => commands));
            SetField(form, "openHostConsole", new Func<HostConsoleOptions, CancellationToken, Task<bool>>((options, ct) => { ct.ThrowIfCancellationRequested(); consoles.Add(options); return Task.FromResult(true); }));
            SetField(form, "launchHostExplorer", new Action<ProcessStartInfo>(explorers.Add));
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
                host.Connection = host.IsLocal ? "Lokal" : "Verbunden";
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
                Assert(rowMenu.Items.OfType<ToolStripMenuItem>().Where(item => item.Available).Select(item => item.Text).SequenceEqual(new[]
                    { "Update", "Explorer öffnen", "Eingabeaufforderung", "Collect Logs", "NovaTouch POS neustarten", "Nt.Payment neustarten", "Nt.Fiskal neustarten", "NT3.Server.Suite neustarten", "Client entfernen" }) &&
                    rowMenu.Items.OfType<ToolStripSeparator>().Count() == 3, "host actions have the requested order and separators");
                int collectIndex = rowMenu.Items.IndexOfKey("hostCollectLogs");
                Assert(rowMenu.Items[collectIndex - 1] is ToolStripSeparator && rowMenu.Items[collectIndex + 1] is ToolStripSeparator,
                    "Collect Logs sits in its own section directly after the command prompt");
                Rectangle last = table.GetRowDisplayRectangle(table.Rows.Count - 1, false);
                ShowContext(new Point(20, last.Bottom + 30));
                var backgroundMenu = Field<ContextMenuStrip>(form, "hostBackgroundMenu");
                Assert(backgroundMenu.Visible && !rowMenu.Visible && !columnMenu.Visible && backgroundMenu.Items[0].Text == "Remote Client hinzufügen",
                    "empty table space offers adding a remote client instead of columns or stale host actions");
                backgroundMenu.Close();

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
                    updates.Requests.Count == 0 && commands.Requests.Count == 0 && consoles.Count == 0,
                    "an unreachable remote disables update and command without starting workers or preventing removal");
                rowMenu.Items["hostExplorer"]!.PerformClick(); WaitIdle();
                Assert(rowMenu.Items["hostExplorer"]!.Enabled && explorers.Single().ArgumentList.Single() == @"\\PC-A.invalid\C$",
                    "SMB Explorer access remains available independently of a failed WinRM connection");
                explorers.Clear();
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
                    commands.Requests.Clear(); consoles.Clear();
                    Field<TextBox>(form, "txtUser").Text = "SYNTHETIC\\admin";
                    Field<TextBox>(form, "txtPassword").Text = "Synthetic pipe-only password";
                    Field<CheckBox>(form, "chkSsl").Checked = true;
                    Field<NumericUpDown>(form, "nHttpsPort").Value = 443;
                    string before = host.Result;
                    rowMenu.Items["hostExplorer"]!.PerformClick(); WaitIdle();
                    ProcessStartInfo explorer = explorers.Single(); explorers.Clear();
                    Assert(explorer.FileName == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") &&
                        explorer.UseShellExecute && explorer.Arguments.Length == 0 && explorer.ArgumentList.Single() ==
                            (host.IsLocal ? Path.GetPathRoot(Environment.SystemDirectory) : @"\\" + host.Host + @"\C$") &&
                        consoles.Count == 0 && commands.Requests.Count == 0 && host.Result == before,
                        "Explorer opens only the clicked local drive or remote administrative share without WinRM credentials, workers or result changes; local=" + host.IsLocal);
                    rowMenu.Items["hostCommand"]!.PerformClick(); WaitIdle();
                    HostConsoleOptions console = consoles.Single();
                    Assert(console.Host == host.Host && console.LocalExecution == host.IsLocal && commands.Requests.Count == 0 && host.Result == before &&
                        (host.IsLocal ? console.Username.Length == 0 && console.Password.Length == 0 : console.UseSsl && console.HttpsPort == 443 && console.Username == "SYNTHETIC\\admin" && console.Password == "Synthetic pipe-only password"),
                        "the row opens one independent console with the correct target and credentials; local=" + host.IsLocal);
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
                Assert(selected.SequenceEqual(Field<List<HostState>>(form, "hosts").Select(host => host.IsSelected)), "Explorer, commands and updates preserve the complete checkbox selection");

                foreach (string mode in new[] { "Light", "Dark" })
                {
                    UiTheme.SetMode(mode);
                    Call(form, "PrepareHostActionsMenu", remote[0]);
                    rowMenu.Show(table, new Point(80, 60)); Application.DoEvents();
                    using var bitmap = new Bitmap(rowMenu.Width, rowMenu.Height);
                    rowMenu.DrawToBitmap(bitmap, rowMenu.ClientRectangle);
                    bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "host-actions-" + mode.ToLowerInvariant() + ".png"));
                    rowMenu.Close();
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
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new WorkerResult { Success = true, Changed = true, Message = "Synthetic output" });
        }
        public void Dispose() { }
    }
}
