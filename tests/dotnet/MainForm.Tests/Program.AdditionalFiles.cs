using System.Collections.Concurrent;
using System.IO.Compression;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void DropFiles(MainForm form, params string[] paths)
    {
        var area = All(form).Single(control => control.Name == "packageDropArea");
        var args = new DragEventArgs(new DataObject(DataFormats.FileDrop, paths), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
        typeof(Control).GetMethod("OnDragDrop", Private)!.Invoke(area, [args]);
        Complete(Field<Task>(form, "packageDropTask"));
    }

    private static void AdditionalFileSelectionTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "additional-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string payment = Path.Combine(root, "nt.payment_2.0.0.zip"), text = Path.Combine(root, "Grüße.txt"), data = Path.Combine(root, "data");
        string script = Path.Combine(root, "never-execute.ps1"), exe = Path.Combine(root, "third-party.exe"), zip = Path.Combine(root, "backup.zip");
        foreach (string path in new[] { payment, text, data, script }) File.WriteAllText(path, "synthetic file, never execute");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), exe);
        using (ZipFile.Open(zip, ZipArchiveMode.Create)) { }
        string thirdMsi = Path.Combine(root, "third-party.msi");
        MsiFixture.Create(thirdMsi, "Unrelated application", "1.0.0", null);
        var worker = new FakeRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        Populate(form);
        string? warning = null;
        SetField(form, "showPackageDropWarning", new Action<string>(message => warning = message));
        DropFiles(form, payment, text, data, script, exe, zip, thirdMsi, text);
        var extra = Field<List<string>>(form, "additionalFiles");
        var overview = Field<TableLayoutPanel>(form, "additionalFilesTable");
        Assert(warning is null && Field<string>(form, "paymentZip") == payment && extra.Count == 6 &&
            new[] { text, data, script, exe, zip, thirdMsi }.All(extra.Contains),
            "mixed drops retain known packages and accept arbitrary extensions, no extension, unknown EXE/MSI and non-package ZIPs as additional files");
        Assert(overview.Controls.OfType<Button>().Count() == 6 && overview.Controls.OfType<Label>().Any(label => label.Text == "Zusätzliche Dateien") &&
            Field<Label>(form, "selectedPackagesList").Text.Contains("Grüße.txt"),
            "the selected-package overview lists additional filenames with their own remove actions");
        var removed = overview.Controls.OfType<Button>().Single(button => Equals(button.Tag, text));
        typeof(Button).GetMethod("OnClick", Private)!.Invoke(removed, [EventArgs.Empty]);
        Assert(!extra.Contains(text) && File.Exists(text) && Field<string>(form, "paymentZip") == payment,
            "removing an additional file preserves its source and the defined package selection");
        foreach (string state in new[] { "busy", "selfUpdating", "loadingPackages", "preparingDatabaseUpload", "restartingAsAdministrator" })
        {
            SetField(form, state, true); Call(form, "UpdateButtons");
            Assert(overview.Controls.OfType<Button>().All(button => !button.Enabled), "additional file removal is locked during " + state);
            Call(form, "RemoveAdditionalFile", script);
            Assert(extra.Contains(script), "active processing preserves additional files during " + state);
            SetField(form, state, false);
        }
        warning = null; DropFiles(form, root);
        Assert(warning is not null && extra.Count == 5, "folders are rejected without altering the file selection");
        Assert(worker.Requests.IsEmpty, "selecting third-party files never executes a program, script or host action");
        string report = HtmlReport.Create([], "", "", DateTime.Now, false, additionalFiles: ["sample<&>.json"]);
        Assert(report.Contains("Zusätzliche Datei:") && report.Contains("sample&lt;&amp;&gt;.json") && !report.Contains("sample<&>"),
            "the HTML report identifies and encodes additional filenames");
        form.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(overview.Width, overview.Height);
        overview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "additional-files-overview.png"));
        form.Hide();
    }

    private static void AdditionalFileUpdateTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "additional-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string payment = Path.Combine(root, "nt.payment_2.0.0.zip"), ntc = Path.Combine(root, "control.msi"), extra = Path.Combine(root, "settings.json");
        File.WriteAllText(payment, "fixture, never installed"); File.WriteAllText(extra, "{\"fixture\":true}");
        MsiFixture.Create(ntc, "NovaTouch Control", "2026.4.9800", null);
        var worker = new AdditionalRunner { FailExtraHost = "PC-A.invalid" };
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        HostState[] remotes = Populate(form);
        foreach (HostState host in remotes.Skip(1)) host.IsSelected = false;
        Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
        Field<TextBox>(form, "txtRemotePath").Text = Path.Combine(root, "target");
        Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked = false;
        Field<CheckBox>(form, "chkSkipLocalPackageCopy").Checked = true;
        Field<CheckBox>(form, "chkSkipRemotePackageCopy").Checked = true;
        Field<CheckBox>(form, "chkSkipZipTransfer").Checked = true;
        SetField(form, "isProcessElevated", new Func<bool>(() => true));
        string confirmation = "";
        SetField(form, "confirmUpdate", new Func<string, bool>(text => { confirmation = text; return true; }));
        DropFiles(form, payment, ntc, extra);
        foreach (HostState host in Targets(form)) { host.Scanned = true; host.HasIrisInstances = false; host.InstalledComponents = UpdateComponents.Payment | (host.IsLocal ? UpdateComponents.Ntc : UpdateComponents.None); }
        Call(form, "RebuildRows");
        Complete((Task)Call(form, "RunUpdatesAsync", false)!);
        WorkerRequest[] installs = worker.Requests.Where(request => request.Action == "update").ToArray();
        WorkerRequest[] uploads = worker.Requests.Where(request => request.TransferFiles.Any(file => file.LocalPath == extra)).ToArray();
        Assert(installs.Length == 3 && installs.Count(request => request.LocalExecution) == 2 &&
            installs.Where(request => !request.LocalExecution).All(request => request.MsiPackage is null),
            "mixed selections keep the regular local Payment/NTC and remote Payment installation rules");
        Assert(uploads.Length == 2 && uploads.All(request => request.RemoteInstallPath == Path.Combine(root, "target") &&
            request.TransferFiles is [{ Sha256.Length: 64, RelativePath: "settings.json" }] && !request.SkipZipTransfer && !request.SkipRemotePackageCopy),
            "additional files are verified and copied to the explicit target on local and remote hosts even when package copying is disabled");
        WorkerRequest[] ordered = worker.Requests.ToArray();
        Assert(uploads.All(upload => Array.FindIndex(ordered, request => ReferenceEquals(request, upload)) >
            Array.FindLastIndex(ordered, request => request.Host == upload.Host && request.Action == "update")) &&
            confirmation.Contains("settings.json") && confirmation.Contains("nicht ausführen"),
            "the confirmation identifies copy-only files and each host receives them after its normal installations");
        HostState local = Targets(form).Single(host => host.IsLocal), remote = remotes[0];
        Assert(local.Updates!.AdditionalFilesCompleted && local.Result == "Erfolgreich" && remote.Updates!.Pending == UpdateComponents.None &&
            remote.CanRetryUpdate && remote.Error.Contains("Zusätzliche Dateien"),
            "a failed additional upload preserves successful installations and remains retryable with a clear error");
        File.Delete(payment); File.Delete(ntc);
        worker.Requests.Clear(); worker.FailExtraHost = "";
        Complete((Task)Call(form, "RunUpdatesAsync", true)!);
        Assert(worker.Requests.Count == 1 && worker.Requests.Single() is { Action: "transfer", Host: "PC-A.invalid" } &&
            remote.Updates!.AdditionalFilesCompleted && remote.Result == "Erfolgreich",
            "retrying an extra-file error uploads only that file to the failed host without reinstalling or requiring completed package sources");

        var onlyWorker = new AdditionalRunner();
        using var onlyFiles = new MainForm(preview: true, onlyWorker, new FakeRunner());
        HostState[] onlyHosts = Populate(onlyFiles);
        foreach (HostState host in onlyHosts) { host.Scanned = true; host.InstalledComponents = UpdateComponents.None; host.HasIrisInstances = false; }
        Field<CheckBox>(onlyFiles, "chkUploadToZzzSoftware").Checked = false;
        Field<TextBox>(onlyFiles, "txtRemotePath").Text = Path.Combine(root, "only-target");
        SetField(onlyFiles, "confirmUpdate", new Func<string, bool>(_ => true));
        DropFiles(onlyFiles, extra);
        Complete((Task)Call(onlyFiles, "RunUpdatesAsync", false)!);
        Assert(onlyWorker.Requests.Count(request => request.Action == "transfer") == 3 &&
            onlyWorker.Requests.All(request => request.Action is "check-upload-directory" or "transfer") &&
            onlyHosts.All(host => host.Updates?.AdditionalFilesCompleted == true),
            "an additional-files-only run reaches selected hosts without NovaTouch components and performs no installation");
        Field<TextBox>(onlyFiles, "txtRemotePath").Text = "relative-target";
        Field<CheckBox>(onlyFiles, "chkSkipRemotePackageCopy").Checked = true;
        Assert(!(bool)Call(onlyFiles, "IsUpdatePathValid", (object)onlyHosts)!,
            "additional files require an absolute destination even when package copying is disabled");
        AdditionalFileCollisionTest(root);
        AdditionalFileCombinedFailureTest(root, extra);
    }

    private static void AdditionalFileCombinedFailureTest(string root, string extra)
    {
        string payment = Path.Combine(root, "nt.payment_1.0.0.zip"); File.WriteAllText(payment, "fixture");
        var worker = new AdditionalRunner { FailCoreStaging = true, FailExtraHost = "LOCALHOST" };
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.Local;
        Field<TextBox>(form, "txtRemotePath").Text = Path.Combine(root, "failed-stage-target");
        Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.Payment, payment, "1.0.0"));
        Call(form, "AddAdditionalFiles", (object)new[] { extra });
        Call(form, "CaptureCredentials"); Call(form, "CaptureAdditionalFiles", true);
        Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", UpdateComponents.Payment, CancellationToken.None,
            new Func<string, CancellationToken, Task<string>>(WorkerRunner.Sha256Async))!);
        Complete((Task)Call(form, "PrepareAdditionalFilesAsync", UpdateComponents.Payment, CancellationToken.None)!);
        HostState local = Targets(form).Single(); local.Updates = new(UpdateComponents.Payment);
        Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
        Assert(local.Updates.ErrorText.Contains("fixture staging failed") && local.Updates.ErrorText.Contains("fixture transfer failed") &&
            worker.Requests.All(request => request.Action != "update"),
            "simultaneous package staging and additional upload failures retain both errors without starting an unprepared installation");
    }

    private static void AdditionalFileCollisionTest(string root)
    {
        string first = Path.Combine(root, "one"), second = Path.Combine(root, "two");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        string a = Path.Combine(first, "same.dat"), b = Path.Combine(second, "same.dat");
        File.WriteAllText(a, "first"); File.WriteAllText(b, "second");
        var worker = new FakeRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        Call(form, "AddAdditionalFiles", (object)new[] { a, b });
        Call(form, "CaptureAdditionalFiles", true);
        Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", UpdateComponents.None, CancellationToken.None,
            new Func<string, CancellationToken, Task<string>>(WorkerRunner.Sha256Async))!);
        bool blocked = false;
        try { Complete((Task)Call(form, "PrepareAdditionalFilesAsync", UpdateComponents.None, CancellationToken.None)!); }
        catch (InvalidDataException) { blocked = true; }
        Assert(blocked && worker.Requests.IsEmpty, "different files with the same target name are rejected before any host is changed");
        Call(form, "RemoveAdditionalFile", b);
        Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.Payment, b, "1.0.0"));
        Call(form, "CaptureAdditionalFiles", true);
        Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", UpdateComponents.Payment, CancellationToken.None,
            new Func<string, CancellationToken, Task<string>>(WorkerRunner.Sha256Async))!);
        blocked = false;
        try { Complete((Task)Call(form, "PrepareAdditionalFilesAsync", UpdateComponents.Payment, CancellationToken.None)!); }
        catch (InvalidDataException) { blocked = true; }
        Assert(blocked && worker.Requests.IsEmpty, "additional files cannot silently overwrite a selected package with the same target filename");
    }

    private static async Task AdditionalFileCopyTest()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "additional-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "never-execute.ps1"), target = Path.Combine(root, "target");
        File.WriteAllText(source, "throw 'This script must only be copied, never executed.'");
        using var worker = new WorkerRunner();
        var request = new WorkerRequest { Action = "stage-packages", LocalExecution = true, Host = "LOCALHOST", RemoteInstallPath = target,
            TransferFiles = [new() { LocalPath = source, RelativePath = Path.GetFileName(source), Sha256 = await WorkerRunner.Sha256Async(source, CancellationToken.None) }] };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        WorkerResult first = await worker.RunAsync(request, _ => { }, timeout.Token);
        Assert(first.Success && first.Changed && File.ReadAllText(Path.Combine(target, Path.GetFileName(source))) == File.ReadAllText(source),
            "the real worker copies arbitrary files byte-for-byte without executing their contents: " + first.Error);
        WorkerResult identical = await worker.RunAsync(request, _ => { }, timeout.Token);
        Assert(identical.Success && !identical.Changed, "an identical destination file is verified without retransmission");
        File.AppendAllText(source, "\n# changed fixture");
        request.TransferFiles[0].Sha256 = await WorkerRunner.Sha256Async(source, timeout.Token);
        WorkerResult replacement = await worker.RunAsync(request, _ => { }, timeout.Token);
        Assert(replacement.Success && replacement.Changed && File.ReadAllText(Path.Combine(target, Path.GetFileName(source))) == File.ReadAllText(source),
            "a changed file replaces its earlier destination copy and preserves the source");
    }

    private sealed class AdditionalRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal string FailExtraHost { get; set; } = "";
        internal bool FailCoreStaging { get; set; }
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action == "check-upload-directory") return Task.FromResult(new WorkerResult { Success = true, UploadRequired = false });
            if (request.Action == "stage-packages" && FailCoreStaging && request.TransferFiles.Any(file => file.LocalPath.EndsWith(".zip")))
                return Task.FromResult(new WorkerResult { Success = false, Error = "fixture staging failed" });
            if (request.Action is "transfer" or "stage-packages")
                return Task.FromResult(new WorkerResult { Success = request.Host != FailExtraHost, Changed = true,
                    Error = request.Host == FailExtraHost ? "fixture transfer failed" : "", Message = "fixture copied" });
            if (request.Action != "update") throw new InvalidOperationException("Unexpected action: " + request.Action);
            return Task.FromResult(new WorkerResult { Success = true, Changed = true,
                Services = [new() { Kind = "payment", Name = "nt.payment", ProductVersion = "2.0.0", State = "Running" }],
                NtcVersion = "2026.4.9800", InstalledPrograms = [new() { Kind = "ntc", DisplayName = "NovaTouch Control", Version = "2026.4.9800" }] });
        }
        public void Dispose() { }
    }
}
