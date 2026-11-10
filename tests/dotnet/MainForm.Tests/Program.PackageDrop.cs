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
        var files = All(form).Single(control => control.Name == "filesCard");
        var source = Field<TextBox>(form, "softwarePath");
        var destination = Field<TextBox>(form, "txtRemotePath");
        var settings = (TableLayoutPanel)source.Parent!.Parent!;
        Assert(settings.GetRow(source.Parent) + 1 == settings.GetRow(destination.Parent!) &&
            !All(card).Contains(source) && All(card).Contains(Field<Button>(form, "browseSoftware")) &&
            All(card).Contains(Field<Button>(form, "readPackages")),
            "the source directory moves directly above the destination in settings while both package actions stay in the installation card");
        Assert(AppElevation.IsProcessElevated() ? Field<List<ShellFileDropTarget>>(form, "shellPackageDrops").Count > 0 :
            All(files).All(control => control.AllowDrop) && All(card).All(control => control.AllowDrop),
            "package drops work on labels, buttons, individual rows and collapsed section headers");
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
        var fiscalButton = Field<Button>(form, "btnFiscal");
        Drop(fiscalButton, payment);
        Assert(warning?.Contains("Fiscal", StringComparison.OrdinalIgnoreCase) == true && Field<string>(form, "paymentZip") == payment &&
            Field<string>(form, "fiscalZip") == fiscalOld, "a drop on an individual row rejects an unrelated product without changing the selection");
        warning = null;
        Drop(Field<Label>(form, "lblFiscal"), fiscal, paymentOld);
        Assert(warning is null && Field<string>(form, "fiscalZip") == fiscal && Field<string>(form, "paymentZip") == payment,
            "dropping on an individual version label changes only that row's component");
        Drop(files, suite, broken);
        Assert(warning is null && Field<string>(form, "serverSuiteZip") == suite &&
            Field<TableLayoutPanel>(form, "selectedPackagesTable").RowCount == 5 &&
            Field<Label>(form, "packageStatus").Text.Contains("1 Hinweis(e)") && Field<List<string>>(form, "additionalFiles").Contains(broken),
            "the individual section accepts packages by type and retains an unrecognized file as copy-only with its parser warning");
        Drop(dropArea, broken);
        Assert(warning is null && Field<List<string>>(form, "additionalFiles").Count(path => path == broken) == 1 &&
            Field<string>(form, "serverSuiteZip") == suite && !Field<bool>(form, "loadingPackages"),
            "unrecognized files are added once without losing valid package selections");
        foreach (string state in new[] { "busy", "selfUpdating", "loadingPackages", "preparingDatabaseUpload", "restartingAsAdministrator", "connectionClosing" })
        {
            SetField(form, state, true);
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
