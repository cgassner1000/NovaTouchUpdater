using NovaTouchUpdater;

internal static partial class Program
{
    private static async Task SoftwareCompilationTests()
    {
        var worker = new CompilationRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        _ = form.Handle;
        Populate(form);
        Field<ComboBox>(form, "executionMode").SelectedIndex = 1;
        SetField(form, "readIrisInventory", new Func<CancellationToken, Task<IrisInventoryResult>>(_ => Task.FromResult(
            new IrisInventoryResult([new("LOCAL", "C:\\IRIS", "", "", ["USER", "%SYS", "PROG-DEV"], SuperServerPort: 1972)], []))));
        SetField(form, "showBetaActivated", new Action(() => { }));
        string? warning = null;
        SetField(form, "showSoftwareCompilationWarning", new Action<string>(text => warning = text));
        worker.AfterProgress = request =>
        {
            Application.DoEvents();
            HostState host = ((HostState[])Call(form, "SelectedHosts")!).Single(host => host.Host == request.Host);
            Assert(host.Result == "Läuft", "each new compilation replaces a previous failure with running status before progress arrives");
            Assert(host.ProgressPercent == 40 && host.Phase == "IRIS:1972/USER · Klasse 101 von 250 · Novacom-NT-BOH.xml" &&
                host.Row?.Cells["information"].Tag is 40 && host.Row.Cells["information"].Value?.ToString() == "40 % · IRIS:1972/USER · Klasse 101 von 250 · Novacom-NT-BOH.xml",
                "the information cell shows percentage, database destination, class count and XML filename last; timing details stay in the log");
        };
        IReadOnlyList<SoftwareCompilationHostOutcome>? overview = null;
        SetField(form, "showSoftwareCompilationResults", new Action<IReadOnlyList<SoftwareCompilationHostOutcome>>(results =>
        {
            if (worker.ReportCompilerError)
                Assert(worker.Requests.Count(request => request.Action == "compile-software-packages") == 4,
                    "compiler errors are displayed only after every selected server has finished");
            overview = results;
        }));
        bool confirm = false, selectOne = false;
        SetField(form, "selectSoftwareCompilationTargets", new Func<ServicePackHostPlan[], ServicePackHostPlan[]?>(plans =>
        {
            Assert(plans.Length == 4 && plans.SelectMany(plan => plan.Targets).All(target =>
                target.Namespaces.All(ns => !ns.StartsWith('%')) && target.Namespaces.Contains("PROG-DEV")),
                "namespace selection includes every selected server and PROG namespace while excluding system namespaces");
            if (!confirm) return null;
            if (!selectOne) return plans;
            IrisUploadTarget target = plans[0].Targets[0];
            return [new(plans[0].Host, [new() { Instance = target.Instance, Port = target.Port, Directory = target.Directory,
                Namespaces = ["PROG-DEV"], User = target.User, Password = target.Password, Ssl = target.Ssl }])];
        }));
        await (Task)Call(form, "CompileSoftwarePackagesAsync")!;
        Assert(!Field<BetaFeatureGate>(form, "betaFeatures").Enabled && worker.Requests.Any(request => request.Action == "database-inventory") &&
            worker.Requests.All(request => request.Action == "database-inventory"),
            "compilation opens namespace selection without activation; declining does not compile or load DLLs");
        worker.Requests.Clear(); confirm = true;
        await (Task)Call(form, "CompileSoftwarePackagesAsync")!;
        Assert(warning is null && worker.Requests.Count(request => request.Action == "compile-software-packages") == 4 &&
            worker.Requests.Where(request => request.Action == "compile-software-packages").All(request =>
                !request.SoftwareCompilationChild && request.ServicePackFiles.Length == 0 && request.MsiPackages.Length == 0 &&
                request.DatabaseTargets.All(target => target.Namespaces.All(ns => !ns.StartsWith('%')))),
            "confirmed compilation uses local and remote hosts with their IRIS targets, without MSI or servicepack-file operations");
        Assert(overview?.Count == 4 && overview[0].Host == Environment.MachineName + " (lokal)",
            "the final overview includes every server and identifies the actual local computer");
        Assert(((HostState[])Call(form, "SelectedHosts")!).All(host => host.ProgressPercent == 100 && host.Phase == "kompiliert"),
            "successful compilation finishes with full progress and a concise result");
        worker.Requests.Clear(); worker.ReportCompilerError = true;
        await (Task)Call(form, "CompileSoftwarePackagesAsync")!;
        Assert(warning is null && overview?.Sum(host => host.Result.SoftwareCompilationResults.Sum(ns => ns.CompilationErrors.Length)) == 4 &&
            worker.Requests.Count(request => request.Action == "compile-software-packages") == 4,
            "compiler errors continue subsequent servers without intermediate warning dialogs and retain all failures for the final overview");
        SoftwareCompilationDialogTests(overview!);
        Assert(((HostState[])Call(form, "SelectedHosts")!).All(host => host.ProgressPercent is null),
            "compiler errors clear completed progress before showing the final result");
        worker.ReportCompilerError = false;
        worker.Requests.Clear(); selectOne = true;
        await (Task)Call(form, "CompileSoftwarePackagesAsync")!;
        Assert(worker.Requests.Count(request => request.Action == "compile-software-packages") == 1 &&
            worker.Requests.Single(request => request.Action == "compile-software-packages").DatabaseTargets.Single().Namespaces.SequenceEqual(["PROG-DEV"]),
            "only the selected namespace is sent to the compiler and unselected servers are omitted");
        selectOne = false;
        DatabaseTargetSelectionTests(servicePacks: false);
        worker.Requests.Clear(); worker.OnCompile = () => Field<CancellationTokenSource>(form, "cancellation").Cancel();
        await (Task)Call(form, "CompileSoftwarePackagesAsync")!;
        Assert(worker.Requests.Count(request => request.Action == "compile-software-packages") == 1,
            "cancellation is available to the active host and prevents subsequent hosts from starting");
        using var nativeWorker = new WorkerRunner();
        WorkerResult empty = await nativeWorker.RunAsync(new WorkerRequest { Action = "compile-software-packages", LocalExecution = true, Host = "LOCALHOST", DatabaseTargets = [] }, _ => { }, CancellationToken.None);
        Assert(empty.Success && empty.Skipped && !empty.Changed,
            "the actual embedded worker starts its x86 child and handles an empty plan before loading installed DLLs or opening a database: " + empty.Error);
        using var protocolWorker = new WorkerRunner();
        string protocolScript = (string)typeof(WorkerRunner).GetField("scriptFile", Private)!.GetValue(protocolWorker)!;
        File.WriteAllText(protocolScript, """
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            $request=[Console]::In.ReadLine() | ConvertFrom-Json
            if($null -ne [Console]::In.ReadLine()){throw 'Unexpected confirmation input'}
            [Console]::Out.WriteLine('{"Event":"progress","Phase":"Softwarepaket-Kompilierungsfehler","Message":"Fixture.First"}')
            [Console]::Out.WriteLine('{"Event":"progress","Phase":"Softwarepaket-Kompilierungsfehler","Message":"Fixture.Second"}')
            [Console]::Out.WriteLine('{"Event":"result","Success":false,"SoftwareCompilationResults":[{"Instance":"IRIS:1972","Namespace":"TAC","CompilationErrors":[{"Item":"Fixture.First","ItemType":"Klasse","Details":"#5272"},{"Item":"Fixture.Second","ItemType":"Routine","Details":"#5475"}]}]}')
            """);
        int loggedErrors = 0;
        using var protocolTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        WorkerResult protocol = await protocolWorker.RunAsync(new WorkerRequest { Action = "compile-software-packages" }, ev =>
        {
            if (ev.Phase == "Softwarepaket-Kompilierungsfehler") loggedErrors++;
        }, protocolTimeout.Token);
        Assert(!protocol.Success && loggedErrors == 2 && protocol.SoftwareCompilationResults.Single().CompilationErrors.Length == 2,
            "the real process finishes with closed stdin and retains every compiler error without an OK round trip");
    }

