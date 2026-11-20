using NovaTouchUpdater;

internal static partial class Program
{
    private static void RemoteClientTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "remote-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        Directory.CreateDirectory(paths.RootDirectory);
        var previousContext = SynchronizationContext.Current;
        using var context = new WindowsFormsSynchronizationContext();
        try
        {
            AppConfigStore.Save(paths.ConfigFile, new UpdaterConfig { Hosts = ["PC-A.invalid", "PC-B.invalid"], ExecutionMode = "Local", ScanOnStartup = false }, "");
            var worker = new FakeRunner(); var monitor = new RemoteClientProbe();
            using var form = new MainForm(preview: true, worker, new FakeRunner(), dataPaths: paths);
            _ = form.Handle; Call(form, "LoadStartupConfig");
            SetField(form, "createHostConnectionRunner", new Func<IWorkerRunner>(() => monitor));
            var hosts = Field<List<HostState>>(form, "hosts");
            HostState first = hosts.Single(host => host.Host == "PC-A.invalid"), second = hosts.Single(host => host.Host == "PC-B.invalid");
            first.IsSelected = false; first.InstalledComponents = UpdateComponents.None;
            first.Updates = new(UpdateComponents.Payment); first.Updates.Fail(UpdateComponents.Payment, "Keep previous update failure");
            first.Result = "Fehler"; first.Error = "Keep previous update failure";
            var background = (ContextMenuStrip)Call(form, "PrepareHostBackgroundMenu")!;
            Assert(background.Items.Count == 1 && background.Items[0].Text == "Remote Client hinzufügen", "blank table context offers the single add-client action");
            SetField(form, "chooseRemoteClient", new Func<string?>(() => " 10.20.30.40 "));
            SynchronizationContext.SetSynchronizationContext(context);
            background.Items[0].PerformClick(); Complete(Field<Task>(form, "hostActionTask"));
            HostState added = hosts.Single(host => host.Host == "10.20.30.40");
            var saved = AppConfigStore.Load(paths.ConfigFile).Settings;
            Assert(saved.Hosts!.SequenceEqual(["PC-A.invalid", "PC-B.invalid", "10.20.30.40"]) && saved.ExecutionMode == "LocalAndRemote" && added.Row is not null,
                "adding from local mode persists the new remote and makes it visible alongside the local host");
            Assert(monitor.Requests is [{ Action: "monitor-connections", Parallelism: 1 }] && monitor.Requests[0].Hosts.SequenceEqual([added.Host]) && worker.Requests.IsEmpty &&
                added.Connection == "Nicht erreichbar" && !added.Scanned && first.Error == "Keep previous update failure",
                "adding performs exactly one connection-only check and leaves existing host results and installations alone");
            var menu = (ContextMenuStrip)Call(form, "PrepareHostActionsMenu", added)!;
            Assert(menu.Items["hostConnect"]!.Available && menu.Items["hostWinRm"]!.Available && !menu.Items["hostUpdate"]!.Enabled && !menu.Items["hostCommand"]!.Enabled && !menu.Items["hostExplorer"]!.Enabled,
                "an unreachable remote exposes Connect and WinRM configuration while update and console remain disabled");

            Field<TextBox>(form, "txtUser").Text = "SYNTHETIC\\admin";
            Field<TextBox>(form, "txtPassword").Text = "Synthetic credential";
            Field<CheckBox>(form, "chkSsl").Checked = true;
            Field<NumericUpDown>(form, "nHttpsPort").Value = 443;
            monitor.Requests.Clear(); monitor.Connected = true;
            SynchronizationContext.SetSynchronizationContext(context);
            menu.Items["hostConnect"]!.PerformClick(); Complete(Field<Task>(form, "hostActionTask"));
            Assert(monitor.Requests.Count == 1 && monitor.Requests[0] is { UseSsl: true, HttpsPort: 443, Username: "SYNTHETIC\\admin", Password: "Synthetic credential" } &&
                monitor.Requests[0].Hosts.SequenceEqual([added.Host]) && added.Connection == "Verbunden" && added.ConnectionState.UsesHttps == true && !added.Scanned,
                "Connect retries only its own host with the configured transport and credentials, without reading versions");
            Call(form, "PrepareHostActionsMenu", added);
            Assert(!menu.Items["hostConnect"]!.Available && !menu.Items["hostWinRm"]!.Available && menu.Items["hostUpdate"]!.Enabled && menu.Items["hostExplorer"]!.Enabled,
                "a successful connection restores host actions and removes the failure repair section");

            monitor.Requests.Clear();
            SynchronizationContext.SetSynchronizationContext(context);
            Complete((Task)Call(form, "AddRemoteClientAsync", "pc-a.INVALID")!);
            Assert(hosts.Count(host => !host.IsLocal) == 3 && !first.IsSelected && second.IsSelected && first.Error == "Keep previous update failure" &&
                first.Updates.ProgressFor(UpdateComponents.Payment)!.Value.Status == ComponentUpdateStatus.Failed &&
                monitor.Requests.Single().Hosts.SequenceEqual([first.Host]) && first.Connection == "Verbunden",
                "adding an existing hostname deduplicates case-insensitively and reconnects it without changing selection or update failures");
            first.Connection = "Nicht erreichbar"; monitor.WaitForCancel = true;
            SynchronizationContext.SetSynchronizationContext(context);
            Task pending = (Task)Call(form, "CheckRemoteHostConnectionAsync", first)!;
            Assert(Field<bool>(form, "busy") && first.Connection == "Prüfung läuft", "a connection retry exposes its active state and can be cancelled");
            Call(form, "CancelCurrentOperation"); Complete(pending);
            Assert(monitor.Cancelled && !Field<bool>(form, "busy") && first.Connection == "Nicht erreichbar" && first.Error == "Keep previous update failure",
                "cancelling a single connection check restores its previous state and preserves update errors");
            monitor.WaitForCancel = false;

            Field<CheckBox>(form, "chkSsl").Checked = false;
            added.Connection = "Nicht erreichbar";
            foreach (bool prepFails in new[] { false, true })
            {
                string copied = ""; var diagnostic = new SingleClientDiagnostic { PreparationFails = prepFails };
                SetField(form, "showWinRmDialog", new Action<WinRmTestForm>(dialog =>
                {
                    _ = dialog.Handle;
                    var requests = WinRmField<WorkerRequest[]>(dialog, "diagnostics");
                    Assert(requests.Length == 1 && requests[0].Host == added.Host && !requests[0].UseSsl &&
                        WinRmField<WorkerRequest>(dialog, "preparation").Hosts.SequenceEqual([added.Host]),
                        "row WinRM configuration limits diagnostics and local TrustedHosts preparation to the clicked remote");
                    typeof(WinRmTestForm).GetField("copyText", Private)!.SetValue(dialog, new Action<string>(text => copied = text));
                    SynchronizationContext.SetSynchronizationContext(context);
                    Complete(dialog.RunDiagnosticsAsync(diagnostic));
                    Complete(dialog.CopyClientSetupAsync(0));
                    Assert(WinRmField<DataGridView>(dialog, "grid").Rows[0].Cells["clientSetup"].Value is WinRmSetupState.Copied,
                        "the row repair code provides the existing copied-checkmark feedback");
                }));
                Call(form, "PrepareHostActionsMenu", added);
                menu.Items["hostWinRm"]!.PerformClick(); Complete(Field<Task>(form, "hostActionTask"));
                Assert(copied == WinRmClientSetup.CreateCommand(added.Host, false, 5986) &&
                    diagnostic.Requests.All(request => request.Action == "winrm-prepare" ? request.Hosts.SequenceEqual([added.Host]) : request.Host == added.Host),
                    "the unchanged HTTP copy/paste command is available for this host, even if local preparation fails=" + prepFails);
            }
            Assert(worker.Requests.IsEmpty, "adding, reconnecting and repairing never starts an installation or inventory worker");

            foreach (string mode in new[] { "Light", "Dark" })
            {
                UiTheme.SetMode(mode);
                using var dialog = new AddRemoteClientDialog();
                dialog.Show(); Application.DoEvents();
                var input = (TextBox)dialog.Controls.Find("remoteHost", true).Single();
                var add = (Button)dialog.Controls.Find("addRemoteClient", true).Single();
                input.Text = "http://invalid/path"; add.PerformClick();
                Assert(dialog.DialogResult != DialogResult.OK, "the small popup rejects URLs and keeps the entry editable; " + mode);
                input.Text = "10.20.30.40";
                using var image = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size));
                image.Save(Path.Combine(TestWorkspace.Artifacts, "add-remote-client-" + mode.ToLowerInvariant() + ".png"));
                add.PerformClick();
                Assert(dialog.HostName == "10.20.30.40", "the small popup accepts a single IP address; " + mode);
            }
            foreach (string invalid in new[] { "", "LOCALHOST", ".", "one two", "https://client", "host:5986", "host;command" })
                Assert(!AddRemoteClientDialog.TryNormalize(invalid, out _), "invalid remote entry is rejected: " + invalid);
            Assert(AddRemoteClientDialog.TryNormalize(" SRV-8990-811 ", out string normalized) && normalized == "SRV-8990-811" &&
                AddRemoteClientDialog.TryNormalize("srv.example.invalid", out _) && AddRemoteClientDialog.TryNormalize("::1", out _),
                "client input supports trimmed Windows hostnames, FQDNs, IPv4 and IPv6");
        }
        finally
        {
            UiTheme.SetMode("System"); SynchronizationContext.SetSynchronizationContext(previousContext);
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Remote fixture outside artifacts");
            Directory.Delete(root, recursive: true);
        }
    }

    private static T WinRmField<T>(WinRmTestForm dialog, string name) => (T)typeof(WinRmTestForm).GetField(name, Private)!.GetValue(dialog)!;

    private sealed class RemoteClientProbe : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal bool Connected, WaitForCancel, Cancelled;
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Add(request);
            if (WaitForCancel)
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Cancelled = true; throw; }
            progress(new() { Host = "unrelated.invalid", Connected = true, Message = "Ignore unrelated event" });
            progress(new() { Host = request.Hosts.Single(), Connected = Connected, Message = "Synthetic connection result" });
            return new() { Success = true };
        }
        public void Dispose() { }
    }

    private sealed class SingleClientDiagnostic : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal bool PreparationFails;
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(request.Action == "winrm-prepare" ? new WorkerResult { Success = !PreparationFails, Message = "Synthetic local preparation" }
                : new WorkerResult { Success = false, Tcp = "Fehler", Session = "Fehler", Command = "Übersprungen", Error = "Synthetic unreachable remote" });
        }
        public void Dispose() { }
    }
}
