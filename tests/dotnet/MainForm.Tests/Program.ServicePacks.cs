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
        var check = Field<Button>(form, "btnDatabaseCheck");
        int popups = 0;
        SetField(form, "showBetaActivated", new Action(() => popups++));
        using var before = new Bitmap(image.Width, image.Height);
        image.DrawToBitmap(before, image.ClientRectangle);
        Cursor cursor = image.Cursor;
        Assert(!gate.Enabled && !button.Visible && check.Text == "Check Database" && check.Visible,
            "servicepacks are hidden initially while Check Database remains a regular function");
        for (int click = 0; click < 4; click++) gate.RegisterClick(image, true);
        gate.RegisterClick(check, true);
        for (int click = 0; click < 4; click++) gate.RegisterClick(image, true);
        Assert(!gate.Enabled && popups == 0 && !button.Visible, "another control resets the five consecutive logo clicks");
        var fifth = Message.Create(image.Handle, 0x0203, IntPtr.Zero, IntPtr.Zero);
        Assert(!gate.PreFilterMessage(ref fifth) && gate.Enabled && popups == 1 && button.Visible,
            "the fifth logo click, including native double-click messages, activates beta once without consuming the click");
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
                    $"the beta button keeps both lines and the regular button height at {width}px / {scale:P0}");
            }
        }
        using var fresh = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        Assert(!Field<BetaFeatureGate>(fresh, "betaFeatures").Enabled, "beta activation is limited to one application session");
    }

    private static async Task ServicePackTests()
    {
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
            Assert(xml.InnerText.Contains("Grüße 🎉") && File.ReadAllBytes(classes).SequenceEqual(original),
                "UTF-16 XML is normalized without changing Unicode content or source files");
            Assert(await WorkerRunner.Sha256Async(snapshot, CancellationToken.None) == selection.Files[0].Sha256,
                "servicepack snapshots are protected by SHA256");
        }
        Assert(!File.Exists(snapshot), "only the temporary servicepack snapshots are removed afterwards");
        foreach (string bad in new[] { "<Export><Global name=\"unsafe\" /></Export>", "<Export/>", "<Export><Class name=\"User.*\" /></Export>",
            "<!DOCTYPE Export [<!ENTITY x SYSTEM 'file:///C:/missing'>]><Export><Class name=\"User.Bad\">&x;</Class></Export>", "<Export><Routine" })
        {
            string path = Path.Combine(root, "invalid.xml"); File.WriteAllText(path, bad);
            bool rejected = false;
            try { using var ignored = await ServicePackSelection.CreateAsync([path], CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidDataException or XmlException) { rejected = true; }
            Assert(rejected, "malformed, empty, non-code and external-entity XML is rejected before database access");
        }
        using var picker = MainForm.CreateServicePackFileDialog();
        Assert(picker.Multiselect && picker.Filter.Contains("*.xml"), "any number of XML servicepacks can be selected together");

        var worker = new ServicePackRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        HostState[] selected = Populate(form);
        Field<ComboBox>(form, "executionMode").SelectedIndex = 1;
        SetField(form, "readIrisInventory", new Func<CancellationToken, Task<IrisInventoryResult>>(_ => Task.FromResult(
            new IrisInventoryResult([new("LOCAL", "C:\\IRIS", "", "", ["USER", "%SYS", "APP"], SuperServerPort: 1972)], []))));
        SetField(form, "showBetaActivated", new Action(() => { }));
        var gate = Field<BetaFeatureGate>(form, "betaFeatures");
        var logo = All(form).OfType<AppMark>().Single();
        ServicePackHostResult[]? report = null;
        string? warning = null;
        SetField(form, "showServicePackResults", new Action<ServicePackHostResult[]>(results => report = results));
        SetField(form, "showServicePackWarning", new Action<string>(text => warning = text));
        bool confirm = false;
        SetField(form, "confirmServicePackImport", new Func<ServicePackHostPlan[], int, bool>((plans, files) =>
        {
            Assert(files == 2 && plans.SelectMany(plan => plan.Targets).All(target => target.Namespaces.All(ns => !ns.StartsWith('%'))),
                "confirmation is prepared for the selected hosts and excludes all percent namespaces");
            return confirm;
        }));
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(worker.Requests.Count == 0, "hidden beta servicepacks cannot be invoked before activation");
        for (int click = 0; click < 5; click++) gate.RegisterClick(logo, true);
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(worker.Requests.All(request => request.Action == "database-inventory"), "declining confirmation imports no code");
        worker.Requests.Clear(); confirm = true;
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(warning is null && report?.Length == 4 && worker.Requests.Count(request => request.Action == "import-servicepacks") == 4,
            "confirmed servicepacks run on local and all selected remote hosts and report each host");
        Assert(worker.Requests.Where(request => request.Action == "import-servicepacks").All(request => request.ServicePackFiles.Length == 2 &&
            request.DatabaseTargets.All(target => target.Namespaces.All(ns => !ns.StartsWith('%'))) && request.MsiPackages.Length == 0),
            "servicepack requests contain only the selected XMLs and filtered IRIS targets, without MSI operations");
        Assert(File.ReadAllBytes(classes).SequenceEqual(original), "UI imports leave source files unchanged");
        worker.Requests.Clear();
        worker.OnImport = () => Field<CancellationTokenSource>(form, "cancellation").Cancel();
        await (Task)Call(form, "ImportServicePackFilesAsync", new object[] { new[] { classes, routines } })!;
        Assert(worker.Requests.Count(request => request.Action == "import-servicepacks") == 1 && report!.Count(item => !item.Result.Success) == 3,
            "cancellation completes the accepted host and prevents all remaining host imports");
        using var actualWorker = new WorkerRunner();
        WorkerResult rejectedRequest = await actualWorker.RunAsync(new WorkerRequest { Action = "import-servicepacks", LocalExecution = true }, _ => { }, CancellationToken.None);
        Assert(!rejectedRequest.Success && rejectedRequest.Error.Contains("Keine XML-Servicepacks"),
            "the real embedded worker rejects empty imports before any IRIS connection");
    }

    private sealed class ServicePackRunner : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal Action? OnImport { get; set; }
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Action == "database-inventory") return Task.FromResult(new WorkerResult { Success = true,
                IrisInstances = [new() { Name = "REMOTE", Directory = "C:\\IRIS", SuperServerPort = 1972, Namespaces = ["USER", "%SYS", "APP"] }] });
            Assert(request.Action == "import-servicepacks" && !ct.CanBeCanceled && request.ServicePackFiles.All(file => File.Exists(file.Path)),
                "an accepted host finishes with immutable snapshots and without forced cancellation");
            OnImport?.Invoke();
            return Task.FromResult(new WorkerResult { Success = true, Message = "importiert und kompiliert", ServicePackResults =
                request.DatabaseTargets.SelectMany(target => target.Namespaces.Select(ns => new ServicePackNamespaceResult {
                    Instance = target.Instance, Namespace = ns, Success = true, Compiled = true, ImportedFiles = request.ServicePackFiles.Length })).ToArray() });
        }
        public void Dispose() { }
    }
}
