using Microsoft.Win32;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void DatabaseCheckTests()
    {
        string root = Path.GetFullPath(Path.Combine(TestWorkspace.Artifacts, "iris-inventory-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "IRIS test fixtures stay within test artifacts");
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "First"), second = Path.Combine(root, "Second"), invalid = Path.Combine(root, "Invalid"), sysOnly = Path.Combine(root, "SystemOnly");
            foreach (string path in new[] { first, second, invalid, sysOnly }) Directory.CreateDirectory(path);
            string cpf = Path.Combine(first, "iris.cpf"), active = Path.Combine(second, "active configuration.cpf");
            File.WriteAllText(cpf, "[ConfigFile]\nProduct=IRIS\n[Startup]\nDefaultPort=51774\n[Namespaces]\n%SYS=IRISSYS\nUSER=USER\nAPP-A=APP\n%OTHER=OTHER\nuser=USER\n; comment\n# comment\n[Databases]\nNOT-A-NAMESPACE=DB\n");
            File.WriteAllText(active, "[namespaces]\n%sys=IRISSYS\nSECOND=SECOND\n");
            File.WriteAllText(Path.Combine(second, "iris.cpf"), "[Namespaces]\nSTALE=STALE\n");
            File.WriteAllText(Path.Combine(invalid, "iris.cpf"), "[Databases]\nUSER=USER\n");
            File.WriteAllText(Path.Combine(sysOnly, "iris.cpf"), "[Namespaces]\n%SYS=IRISSYS\n%OTHER=OTHER\n%custom=CUSTOM\n");
            string original = File.ReadAllText(cpf);
            var views = new List<RegistryView>();
            var inventory = new IrisDatabaseInventory((view, ct) =>
            {
                views.Add(view);
                if (view == RegistryView.Registry64) return [new("first", "", "", "")];
                return [new("FIRST", first, "", "2026.1"), new("SECOND", second, active, "2025.1"),
                    new("MISSING", Path.Combine(root, "Missing"), "", "2024.1"), new("INVALID", invalid, "", "2024.1"),
                    new("SYSTEM-ONLY", sysOnly, "", "2024.1"), new("REGISTRY-ERROR", "", "", "", "No access to this instance")];
            });
            Task<IrisInventoryResult> task = inventory.ReadAsync(CancellationToken.None);
            Complete(task);
            IrisInventoryResult result = task.Result;
            Assert(views.Contains(RegistryView.Registry32) && (!Environment.Is64BitOperatingSystem || views.Contains(RegistryView.Registry64)) && result.Instances.Length == 6,
                "local discovery reads both registry views, deduplicates instance names and retains instances with read errors");
            Assert(result.Instances.Single(instance => instance.Name == "FIRST").Namespaces.SequenceEqual(new[] { "APP-A", "USER" }),
                "namespace parsing excludes every percent-prefixed name, retains USER and removes case-insensitive duplicates");
            Assert(result.Instances.Single(instance => instance.Name == "SECOND").Namespaces.SequenceEqual(new[] { "SECOND" }),
                "an explicitly registered active CPF takes precedence over the default iris.cpf");
            Assert(result.Instances.Count(instance => instance.Error.Length > 0) == 3 && result.Instances.Single(instance => instance.Name == "SYSTEM-ONLY") is { Error: "", Namespaces.Length: 0 },
                "missing files, invalid CPF sections and registry errors stay isolated from readable or system-only instances");
            Assert(File.ReadAllText(cpf) == original, "Database Check reads configuration without changing it");
            Assert(result.Instances.Single(instance => instance.Name == "FIRST").SuperServerPort == 51774 &&
                result.Instances.Single(instance => instance.Name == "SECOND").ConnectionError.Length > 0,
                "the active CPF supplies the instance's SuperServer port and reports missing connection settings separately from namespaces");
            foreach (string invalidPort in new[] { "0", "65536", "broken", "1972\nDefaultPort=1973" })
                Assert(IrisDatabaseInventory.ReadSuperServerPort(new StringReader("[Startup]\nDefaultPort=" + invalidPort), CancellationToken.None) == 0,
                    "invalid or ambiguous SuperServer ports cannot select an upload target");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool cancelled = false;
            try { Complete(inventory.ReadAsync(cancellation.Token)); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && views.Count == (Environment.Is64BitOperatingSystem ? 2 : 1), "cancelled discovery performs no further registry or file reads");
            var partial = new IrisDatabaseInventory((view, ct) => view == RegistryView.Registry64
                ? throw new UnauthorizedAccessException("Registry view unavailable") : [new("FIRST", first, "", "2026.1")]);
            Task<IrisInventoryResult> partialTask = partial.ReadAsync(CancellationToken.None);
            Complete(partialTask);
            Assert(partialTask.Result.Instances.Length == 1 && partialTask.Result.Warnings.Length == (Environment.Is64BitOperatingSystem ? 1 : 0),
                "one unreadable registry view does not hide instances from the other view");
        }
        finally
        {
            Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "IRIS test cleanup remains within its fixture directory");
            Directory.Delete(root, true);
        }
        DatabaseCheckFormTests();
        DatabaseCheckButtonTests();
        DatabaseUploadSettingsTests();
    }

    private static void DatabaseUploadSettingsTests()
    {
        var inventory = new IrisInventoryResult([
            new("FIRST", @"C:\IRIS\First", @"C:\IRIS\First\iris.cpf", "2026.1", ["%SYS", "%OTHER", "%custom", "APP", "USER"], SuperServerPort: 1972),
            new("SECOND", @"D:\IRIS\Second", @"D:\IRIS\Second\iris.cpf", "2025.1", ["TENANT"], SuperServerPort: 51774),
            new("SYSTEM-ONLY", @"C:\IRIS\System", @"C:\IRIS\System\iris.cpf", "2026.1", ["%SYS", "%OTHER"])], []);
        using var check = new DatabaseCheckForm(_ => Task.FromResult(inventory));
        foreach (Control control in All(check)) _ = control.Handle;
        Complete(check.RefreshAsync());
        Assert(All(check).OfType<TextBox>().All(control => control.ReadOnly) && !All(check).OfType<CheckBox>().Any(),
            "Database Check shows neither credential inputs nor a TLS option");
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        _ = form.Handle;
        var tls = Field<CheckBox>(form, "chkIrisTls");
        Control settings = All(form).Single(control => control.Name == "settingsCard");
        Assert(tls.Text == "IRIS-Verbindung mit TLS" && All(settings).Contains(tls), "the IRIS TLS switch belongs to Einstellungen");
        SetField(form, "readIrisInventory", new Func<CancellationToken, Task<IrisInventoryResult>>(_ => Task.FromResult(inventory)));
        string path = Path.Combine(TestWorkspace.Artifacts, "iris-options-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            foreach (bool enabled in new[] { false, true })
            {
                tls.Checked = enabled;
                Field<CheckBox>(form, "chkSsl").Checked = !enabled;
                Task<bool> prepared = (Task<bool>)Call(form, "PrepareLocalDatabaseUploadAsync", CancellationToken.None)!;
                Complete(prepared);
                IrisUploadTarget[] targets = Field<IrisUploadTarget[]>(form, "activeDatabaseTargets");
                Assert(prepared.Result && targets.Length == 2 && targets[0].Port == 1972 && targets[1].Port == 51774 &&
                    targets[0].Namespaces.SequenceEqual(["APP", "USER"]) && targets.All(target => target.User == "_SYSTEM" && target.Password == "SYS" && target.Ssl == enabled),
                    "local preparation automatically applies the fixed login and independent IRIS TLS setting to every instance");
                Assert(targets.All(target => target.Namespaces.All(name => !name.StartsWith('%'))),
                    "upload planning excludes all percent-prefixed names and instances containing only those namespaces");
                AppConfigStore.Save(path, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
                using var restored = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
                Call(restored, "ApplyLoadedConfig", AppConfigStore.Load(path));
                Assert(Field<CheckBox>(restored, "chkIrisTls").Checked == enabled && Field<CheckBox>(restored, "chkSsl").Checked != enabled,
                    "IRIS TLS survives configuration save/load independently of WinRM HTTPS");
                Assert(!File.ReadAllText(path).Contains("_SYSTEM") && !File.ReadAllText(path).Contains("SYS"), "fixed IRIS credentials are not written to configuration");
            }
        }
        finally { File.Delete(path); }
    }

    private static void DatabaseCheckFormTests()
    {
        int calls = 0;
        using var form = new DatabaseCheckForm(_ =>
        {
            calls++;
            if (calls == 3) throw new IOException("Simulated inventory failure");
            return Task.FromResult(calls == 1 ? new IrisInventoryResult([
                new("IRIS-PROD", @"C:\IRIS\Production", @"C:\IRIS\Production\production.cpf", "2026.1", ["%SYS", "USER", "APP", "%CUSTOM"]),
                new("IRIS-TEST", @"D:\IRIS\Test", @"D:\IRIS\Test\iris.cpf", "2025.1", [], "CPF cannot be read")], ["One registry view could not be read"])
                : new IrisInventoryResult([new("IRIS-NEW", @"C:\IRIS\New", @"C:\IRIS\New\iris.cpf", "2026.1", ["NEW"])], []));
        });
        foreach (Control control in All(form)) _ = control.Handle;
        Complete(form.RefreshAsync());
        TreeView tree = All(form).OfType<TreeView>().Single();
        TextBox details = All(form).OfType<TextBox>().Single(control => control.Name == "databaseDetails");
        Assert(form.Text == "Database Check – LOKAL" && tree.Nodes.Count == 3 &&
            tree.Nodes[0].Nodes.Cast<TreeNode>().Select(node => node.Text).SequenceEqual(new[] { "APP", "USER" }),
            "the separate local form groups namespaces by instance and never lists percent-prefixed names");
        tree.SelectedNode = tree.Nodes[1];
        Assert(details.Text.Contains(@"D:\IRIS\Test\iris.cpf") && details.Text.Contains("CPF cannot be read"),
            "selecting an instance shows its version, paths and individual read error");
        for (int pass = 0; pass < 3; pass++) Layout(form);
        using (var screenshot = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
            screenshot.Save(Path.Combine(TestWorkspace.Artifacts, "database-check.png"));
        }
        Complete(form.RefreshAsync());
        Assert(tree.Nodes.Count == 1 && tree.Nodes[0].Text.StartsWith("IRIS-NEW") && tree.Nodes[0].Nodes[0].Text == "NEW",
            "refresh replaces the previous instance and namespace list");
        Complete(form.RefreshAsync());
        Assert(tree.Nodes.Count == 0 && details.Text.Contains("Simulated inventory failure"), "an inventory failure clears stale results and displays the error");

        int pendingCalls = 0;
        var blocked = new TaskCompletionSource<IrisInventoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new DatabaseCheckForm(ct => { pendingCalls++; ct.Register(() => blocked.TrySetCanceled(ct)); return blocked.Task; });
        _ = closing.Handle;
        Task loading = closing.RefreshAsync();
        Complete(closing.RefreshAsync());
        Assert(pendingCalls == 1, "refresh cannot start a second concurrent inventory");
        closing.Dispose();
        Complete(loading);
        Assert(closing.IsDisposed, "closing Database Check cancels its pending read without updating disposed controls");
    }

    private static void DatabaseCheckButtonTests()
    {
        var worker = new FakeRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        HostState[] hosts = Populate(form);
        var button = Field<Button>(form, "btnDatabaseCheck");
        var mode = Field<ComboBox>(form, "executionMode");
        Control content = form.Controls["mainScrollArea"]!;
        using var canvas = new Panel { Size = form.ClientSize };
        canvas.Controls.Add(content);
        canvas.CreateControl();
        try
        {
            Assert(button.Text == "BETA: Database Check" && !button.Visible && !button.Enabled, "BETA: Database Check is hidden and disabled for remote-only targets");
            mode.SelectedIndex = (int)UpdateExecutionMode.Local;
            Assert(button.Visible && button.Enabled, "selecting local execution enables Database Check without a preceding scan");
            foreach (Control control in All(canvas)) _ = control.Handle;
            for (int pass = 0; pass < 3; pass++) Layout(canvas);
            using (var screenshot = new Bitmap(canvas.Width, canvas.Height))
            {
                canvas.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
                screenshot.Save(Path.Combine(TestWorkspace.Artifacts, "main-database-check.png"));
            }
            foreach (int width in new[] { 1000, 640 })
            {
                canvas.Size = new Size(width, 980);
                for (int pass = 0; pass < 5; pass++) Layout(canvas);
                Assert(button.Right <= button.Parent!.ClientSize.Width && Math.Abs(button.Right - button.Parent.ClientSize.Width) <= 1,
                    $"the BETA button stays at the right edge of the action bar at {width}px");
            }
            mode.SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
            HostState local = Targets(form).Single(host => host.IsLocal);
            EditSelection(form, local, false);
            Assert(!button.Visible && !button.Enabled, "deselecting the local target hides Database Check even in combined mode");
            EditSelection(form, local, true);
            Assert(button.Visible && button.Enabled, "the local target can use Database Check alongside selected remote hosts");
            SetField(form, "busy", true);
            Call(form, "UpdateButtons");
            Assert(!button.Enabled, "Database Check cannot start during an update");
            SetField(form, "busy", false);
            mode.SelectedIndex = (int)UpdateExecutionMode.Remote;
            foreach (HostState host in hosts) host.IsSelected = false;
            Call(form, "UpdateExecutionSelection");
            Assert(button.Visible && button.Enabled && Targets(form).Single().IsLocal, "the local fallback without selected remote hosts also permits Database Check");
            Assert(worker.Requests.IsEmpty, "Database Check availability does not start workers or remote connections");
        }
        finally { canvas.Controls.Remove(content); form.Controls.Add(content); }
    }

    private static void DatabaseCheckLiveSmoke()
    {
        Task<IrisInventoryResult> task = new IrisDatabaseInventory().ReadAsync(CancellationToken.None);
        Complete(task);
        IrisInventoryResult result = task.Result;
        Assert(result.Instances.Length > 0 && result.Instances.All(instance => instance.Error.Length == 0), "read-only smoke discovers installed IRIS registrations and their CPF files");
        foreach (IrisInstanceInfo instance in result.Instances)
        {
            Assert(instance.Namespaces.All(name => !name.StartsWith('%')), "the real namespace list excludes every percent-prefixed name");
            Assert(instance.SuperServerPort is > 0 and <= 65535, "the active local CPF supplies a valid SuperServer port");
            Console.WriteLine(instance.Name + ": " + string.Join(", ", instance.Namespaces));
        }
        using var form = new DatabaseCheckForm(_ => Task.FromResult(result));
        foreach (Control control in All(form)) _ = control.Handle;
        Complete(form.RefreshAsync());
        for (int pass = 0; pass < 3; pass++) Layout(form);
        using var screenshot = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
        screenshot.Save(Path.Combine(TestWorkspace.Artifacts, "database-check-local.png"));
    }
}
