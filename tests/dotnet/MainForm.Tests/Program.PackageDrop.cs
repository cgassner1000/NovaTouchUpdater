using System.IO.Compression;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void PackageDropTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "package-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string FileFixture(string name)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, "synthetic package; never installed");
            return path;
        }
        string paymentOld = FileFixture("nt.payment_1.0.0.zip"), payment = FileFixture("nt.payment_2.0.0.ZIP");
        string fiscalOld = FileFixture("nt.fiscal_1.0.0.zip"), fiscal = FileFixture("nt.fiscal_3.0.0.zip");
        string suite = FileFixture("nt.server.suite-2026.4.9999.zip"), broken = FileFixture("broken.zip");
        string ntc = Path.Combine(root, "control.msi"), pos = Path.Combine(root, "pos.msi");
        MsiFixture.Create(ntc, "NovaTouch Control", "2026.4.9800", null, "Nov.WaWi.exe", "2026.4.9800.12345");
        MsiFixture.Create(pos, "NovaTouch POS", "2026.4.9800", null);
        string archivePath = Path.Combine(root, "packages.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(ntc, "Control/control.msi");
            archive.CreateEntryFromFile(pos, "POS/pos.msi");
        }
        var originals = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllBytes);
        var worker = new FakeRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        Populate(form);
        string? warning = null;
        SetField(form, "showPackageDropWarning", new Action<string>(text => warning = text));
        var card = All(form).Single(control => control.Name == "localUpdateCard");
        var dropArea = All(card).Single(control => control.Name == "packageDropArea");
        var source = Field<TextBox>(form, "softwarePath");
        var destination = Field<TextBox>(form, "txtRemotePath");
        var settings = (TableLayoutPanel)source.Parent!.Parent!;
        Assert(settings.GetRow(source.Parent) + 1 == settings.GetRow(destination.Parent!) &&
            !All(card).Contains(source) && All(card).Contains(Field<Button>(form, "browseSoftware")) &&
            All(card).Contains(Field<Button>(form, "readPackages")) && All(card).Contains(Field<Button>(form, "browsePackageFiles")),
            "the source directory stays in settings while folder browsing, file browsing and package refreshing belong to the installation card");
        Assert(AppElevation.IsProcessElevated() ? Field<List<ShellFileDropTarget>>(form, "shellPackageDrops").Count > 0 :
            All(card).All(control => control.AllowDrop),
            "package drops work throughout the installation card and on its buttons");
        var sourceDirectory = Path.Combine(root, "configured source");
        source.Text = sourceDirectory;
        Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.Fiscal, fiscalOld, "1.0.0"));
        DragEventArgs Enter(Control surface, IDataObject data, DragDropEffects allowed = DragDropEffects.Copy)
        {
            var args = new DragEventArgs(data, 0, 0, 0, allowed, DragDropEffects.None);
            typeof(Control).GetMethod("OnDragEnter", Private)!.Invoke(surface, [args]);
            return args;
        }
        DataObject Data(params string[] paths) => new(DataFormats.FileDrop, paths);
        void Drop(Control surface, params string[] paths)
        {
            var args = new DragEventArgs(Data(paths), 0, 0, 0, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None);
            typeof(Control).GetMethod("OnDragDrop", Private)!.Invoke(surface, [args]);
            Complete(Field<Task>(form, "packageDropTask"));
        }
        Assert(Enter(dropArea, Data(payment)).Effect == DragDropEffects.Copy &&
            Enter(dropArea, Data(payment), DragDropEffects.Move).Effect == DragDropEffects.None &&
            Enter(dropArea, Data("notes.txt")).Effect == DragDropEffects.Copy &&
            Enter(dropArea, new DataObject(DataFormats.Text, payment)).Effect == DragDropEffects.None,
            "all file drops are accepted with copy semantics; move-only and plain text drags are rejected");
        Drop(dropArea, paymentOld, payment, payment, ntc, archivePath);
        var msi = Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages");
        Assert(warning is null && Field<string>(form, "paymentZip") == payment && Field<string>(form, "fiscalZip") == fiscalOld &&
            msi.Count == 2 && msi[UpdateComponents.Ntc].ProductVersion == "2026.4.9800.12345" &&
            source.Text == sourceDirectory && Field<TableLayoutPanel>(form, "selectedPackagesTable").RowCount == 4,
            "a multi-file drop detects MSI and ZIP contents, selects the highest version and preserves unrelated packages and the configured directory");
        Drop(Field<Button>(form, "browsePackageFiles"), fiscal);
        Assert(warning is null && Field<string>(form, "fiscalZip") == fiscal && Field<string>(form, "paymentZip") == payment,
            "dropping on the new file button uses the common pipeline and preserves unrelated products");
        Drop(dropArea, suite, broken);
        Assert(warning is null && Field<string>(form, "serverSuiteZip") == suite &&
            Field<TableLayoutPanel>(form, "selectedPackagesTable").RowCount == 5 &&
            Field<Label>(form, "packageStatus").Text.Contains("1 Hinweis(e)") && Field<List<string>>(form, "additionalFiles").Contains(broken),
            "the installation card detects package types and retains unrelated files as copy-only with parser warnings");
        Drop(dropArea, broken);
        Assert(warning is null && Field<List<string>>(form, "additionalFiles").Count(path => path == broken) == 1 &&
            Field<string>(form, "serverSuiteZip") == suite && !Field<bool>(form, "loadingPackages"),
            "unrecognized files are added once without losing valid package selections");
        string paymentExe = FileFixture("Nt.Payment_4.0.0.exe"), fiscalExe = FileFixture("NT.FISCAL-4.0.0.EXE");
        Drop(dropArea, paymentExe);
        Assert(warning?.Contains("nt.payment", StringComparison.OrdinalIgnoreCase) == true && warning.Contains("ZIP") &&
            Field<string>(form, "paymentZip") == payment && !Field<List<string>>(form, "additionalFiles").Contains(paymentExe),
            "Payment EXE drops show a ZIP instruction without replacing packages or adding a copy-only EXE");
        warning = null;
        Drop(dropArea, fiscalExe, fiscal);
        Assert(warning?.Contains("nt.fiscal", StringComparison.OrdinalIgnoreCase) == true && warning.Contains("ZIP") &&
            Field<string>(form, "fiscalZip") == fiscal && !Field<List<string>>(form, "additionalFiles").Contains(fiscalExe),
            "a mixed Fiscal EXE and valid ZIP drop retains the valid ZIP and rejects only the EXE");
        warning = null;
        int warningsShown = 0;
        SetField(form, "showPackageDropWarning", new Action<string>(text => { warning = text; warningsShown++; }));
        Drop(dropArea, paymentExe, fiscalExe);
        Assert(warningsShown == 1 && warning!.Contains("nt.payment") && warning.Contains("nt.fiscal") &&
            Field<string>(form, "paymentZip") == payment && Field<string>(form, "fiscalZip") == fiscal,
            "multiple unsupported service EXEs produce one message and preserve the existing selection");
        using var picker = MainForm.CreatePackageFileDialog();
        Assert(picker.Multiselect && picker.CheckFileExists && picker.Filter.Contains("*.msi") && picker.Filter.Contains("*.*"),
            "the new file dialog permits multiple packages and other files");
        warning = null;
        SetField(form, "choosePackageFiles", new Func<string[]?>(() => [payment, fiscal, ntc]));
        var fileButton = Field<Button>(form, "browsePackageFiles");
        typeof(Button).GetMethod("OnClick", Private)!.Invoke(fileButton, [EventArgs.Empty]);
        Complete(Field<Task>(form, "packageDropTask"));
        Assert(warning is null && Field<string>(form, "paymentZip") == payment && Field<string>(form, "fiscalZip") == fiscal &&
            source.Text == sourceDirectory && msi.Count == 2, "file browsing shares multi-file detection and preserves unrelated packages and the source folder");
        SetField(form, "choosePackageFiles", new Func<string[]?>(() => [paymentExe]));
        typeof(Button).GetMethod("OnClick", Private)!.Invoke(fileButton, [EventArgs.Empty]);
        Complete(Field<Task>(form, "packageDropTask"));
        Assert(warning?.Contains("nt.payment") == true && !Field<List<string>>(form, "additionalFiles").Contains(paymentExe),
            "the file picker applies the same Payment ZIP requirement as drag and drop");
        SetField(form, "choosePackageFiles", new Func<string[]?>(() => null));
        typeof(Button).GetMethod("OnClick", Private)!.Invoke(fileButton, [EventArgs.Empty]);
        Assert(Field<string>(form, "paymentZip") == payment && !Field<bool>(form, "loadingPackages"),
            "cancelling file browsing leaves the selected packages intact");
        foreach (string state in new[] { "busy", "selfUpdating", "loadingPackages", "preparingDatabaseUpload", "restartingAsAdministrator", "connectionClosing" })
        {
            SetField(form, state, true);
            bool pickerOpened = false;
            SetField(form, "choosePackageFiles", new Func<string[]?>(() => { pickerOpened = true; return [paymentOld]; }));
            typeof(Button).GetMethod("OnClick", Private)!.Invoke(fileButton, [EventArgs.Empty]);
            Assert(!pickerOpened, "file browsing cannot change captured packages during " + state);
            Assert(Enter(dropArea, Data(paymentOld)).Effect == DragDropEffects.None, "package drops are disabled during " + state);
            Drop(dropArea, paymentOld);
            Assert(Field<string>(form, "paymentZip") == payment, "active work keeps its package selection during " + state);
            SetField(form, state, false);
        }
        Assert(worker.Requests.IsEmpty && originals.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)),
            "drag and drop changes only the selection, leaving source files unchanged and starting no host action or installer");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { Complete(Field<SoftwarePackageCatalog>(form, "packageCatalog").ReadFilesAsync([payment], cancelled.Token)); }
        catch (OperationCanceledException) { stopped = true; }
        Assert(stopped, "reading dropped packages honors cancellation before accessing sources");
        string config = Path.Combine(root, "config.json");
        AppConfigStore.Save(config, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
        using var restored = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        Call(restored, "ApplyLoadedConfig", AppConfigStore.Load(config));
        Assert(Field<TextBox>(restored, "softwarePath").Text == sourceDirectory,
            "the relocated source directory still persists in configuration");
        using var preview = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        preview.ClientSize = new Size(1710, 980);
        preview.Show(); Application.DoEvents();
        var previewDrop = All(preview).Single(control => control.Name == "packageDropArea");
        using (var dropBitmap = new Bitmap(previewDrop.Width, previewDrop.Height))
        {
            previewDrop.DrawToBitmap(dropBitmap, new Rectangle(Point.Empty, dropBitmap.Size));
            dropBitmap.Save(Path.Combine(TestWorkspace.Artifacts, "package-drop-area.png"));
            Enter(previewDrop, Data(payment));
            previewDrop.DrawToBitmap(dropBitmap, new Rectangle(Point.Empty, dropBitmap.Size));
            dropBitmap.Save(Path.Combine(TestWorkspace.Artifacts, "package-drop-area-active.png"));
        }
        var previewSettings = All(preview).OfType<ExpandableSection>().Single(control => control.Name == "settingsCard");
        previewSettings.Expand(); Application.DoEvents();
        Assert(Field<TextBox>(preview, "softwarePath").Visible && Field<TextBox>(preview, "txtRemotePath").Visible,
            "both directory fields are visible together when settings are opened");
        using var bitmap = new Bitmap(previewSettings.Width, previewSettings.Height);
        previewSettings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "package-source-settings.png"));
        preview.Hide();
    }
}
