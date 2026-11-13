using System.Text;
using System.Xml;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void BetaFeatureTests()
    {
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        using var canvas = new Panel { Size = form.ClientSize };
        canvas.Controls.Add(form.Controls[0]); canvas.CreateControl();
        foreach (Control control in All(canvas)) _ = control.Handle;
        var gate = Field<BetaFeatureGate>(form, "betaFeatures");
        var image = All(canvas).OfType<AppMark>().Single();
        var button = Field<Button>(form, "btnServicePacks");
        var compile = Field<Button>(form, "btnCompileSoftware");
        var check = Field<Button>(form, "btnDatabaseCheck");
        var tls = Field<CheckBox>(form, "chkIrisTls");
        var autoCompile = Field<CheckBox>(form, "chkCompileAfterInstallation");
        var tlsParent = (TableLayoutPanel)tls.Parent!;
        var tlsCell = tlsParent.GetCellPosition(tls);
        var betaDivider = tlsParent.Controls.Cast<Control>().Single(control => control.Name == "erweitertSettingsDivider");
        var betaCell = tlsParent.GetCellPosition(betaDivider);
        var buildPreference = tlsParent.Controls.Cast<Control>().Single(control => control.Name == "buildPreference");
        var generalDivider = tlsParent.Controls.Cast<Control>().Single(control => control.Name == "allgemeinSettingsDivider");
        Assert(tlsParent.GetRow(betaDivider) < tlsParent.GetRow(buildPreference) &&
            tlsParent.GetRow(buildPreference) < tlsParent.GetRow(tls) && tlsParent.GetRow(tls) < tlsParent.GetRow(generalDivider),
            "the Erweitert settings group contains use Build and IRIS TLS before Allgemein");
        Assert(autoCompile.Parent == tlsParent && tlsParent.GetRow(autoCompile) > tlsParent.GetRow(generalDivider) &&
            tlsParent.GetRow(autoCompile) == tlsParent.GetRow(Field<CheckBox>(form, "chkUploadToZzzSoftware")) + 1,
            "automatic compilation belongs to Allgemein directly below database upload");
        var autoCompileCell = tlsParent.GetCellPosition(autoCompile);
        canvas.Controls.Add(autoCompile);
        canvas.Controls.Add(tls);
        canvas.Controls.Add(betaDivider);
        int popups = 0;
        SetField(form, "showBetaActivated", new Action(() => popups++));
        using var before = new Bitmap(image.Width, image.Height);
        image.DrawToBitmap(before, image.ClientRectangle);
        Cursor cursor = image.Cursor;
        Assert(!gate.Enabled && !betaDivider.Visible && button.Visible && !compile.Visible && button.Enabled && compile.Enabled && !tls.Visible && autoCompile.Visible &&
            autoCompile is AppToggleSwitch { Checked: false, Text: "nach Installation kompilieren" } && tls.Text == "IRIS-Verbindung mit TLS" && check.Text == "Check Database" && !check.Visible,
            "servicepacks and automatic compilation remain available while manual compilation and IRIS TLS require advanced activation");
        for (int click = 0; click < 4; click++) gate.RegisterClick(image, true);
        gate.RegisterClick(check, true);
        for (int click = 0; click < 4; click++) gate.RegisterClick(image, true);
        Assert(!gate.Enabled && popups == 0 && button.Visible && !compile.Visible, "another control resets the five logo clicks while advanced actions stay hidden");
        var fifth = Message.Create(image.Handle, 0x0203, IntPtr.Zero, IntPtr.Zero);
        Assert(!gate.PreFilterMessage(ref fifth) && gate.Enabled && popups == 1 && betaDivider.Visible && button.Visible && compile.Visible && tls.Visible && autoCompile.Visible && check.Visible,
            "the fifth logo click reveals IRIS TLS while regular actions remain available");
        tlsParent.Controls.Add(tls, tlsCell.Column, tlsCell.Row);
        tlsParent.Controls.Add(autoCompile, autoCompileCell.Column, autoCompileCell.Row);
        tlsParent.Controls.Add(betaDivider, betaCell.Column, betaCell.Row);
        for (int click = 0; click < 8; click++) gate.RegisterClick(image, true);
        Assert(popups == 1, "further logo clicks do not repeat the activation popup");
        using var future = new Button();
        canvas.Controls.Add(future); gate.Register(future);
        Assert(future.Visible, "future beta controls use the same registration and activation state");
        canvas.Controls.Remove(future);
        using var after = new Bitmap(image.Width, image.Height);
        image.DrawToBitmap(after, image.ClientRectangle);
        using var beforeBytes = new MemoryStream(); using var afterBytes = new MemoryStream();
        before.Save(beforeBytes, System.Drawing.Imaging.ImageFormat.Png); after.Save(afterBytes, System.Drawing.Imaging.ImageFormat.Png);
        Assert(beforeBytes.ToArray().SequenceEqual(afterBytes.ToArray()) && cursor == image.Cursor && image.Cursor == Cursors.Default,
            "activation leaves the PNG pixels and mouse cursor unchanged");
        for (int pass = 0; pass < 4; pass++) Layout(canvas);
        Call(form, "ApplyColumnWidths");
        var update = Field<Button>(form, "btnUpdate");
        Assert(compile.Parent == button.Parent && compile.Parent!.Controls.GetChildIndex(compile) == compile.Parent.Controls.GetChildIndex(button) + 1 &&
            compile.Height == update.Height && compile.Text == "Softwarepakete\nkompilieren", "software compilation appears directly after servicepacks with the same button height");
        Assert(button.Height == update.Height && button.Parent == update.Parent &&
            button.Parent!.Controls.GetChildIndex(button) == button.Parent.Controls.GetChildIndex(update) + 1 && button.Text == "Servicepacks\neinspielen",
            "the two-line servicepacks button follows Updates starten with the same height");
        using var preview = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(preview, canvas.ClientRectangle);
        preview.Save(Path.Combine(TestWorkspace.Artifacts, "beta-servicepacks.png"));
        foreach (float scale in new[] { 1F, 2F })
        {
            if (scale == 2F)
            {
                var fonts = All(canvas).ToDictionary(control => control, control => control.Font);
                canvas.Scale(new SizeF(2F, 2F));
                foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * 2F, font.Style);
            }
            foreach (int width in new[] { 1000, 640 })
            {
                canvas.Width = (int)(width * scale);
                for (int pass = 0; pass < 3; pass++) Layout(canvas);
                Assert(button.Height == update.Height && button.Right <= button.Parent!.ClientSize.Width &&
                    TextRenderer.MeasureText("Servicepacks", button.Font, Size.Empty, TextFormatFlags.NoPadding).Height * 2 <= button.Height,
                    $"the servicepacks button keeps both lines and the regular button height at {width}px / {scale:P0}");
            }
        }
        using var fresh = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        Assert(!Field<BetaFeatureGate>(fresh, "betaFeatures").Enabled, "beta activation is limited to one application session");
    }

    private static async Task ServicePackTests()
    {
        var worker = new ServicePackRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        _ = form.Handle;
        string root = Path.Combine(TestWorkspace.Artifacts, "servicepacks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string classes = Path.Combine(root, "classes.xml"), routines = Path.Combine(root, "routines.xml");
        File.WriteAllText(classes, "<?xml version=\"1.0\" encoding=\"utf-16\"?><Export><Class name=\"User.ServicePack\"><Description>Grüße 🎉</Description></Class></Export>", Encoding.Unicode);
        File.WriteAllText(routines, "<Export><Routine name=\"ServicePack.mac\" type=\"MAC\"><![CDATA[ServicePack ; Grüße]]></Routine></Export>");
        byte[] original = File.ReadAllBytes(classes);
        string snapshot;
        using (var selection = await ServicePackSelection.CreateAsync([classes, routines, classes], CancellationToken.None))
        {
            snapshot = selection.Files[0].Path;
            Assert(selection.Files.Length == 2 && selection.Files[0].Items.SequenceEqual(["User.ServicePack.cls"]) &&
                selection.Files[1].Items.SequenceEqual(["ServicePack.mac"]), "XML selection accepts classes and routines and removes duplicate file selections");
            using var document = XmlReader.Create(snapshot);
            var xml = new XmlDocument(); xml.Load(document);
            Assert(xml.InnerText.Contains("Grüße 🎉") && File.ReadAllBytes(snapshot).SequenceEqual(original) && File.ReadAllBytes(classes).SequenceEqual(original),
                "remote snapshots preserve every original XML byte, including encoding, whitespace and Unicode");
            Assert(selection.LocalFiles[0].Path == classes && selection.LocalFiles[1].Path == routines &&
                selection.LocalFiles[0].Sha256 == selection.Files[0].Sha256,
                "local servicepacks use the original selected paths with the same checksums as their remote snapshots");
            Assert(await WorkerRunner.Sha256Async(snapshot, CancellationToken.None) == selection.Files[0].Sha256,
                "servicepack snapshots are protected by SHA256");
        }
        Assert(!File.Exists(snapshot), "only the temporary servicepack snapshots are removed afterwards");
        string globals = Path.Combine(root, "globals.xml"), mixed = Path.Combine(root, "mixed.xml");
        File.WriteAllText(globals, "<Export><Global name=\"^ServicePack.Data\"><Node><Subscript>1</Subscript><Data>Grüße 🎉</Data></Node></Global>" +
            "<Global name=\"ServicePack.data\"><Node><Data>case-sensitive</Data></Node></Global></Export>");
        File.WriteAllText(mixed, "<Export><Class name=\"User.ServicePack\"/><Routine name=\"ServicePack\" type=\"MAC\"/>" +
            "<Global name=\"^ServicePack.mac\"><Node><Data>global and routine names stay distinct</Data></Node></Global></Export>");
        using (var selection = await ServicePackSelection.CreateAsync([globals, mixed], CancellationToken.None))
        {
            Assert(selection.Files[0].Items.SequenceEqual(["^ServicePack.Data", "^ServicePack.data"]) &&
                selection.Files[1].Items.SequenceEqual(["User.ServicePack.cls", "ServicePack.mac", "^ServicePack.mac"]),
                "global-only and mixed XMLs accept global names with or without a caret and preserve case-sensitive global identities");
            Assert(selection.LocalFiles.Select(file => file.Path).SequenceEqual([globals, mixed]) &&
                selection.Files.All(file => File.ReadAllBytes(file.Path).SequenceEqual(File.ReadAllBytes(Path.Combine(root, file.Name)))),
                "global data and XML bytes stay unchanged for local imports and remote uploads");
        }
        string standardGlobals = Path.Combine(root, "standard-globals.xml"), standardMixed = Path.Combine(root, "standard-mixed.xml");
        string globalNodes = "<Global><Node><Sub>^ServicePack.Data</Sub><Node><Sub>key with spaces &amp; punctuation!</Sub>" +
            "<Data>Grüße 🎉</Data><Node><Sub>^not another global</Sub><Data>nested</Data></Node></Node>" +
            "<Node><Sub>second branch</Sub><Data>more data</Data></Node></Node></Global>" +
            "<Global><Node><Sub>^ServicePack.data</Sub><Data>case-sensitive</Data></Node></Global>";
        File.WriteAllText(standardGlobals, "<Export generator=\"IRIS\" version=\"26\">" + globalNodes + "</Export>");
        File.WriteAllText(standardMixed, "<Export><Routine name=\"BeforeGlobal\" type=\"MAC\"/>" + globalNodes +
            "<Class name=\"AfterGlobal\"/></Export>");
        using (var selection = await ServicePackSelection.CreateAsync([standardGlobals, standardMixed], CancellationToken.None))
        {
            Assert(selection.Files[0].Items.SequenceEqual(["^ServicePack.Data", "^ServicePack.data"]) &&
                selection.Files[1].Items.SequenceEqual(["BeforeGlobal.mac", "^ServicePack.Data", "^ServicePack.data", "AfterGlobal.cls"]),
                "standard IRIS Global/Node/Sub exports need no name attribute and only root Subs identify globals, including multiple Global blocks");
            Assert(selection.Files.All(file => File.ReadAllBytes(file.Path).SequenceEqual(File.ReadAllBytes(Path.Combine(root, file.Name)))) &&
                selection.LocalFiles.Select(file => file.Path).SequenceEqual([standardGlobals, standardMixed]),
                "standard global exports preserve all XML bytes and following code items for local and remote imports");
        }
        foreach (string bad in new[] { "<Export><Project name=\"unsupported\" /></Export>", "<Export/>", "<Export><Class name=\"User.*\" /></Export>",
            "<Export><Global name=\"^\"/></Export>", "<Export><Global name=\"^^Invalid\"/></Export>",
            "<Export><Global name=\"Data\"/><Global name=\"^Data\"/></Export>",
            "<Export><Global/></Export>", "<Export><Global><Node><Node><Sub>nested only</Sub></Node></Node></Global></Export>",
            "<Export><Global><Node><Sub/></Node></Global></Export>",
            "<!DOCTYPE Export [<!ENTITY x SYSTEM 'file:///C:/missing'>]><Export><Class name=\"User.Bad\">&x;</Class></Export>", "<Export><Routine" })
        {
            string path = Path.Combine(root, "invalid.xml"); File.WriteAllText(path, bad);
            bool rejected = false;
            try { using var ignored = await ServicePackSelection.CreateAsync([path], CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidDataException or XmlException) { rejected = true; }
            Assert(rejected, "malformed, empty, unsupported, duplicate and external-entity XML is rejected before database access");
        }
        using var picker = MainForm.CreateServicePackFileDialog();
        Assert(picker.Multiselect && picker.Filter.Contains("*.xml"), "any number of XML servicepacks can be selected together");

        HostState[] selected = Populate(form);
        Field<ComboBox>(form, "executionMode").SelectedIndex = 1;
        Field<TextBox>(form, "txtRemotePath").Text = @"D:\Servicepacks Ziel";
        SetField(form, "readIrisInventory", new Func<CancellationToken, Task<IrisInventoryResult>>(_ => Task.FromResult(
            new IrisInventoryResult([new("LOCAL", "C:\\IRIS", "", "", ["USER", "%SYS", "APP", "PROG-DEV"], SuperServerPort: 1972)], []))));
        SetField(form, "showBetaActivated", new Action(() => { }));
        worker.OnImport = () => Assert(Targets(form).Single(host => host.Host == worker.Requests.Last().Host).Result == "Läuft",
            "a servicepack host displays running before its new result arrives");
        string? warning = null;
        SetField(form, "showServicePackWarning", new Action<string>(text => warning = text));
        bool confirm = false, emptySelection = false;
        int? selectedPlan = null;
        SetField(form, "selectServicePackTargets", new Func<ServicePackHostPlan[], int, ServicePackHostPlan[]?>((plans, files) =>
        {
            Assert(files == 2 && plans.Length == 4 && plans[0].Targets[0].Namespaces.Contains("PROG-DEV") &&
                plans.SelectMany(plan => plan.Targets).All(target => target.Namespaces.All(ns => !ns.StartsWith('%'))),
                "namespace selection is prepared for all selected hosts, including PROG namespaces and excluding percent namespaces");
            if (!confirm) return null;
            if (emptySelection) return [];
            if (selectedPlan is not { } index) return plans;
            IrisUploadTarget target = plans[index].Targets.Last();
            return [new(plans[index].Host, [new() { Instance = target.Instance, Directory = target.Directory, Port = target.Port,
                Namespaces = ["USER"], User = target.User, Password = target.Password, Ssl = target.Ssl }])];
        }));
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(!Field<BetaFeatureGate>(form, "betaFeatures").Enabled && worker.Requests.Any(request => request.Action == "database-inventory") &&
            worker.Requests.All(request => request.Action == "database-inventory") && !Field<bool>(form, "loadingPackages"),
            "servicepacks open namespace selection without activation; cancelling imports no code and restores the UI");
        worker.Requests.Clear(); confirm = true;
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(warning is null && worker.Requests.Count(request => request.Action == "import-servicepacks") == 4 &&
            Targets(form).Count(host => host.Log.Any(line => line.Contains("Servicepacks: importiert und kompiliert"))) == 4,
            "confirmed servicepacks finish on local and all selected remote hosts with results in the log and no result dialog");
        Assert(Targets(form).All(host => host.Result == "Abgeschlossen"), "servicepack success replaces all previous host results");
        worker.FailImports = true;
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(Targets(form).All(host => host.Result == "Fehler" && host.Error == "simulated import error"),
            "servicepack failures replace running status with the current error");
        worker.FailImports = false;
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(Targets(form).All(host => host.Result == "Abgeschlossen" && host.Error == ""),
            "a successful subsequent servicepack run clears the previous failed result");
        Assert(worker.Requests.Where(request => request.Action == "import-servicepacks").All(request => request.ServicePackFiles.Length == 2 &&
            request.DatabaseTargets.All(target => target.Namespaces.All(ns => !ns.StartsWith('%'))) && request.MsiPackages.Length == 0),
            "servicepack requests contain only the selected XMLs and filtered IRIS targets, without MSI operations");
        Assert(worker.Requests.Where(request => request.Action == "import-servicepacks")
            .All(request => request.RemoteInstallPath == (request.LocalExecution ? "" : @"D:\Servicepacks Ziel")),
            "remote servicepacks use the configured destination while local imports use their existing XML paths");
        Assert(worker.Requests.Where(request => request.Action == "import-servicepacks").All(request =>
            request.LocalExecution ? request.ServicePackFiles.Select(file => file.Path).SequenceEqual([classes, routines]) :
                request.ServicePackFiles.All(file => !new[] { classes, routines }.Contains(file.Path))),
            "only remote hosts receive snapshots; local requests load the original selection directly");
        Assert(File.ReadAllBytes(classes).SequenceEqual(original), "UI imports leave source files unchanged");
        worker.Requests.Clear(); selectedPlan = 1;
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        WorkerRequest remoteImport = worker.Requests.Single(request => request.Action == "import-servicepacks");
        Assert(!remoteImport.LocalExecution && remoteImport.Host == Targets(form)[1].Host &&
            remoteImport.DatabaseTargets is [var remoteTarget] && remoteTarget.Instance == "REMOTE2" && remoteTarget.Port == 56772 &&
            remoteTarget.Namespaces.SequenceEqual(["USER"]),
            "servicepacks reach only the selected remote server, instance and namespace, even when other instances have USER");
        worker.Requests.Clear(); selectedPlan = 0;
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        WorkerRequest localImport = worker.Requests.Single(request => request.Action == "import-servicepacks");
        Assert(localImport.LocalExecution && localImport.DatabaseTargets.Single().Namespaces.SequenceEqual(["USER"]) &&
            localImport.ServicePackFiles.Select(file => file.Path).SequenceEqual([classes, routines]),
            "a local namespace selection preserves original XML paths and excludes every unselected host and namespace");
        worker.Requests.Clear(); selectedPlan = null; emptySelection = true;
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(worker.Requests.All(request => request.Action == "database-inventory") && !Field<bool>(form, "busy") &&
            !Field<bool>(form, "loadingPackages"), "an empty namespace selection starts no import");
        emptySelection = false;
        DatabaseTargetSelectionTests(servicePacks: true);
        worker.Requests.Clear();
        worker.OnImport = () => Field<CancellationTokenSource>(form, "cancellation").Cancel();
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(worker.Requests.Count(request => request.Action == "import-servicepacks") == 1 &&
            Targets(form).Count(host => host.Log.Any(line => line.Contains("Servicepacks: Abgebrochen; Host wurde nicht gestartet."))) == 3,
            "cancellation reaches the active host and prevents all remaining host imports");
        using var actualWorker = new WorkerRunner();
        WorkerResult rejectedRequest = await actualWorker.RunAsync(new WorkerRequest { Action = "import-servicepacks", LocalExecution = true }, _ => { }, CancellationToken.None);
        Assert(!rejectedRequest.Success && rejectedRequest.Error.Contains("Keine XML-Servicepacks"),
            "the real embedded worker rejects empty imports before any IRIS connection");
    }

    private sealed class ServicePackRunner : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal Action? OnImport { get; set; }
        internal bool FailImports { get; set; }
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Action == "database-inventory") return Task.FromResult(new WorkerResult { Success = true,
                IrisInstances = [new() { Name = "REMOTE", Directory = "C:\\IRIS", SuperServerPort = 1972, Namespaces = ["USER", "%SYS", "APP"] },
                    new() { Name = "REMOTE2", Directory = "C:\\IRIS2", SuperServerPort = 56772, Namespaces = ["USER", "%SYS", "APP"] }] });
            Assert(request.Action == "import-servicepacks" && ct.CanBeCanceled && request.ServicePackFiles.All(file => File.Exists(file.Path)),
                "an active host uses existing XML paths and receives the cancellation token");
            OnImport?.Invoke();
            if (FailImports) return Task.FromResult(new WorkerResult { Success = false, Error = "simulated import error", Message = "Import fehlgeschlagen" });
            return Task.FromResult(new WorkerResult { Success = true, Message = "importiert und kompiliert", ServicePackResults =
                request.DatabaseTargets.SelectMany(target => target.Namespaces.Select(ns => new ServicePackNamespaceResult {
                    Instance = target.Instance, Namespace = ns, Success = true, Compiled = true, ImportedFiles = request.ServicePackFiles.Length })).ToArray() });
        }
        public void Dispose() { }
    }
}