    private static void DatabaseTargetSelectionTests(bool servicePacks)
    {
        ServicePackHostPlan[] plans = [
            new(new HostState { Host = "SERVER-A" }, [
                new() { Instance = "IRIS01", Port = 1972, Directory = "C:\\IRIS01", User = "fixture-user", Password = "fixture-secret", Ssl = true, Namespaces = ["%SYS", "USER", "TAC", "PROG-DEV"] },
                new() { Instance = "IRIS02", Port = 56772, Directory = "C:\\IRIS02", Namespaces = ["USER"] }]),
            new(new HostState { Host = "SERVER-B" }, [new() { Instance = "IRIS01", Port = 1972, Namespaces = ["USER"] }]),
            new(new HostState { Host = "SERVER-EMPTY" }, [])];
        using var dialog = servicePacks ? DatabaseTargetsDialog.ForServicePacks(plans, 2) : DatabaseTargetsDialog.ForSoftwareCompilation(plans);
        dialog.Show(); Application.DoEvents();
        var tree = All(dialog).OfType<TreeView>().Single();
        var apply = All(dialog).OfType<Button>().Single(button => button.Name == "applySelectedNamespaces");
        Assert(dialog.Text.StartsWith(servicePacks ? "Servicepacks einspielen" : "Softwarepakete kompilieren") &&
            apply.Text == (servicePacks ? "Einspielen" : "Kompilieren") && apply.DialogResult == DialogResult.OK &&
            All(dialog).OfType<Label>().Any(label => label.Text == (servicePacks
                ? "2 XML-Servicepack(s) in die ausgewählten Namespaces einspielen?"
                : "Softwarepakete in den ausgewählten Namespaces kompilieren?")),
            "the namespace picker confirms the selected operation with one short question");
        Assert(tree.CheckBoxes && tree.Nodes.Count == 3 && tree.Nodes[0].Nodes.Count == 2 &&
            tree.Nodes[0].Nodes[0].Nodes.Count == 3 && tree.Nodes[0].Nodes[0].Nodes.ContainsKey("PROG-DEV"),
            "the picker groups servers, instances and checked namespaces like Check Database, excluding percent namespaces");
        tree.Nodes[2].Checked = true;
        Assert(!tree.Nodes[2].Checked && tree.Nodes[2].Text.Contains("keine berücksichtigten Namespaces"),
            "servers without applicable namespaces are shown and cannot be selected for processing");
        foreach (TreeNode root in tree.Nodes) root.Checked = false;
        Assert(dialog.GetSelectedPlans().Length == 0 && !apply.Enabled, "an empty namespace selection disables processing");
        var leaf = tree.Nodes[0].Nodes[1].Nodes["USER"]!;
        void ClickNode(TreeNode node, int x, MouseButtons button = MouseButtons.Left)
        {
            int y = node.Bounds.Top + node.Bounds.Height / 2;
            typeof(TreeView).GetMethod("OnNodeMouseClick", Private)!.Invoke(tree,
                [new TreeNodeMouseClickEventArgs(node, button, 1, x, y)]);
        }
        ClickNode(leaf, leaf.Bounds.Left + 4);
        Assert(leaf.Checked && apply.Enabled && dialog.GetSelectedPlans() is [var clickedPlan] &&
            clickedPlan.Targets is [var clickedTarget] && clickedTarget.Instance == "IRIS02" && clickedTarget.Namespaces.SequenceEqual(["USER"]),
            "clicking a namespace name selects only that namespace and enables processing");
        ClickNode(leaf, leaf.Bounds.Left + 4);
        Assert(!leaf.Checked && dialog.GetSelectedPlans().Length == 0 && !apply.Enabled,
            "clicking the namespace name again clears its checkbox and disables an empty selection");
        ClickNode(leaf, leaf.Bounds.Left + 4, MouseButtons.Right);
        ClickNode(tree.Nodes[0], tree.Nodes[0].Bounds.Left + 4);
        Assert(!leaf.Checked && !tree.Nodes[0].Checked, "right-clicking a namespace and selecting a server name leave checkboxes unchanged");
        int checkboxX = Enumerable.Range(0, leaf.Bounds.Left).First(x =>
            (tree.HitTest(x, leaf.Bounds.Top + leaf.Bounds.Height / 2).Location & TreeViewHitTestLocations.StateImage) != 0);
        leaf.Checked = true; // The native checkbox toggles before NodeMouseClick.
        ClickNode(leaf, checkboxX);
        Assert(leaf.Checked, "the namespace-name handler does not toggle a checkbox click a second time");
        leaf.Checked = false;
        tree.Nodes[0].Nodes[1].Nodes["USER"]!.Checked = true;
        ServicePackHostPlan[] selected = dialog.GetSelectedPlans();
        Assert(apply.Enabled && selected.Length == 1 && selected[0].Host.Host == "SERVER-A" &&
            selected[0].Targets.Single().Instance == "IRIS02" && selected[0].Targets[0].Port == 56772 &&
            selected[0].Targets[0].Namespaces.SequenceEqual(["USER"]),
            "selecting USER on one instance cannot select the same namespace on another instance or server");
        tree.Nodes[0].Checked = false;
        tree.Nodes[0].Nodes[0].Nodes["PROG-DEV"]!.Checked = true;
        IrisUploadTarget original = plans[0].Targets[0], filtered = dialog.GetSelectedPlans()[0].Targets.Single();
        Assert(filtered.Ssl && filtered.User == original.User && filtered.Password == original.Password && filtered.Directory == original.Directory &&
            !ReferenceEquals(filtered, original) && original.Namespaces.Length == 4,
            "namespace filtering preserves credentials, TLS and installation paths without mutating inventory targets");
        tree.Nodes[1].Checked = true;
        Assert(dialog.GetSelectedPlans().Length == 2, "server checkboxes select their namespaces without changing other servers");
        tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes["PROG-DEV"];
        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, servicePacks ? "servicepack-targets-dialog.png" : "compilation-targets-dialog.png"));
        dialog.Hide();
    }

    private static void SoftwareCompilationDialogTests(IReadOnlyList<SoftwareCompilationHostOutcome> outcomes)
    {
        using var summary = new SoftwareCompilationResultsDialog(outcomes);
        var table = All(summary).OfType<DataGridView>().Single(grid => grid.Name == "compilationErrors");
        Assert(table.Rows.Count == 4 && table.Columns[0].HeaderText == "Server" &&
            table.Rows[0].Cells[4].Value?.ToString() == "cmNT.IFC.Common.TaskDefinition" &&
            All(summary).OfType<TabControl>().Single().SelectedTab?.Text == "Kompilierungsfehler (4)",
            "the final error overview identifies server, instance, namespace and class");
        var details = All(summary).OfType<TextBox>().Single(box => box.Name == "compilationErrorDetails");
        summary.Show(); Application.DoEvents();
        for (int index = 0; index < outcomes.Count; index++)
        {
            var host = outcomes[index];
            var failure = host.Result.SoftwareCompilationResults[0].CompilationErrors[0];
            table.CurrentCell = table.Rows[index].Cells[0];
            Assert(table.Rows[index].Cells[0].Value?.ToString() == host.Host && table.Rows[index].Cells[1].Value?.ToString() == failure.Instance &&
                table.Rows[index].Cells[2].Value?.ToString() == failure.Namespace && details.ReadOnly &&
                details.Text.Contains("Server: " + host.Host) && details.Text.Contains("Instanz: " + failure.Instance) &&
                details.Text.Contains("Namespace: " + failure.Namespace) && details.Text.Contains(failure.Item) && details.Text.Contains("#5272"),
                "selecting an error keeps the full server/instance/namespace/class context in the copyable details");
        }
        using var bitmap = new Bitmap(summary.Width, summary.Height);
        summary.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "compilation-results-dialog.png"));
        summary.Hide();
    }

    private sealed class CompilationRunner : IWorkerRunner
    {
        internal readonly List<WorkerRequest> Requests = [];
        internal Action? OnCompile;
        internal Action<WorkerRequest>? AfterProgress;
        internal bool ReportCompilerError;
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Action == "database-inventory")
                return Task.FromResult(new WorkerResult { Success = true, IrisInstances = [new() { Name = "REMOTE", Directory = "C:\\IRIS", SuperServerPort = 1972, Namespaces = ["%SYS", "USER", "PROG-DEV"] }] });
            Assert(request.Action == "compile-software-packages" && ct.CanBeCanceled, "an active host compilation receives the operation cancellation token");
            OnCompile?.Invoke();
            onProgress(new WorkerEvent { Event = "progress", Phase = "Softwarepaket kompilieren",
                Message = "IRIS:1972/USER · Klasse 101 von 250 · Novacom-NT-BOH.xml", ProgressPercent = 40 });
            AfterProgress?.Invoke(request);
            onProgress(new WorkerEvent { Event = "progress", Phase = "Softwarepaket-Zeiten", Message = "Import 1,20s · Kompilieren 2,30s" });
            AfterProgress?.Invoke(request);
            if (ReportCompilerError)
            {
                var failure = new SoftwareCompilationError { Instance = request.LocalExecution ? "LOCAL:1972" : "REMOTE:56772", Namespace = request.LocalExecution ? "TAC" : "USER", Resource = "Novacom-NT-BOH.xml",
                    Item = "cmNT.IFC.Common.TaskDefinition", ItemType = "Klasse", Details = "ERROR #5272: Cannot change final 'Method': 'GetScheduledRunTime'" };
                onProgress(new WorkerEvent { Event = "progress", Phase = "Softwarepaket-Kompilierungsfehler", Message = failure.Description });
                return Task.FromResult(new WorkerResult { Success = false, Message = "Mit Kompilierungsfehlern", SoftwareCompilationResults =
                    [new() { Instance = failure.Instance, Namespace = failure.Namespace, ImportedResources = 11, CompilationErrors = [failure], Error = failure.Details }] });
            }
            return Task.FromResult(new WorkerResult { Success = true, Message = "kompiliert" });
        }
        public void Dispose() { }
    }
}
