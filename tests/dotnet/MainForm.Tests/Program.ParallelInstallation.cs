using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static readonly string[] installKinds = ["payment", "fiscal", "serversuite", "pos", "ntc", "ntv", "oman"];

    private static void ParallelInstallationTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "parallel-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Complete(ParallelInstallationWorkflowAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationWorkflowAsync(root, true), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationRetryAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationCompilationCancelAsync(root, false), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationCompilationCancelAsync(root, true), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationCompilationCancelAsync(root, false, true), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationCompilationCancelAsync(root, true, true), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationSharedTargetAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationSharedMsiTargetAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationSharedMsiTargetAsync(root, true), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationSharedMsiTargetAsync(root, true, true), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationFailedMsiAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationUploadRetryAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationSelectedMsiAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationSkippedMsiAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationSkippedMsiAsync(root, true), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationUploadPrerequisiteAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationRemotePrerequisiteAsync(root), TimeSpan.FromSeconds(45));
            Complete(ParallelInstallationRemoteControlFailureAsync(root), TimeSpan.FromSeconds(45));
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installation fixtures outside artifacts.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static MainForm ParallelInstallationForm(InstallationRunner runner, string root, bool remote = false)
    {
        var form = new MainForm(preview: true, runner, new FakeRunner());
        _ = form.Handle;
        Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.Local;
        Field<CheckBox>(form, "chkSkipLocalPackageCopy").Checked = true;
        SetField(form, "isProcessElevated", new Func<bool>(() => true));
        SetField(form, "confirmUpdate", new Func<string, bool>(_ => true));
        SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(new[]
            { new IrisUploadTarget { Instance = "SYNTHETIC", Port = 1972, Namespaces = ["USER"] } })));
        foreach (string kind in installKinds)
        {
            string file = Path.Combine(root, kind + (kind is "payment" or "fiscal" or "serversuite" ? ".zip" : ".msi"));
            File.WriteAllText(file, "Synthetic package; never installed.");
            if (kind is "payment" or "fiscal" or "serversuite")
            {
                string field = kind == "serversuite" ? "serverSuite" : kind;
                SetField(form, field + "Zip", file); SetField(form, field + "Version", "2.0.0"); SetField(form, field + "Hash", "synthetic");
            }
            else Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages")[InstallComponent(kind)] =
                new() { Kind = kind, ProductName = HostUpdateState.Label(InstallComponent(kind)), ProductVersion = "2.0.0", PayloadPath = file };
        }
        if (remote)
        {
            var hosts = Field<List<HostState>>(form, "hosts");
            hosts.Clear();
            hosts.Add(new() { Host = "REMOTE-MSI.invalid", Scanned = true });
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.Remote;
            Field<CheckBox>(form, "chkSkipZipTransfer").Checked = true;
            Call(form, "RebuildRows");
        }
        HostState host = Targets(form).Single();
        Call(form, "ReadScanInventory", host, InstallationRunner.Snapshot("", "1.0.0"));
        host.Scanned = true; host.Updates = new(UpdateComponents.All);
        Call(form, "CaptureCredentials");
        return form;
    }

    private static UpdateComponents InstallComponent(string kind) => kind switch
    {
        "payment" => UpdateComponents.Payment, "fiscal" => UpdateComponents.Fiscal, "serversuite" => UpdateComponents.ServerSuite,
        "pos" => UpdateComponents.Pos, "ntc" => UpdateComponents.Ntc, "ntv" => UpdateComponents.Ntv, _ => UpdateComponents.Oman
    };

    private static async Task WaitForInstallAsync(Func<bool> ready)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Installation fixture did not reach its expected state.");
            await Task.Delay(5);
        }
    }

    private static async Task ParallelInstallationWorkflowAsync(string root, bool remote = false)
    {
        var runner = new InstallationRunner { HoldCompilation = true, HoldUploads = true };
        using var form = ParallelInstallationForm(runner, root, remote);
        Field<CheckBox>(form, "chkCompileAfterInstallation").Checked = true;
        SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(new[]
            { new IrisUploadTarget { Instance = "SYNTHETIC", Port = 1972, Namespaces = ["USER"] } })));
        HostState host = Targets(form).Single();
        Task run = (Task)Call(form, "RunUpdatesAsync", false)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 4);
            Assert(new[] { "payment", "fiscal", "serversuite", "ntc" }.All(kind => runner.Active.ContainsKey(kind)),
                "services and the first MSI really run concurrently on the same local or remote target");
            foreach (string kind in new[] { "ntc", "pos", "ntv", "oman" })
            {
                await WaitForInstallAsync(() => runner.Count(kind) == 1);
                Assert(!runner.CompilationRunning, "compilation cannot start while any selected MSI is still installing");
                if (kind == "ntc")
                    Assert(runner.Requests.All(request => request.Action != "publish-msi-packages"), "a held NTC installer blocks every database package upload, including remote-only runs");
                if (kind != "ntc")
                {
                    await WaitForInstallAsync(() => runner.Uploads.ContainsKey("ntc"));
                    Assert(runner.Active.ContainsKey(kind) && !host.Updates!.DatabaseUploadCompleted,
                        "the verified NTC upload overlaps later MSI installs without completing all package uploads");
                }
                runner.Finish(kind);
            }
            await WaitForInstallAsync(() => runner.CompilationRunning);
            Assert(runner.Active.Count == 3 && !run.IsCompleted && host.Phase.Contains("Novacom-NT-BOH.xml"),
                "compilation starts after the final MSI while uploads and independent service installations are still running");
            runner.InstallProgress["serversuite"](new() { Phase = "Installieren", Message = "Synthetic Suite update", ProgressPercent = 65 });
            Assert(host.ProgressPercent == 33 && host.Phase.Contains("Novacom-NT-BOH.xml") &&
                host.Updates!.ProgressFor(UpdateComponents.ServerSuite)?.Percent == 65,
                "concurrent service progress updates its own column without replacing active XML compilation progress");
            foreach (string kind in new[] { "ntc", "pos", "ntv", "oman" })
            {
                await WaitForInstallAsync(() => runner.Uploads.ContainsKey(kind));
                runner.Uploads[kind].TrySetResult(new() { Success = true, Changed = true, DatabaseUploadedKinds = [kind] });
            }
            await WaitForInstallAsync(() => host.Updates!.DatabaseUploadCompleted);
            Assert(!run.IsCompleted && host.ProgressPercent == 33, "uploads do not overwrite active XML compilation progress");
            runner.Compilation.TrySetResult(new() { Success = true, Changed = true });
            await WaitForInstallAsync(() => !runner.CompilationRunning);
            Assert(!run.IsCompleted && Field<bool>(form, "busy"), "the update cannot finish while any service installation is still active");
            foreach (string kind in new[] { "payment", "fiscal", "serversuite" }) runner.Finish(kind);
            await run;
            Assert(runner.MsiMaximum == 1 && runner.Requests.Where(request => request.MsiPackage is not null).Select(request => request.MsiPackage!.Kind)
                .SequenceEqual(["ntc", "pos", "ntv", "oman"]), "NTC, POS, Voucher and SOL install strictly in order with only one MSI active");
            Assert(host.Payment == "2.0.0" && host.Fiscal == "2.0.0" && host.ServerSuite == "2.0.0" && host.PosVersion == "2.0.0" &&
                host.NtcVersion == "2.0.0" && host.NtvVersion == "2.0.0" && host.OmanVersion == "2.0.0" && host.InstalledPrograms.All(program => program.Version == "2.0.0"),
                "late stale inventory snapshots cannot regress any already verified program version");
            Assert(host.Updates is { Pending: UpdateComponents.None, SoftwareCompilationCompleted: true, DatabaseUploadCompleted: true } && host.Result == "Erfolgreich",
                "the complete update waits for all installations and then performs automatic compilation and publication");
            Assert(runner.Requests.All(request => request.Host == host.Host && request.LocalExecution == !remote),
                "installations, compilation and uploads stay on the selected target without adding a local installation in remote mode");
            Assert(runner.Requests.Where(request => request.Action == "publish-msi-packages").Select(request => request.MsiPackages.Single().Kind)
                .SequenceEqual(["ntc", "pos", "ntv", "oman"]), "each MSI is uploaded once and includes only the already verified package");
            var table = Field<DataGridView>(form, "table");
            Assert(new[] { "ntc", "pos", "ntv", "oman" }.Select(name => table.Columns[name].DisplayIndex).SequenceEqual(
                Enumerable.Range(table.Columns["ntc"].DisplayIndex, 4)), "program columns follow the same NTC, POS, Voucher, SOL order");
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private static async Task ParallelInstallationRetryAsync(string root)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root);
        HostState host = Targets(form).Single();
        using var cancel = new CancellationTokenSource();
        Task run = (Task)Call(form, "UpdateHostAsync", host, cancel.Token, NoRetryDelay)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 4);
            runner.Finish("payment", failure: true);
            await WaitForInstallAsync(() => runner.Count("payment") == 2);
            Assert(runner.Active.ContainsKey("serversuite") && runner.Active.ContainsKey("fiscal"), "Payment retries independently without waiting for the other service installations");
            runner.Finish("payment", 2); runner.Finish("fiscal");
            await WaitForInstallAsync(() => runner.Count("ntc") == 1);
            runner.Finish("ntc", failure: true);
            await WaitForInstallAsync(() => runner.Count("ntc") == 2);
            Assert(runner.Count("pos") == 0, "an NTC retry completes before the MSI sequence advances to POS");
            runner.Finish("ntc", 2);
            await WaitForInstallAsync(() => runner.Count("pos") == 1 && host.Updates!.ProgressFor(UpdateComponents.Fiscal)?.Status == ComponentUpdateStatus.Succeeded);
            Assert(runner.Active.ContainsKey("serversuite"), "MSI retries and the next MSI do not wait for an independent service installation");
            Assert(runner.Requests.All(request => request.Action != "compile-software-packages"), "the disabled compilation switch prevents early compilation");
            cancel.Cancel();
            bool cancelled = false;
            try { await run; } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && runner.Count("ntv") == 0 && runner.Count("oman") == 0 &&
                host.Updates!.ProgressFor(UpdateComponents.Payment)?.Status == ComponentUpdateStatus.Succeeded &&
                host.Updates.ProgressFor(UpdateComponents.Pos)?.Status == ComponentUpdateStatus.Cancelled,
                "cancellation stops active lanes, prevents queued Voucher/SOL and preserves verified components");
            runner.ReleaseAll();
            await (Task)Call(form, "UpdateHostAsync", host, CancellationToken.None, NoRetryDelay)!;
            Assert(runner.Count("payment") == 2 && runner.Count("fiscal") == 1 && runner.Count("serversuite") == 2 && runner.Count("pos") == 2 &&
                runner.Count("ntv") == 1 && runner.Count("oman") == 1 && host.Updates!.Pending == UpdateComponents.None,
                "resuming repeats only unfinished programs and still includes Voucher and SOL");
        }
        finally { cancel.Cancel(); runner.ReleaseAll(); try { await run; } catch (OperationCanceledException) { } }
    }

    private static async Task ParallelInstallationCompilationCancelAsync(string root, bool afterInstallers, bool remote = false)
    {
        var runner = new InstallationRunner { HoldCompilation = true, HoldUploads = true };
        using var form = ParallelInstallationForm(runner, root, remote);
        Field<CheckBox>(form, "chkCompileAfterInstallation").Checked = true;
        SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(new[]
            { new IrisUploadTarget { Instance = "SYNTHETIC", Port = 1972, Namespaces = ["USER"] } })));
        Task run = (Task)Call(form, "RunUpdatesAsync", false)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 4);
            await WaitForInstallAsync(() => runner.Active.ContainsKey("ntc"));
            runner.Finish("ntc");
            await WaitForInstallAsync(() => runner.Uploads.ContainsKey("ntc") && runner.Active.ContainsKey("pos"));
            if (afterInstallers)
            {
                runner.ReleaseInstallers();
                await WaitForInstallAsync(() => runner.CompilationRunning);
            }
            Call(form, "CancelCurrentOperation");
            await run;
            Assert(!runner.CompilationRunning && runner.ActiveUploads == 0 && runner.Active.IsEmpty && !Field<bool>(form, "busy") &&
                (afterInstallers || runner.Count("ntv") == 0 && runner.Count("oman") == 0),
                "cancel stops the active upload, installers and late compilation before the UI becomes idle");
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private static async Task ParallelInstallationSharedTargetAsync(string root)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root);
        HostState host = Targets(form).Single();
        foreach (var service in host.Services.Where(service => service.Kind is "payment" or "fiscal")) service.InstallDir = @"C:\Synthetic\Shared";
        Task run = (Task)Call(form, "UpdateHostAsync", host, CancellationToken.None, NoRetryDelay)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 3);
            Assert(runner.Count("fiscal") == 0 && runner.Active.ContainsKey("serversuite") && runner.Active.ContainsKey("ntc"),
                "overlapping Payment/Fiscal targets are serialized while independent installations still overlap");
            runner.Finish("payment");
            await WaitForInstallAsync(() => runner.Count("fiscal") == 1);
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private static async Task ParallelInstallationSharedMsiTargetAsync(string root, bool remote = false, bool packageConflict = false)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root, remote);
        HostState host = Targets(form).Single();
        if (packageConflict)
        {
            SetField(form, "activeSkipZipTransfer", false);
            SetField(form, "activeSkipRemotePackageCopy", false);
            Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages")[UpdateComponents.Ntc].PayloadPath = Field<string>(form, "paymentZip");
        }
        else
        {
            string shared = @"C:\Synthetic\SharedServiceAndMsi";
            host.Services.Single(service => service.Kind == "payment").InstallDir = shared;
            host.InstalledPrograms.Single(program => program.Kind == "ntc").InstallLocation = shared;
            runner.Locations["ntc"] = shared;
        }
        Task run = (Task)Call(form, "UpdateHostAsync", host, CancellationToken.None, NoRetryDelay)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 3);
            Assert(runner.Count("ntc") == 0 && runner.Active.ContainsKey("payment"),
                packageConflict ? "service and MSI transfers sharing one persistent target ZIP are serialized" :
                    "MSI installation waits for the service sharing its actual installation directory");
            runner.Finish("payment");
            await WaitForInstallAsync(() => runner.Active.ContainsKey("ntc"));
            Assert(runner.Active.ContainsKey("fiscal") && runner.Active.ContainsKey("serversuite"),
                "shared targets delay only the conflicting service; independent service installations still overlap the MSI sequence");
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private static async Task ParallelInstallationFailedMsiAsync(string root)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root);
        HostState host = Targets(form).Single();
        Field<CheckBox>(form, "chkCompileAfterInstallation").Checked = true;
        Task run = (Task)Call(form, "RunUpdatesAsync", false)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 4);
            foreach (string kind in new[] { "payment", "fiscal", "serversuite" }) runner.Finish(kind);
            await WaitForInstallAsync(() => runner.Active.ContainsKey("ntc"));
            runner.Finish("ntc");
            await WaitForInstallAsync(() => runner.Active.ContainsKey("pos"));
            runner.gates[("pos", 1)].TrySetResult(new() { Success = false, AutomaticRetriesExhausted = true, Error = "Synthetic POS install failed" });
            runner.ReleaseInstallers();
            await run;
            Assert(runner.Requests.All(request => request.Action != "compile-software-packages") &&
                runner.Requests.Where(request => request.Action == "publish-msi-packages").All(request => request.MsiPackages.All(package => package.Kind != "pos")) &&
                host.Updates!.Pending == UpdateComponents.Pos && host.Updates.SoftwareCompilationError.Contains("wartet"),
                "a failed MSI is not published and blocks compilation while other MSI successes are retained");
            await (Task)Call(form, "RunUpdatesAsync", true)!;
            Assert(runner.Count("ntc") == 1 && runner.Count("pos") == 2 && runner.Count("ntv") == 1 && runner.Count("oman") == 1 &&
                host.Updates is { Pending: UpdateComponents.None, SoftwareCompilationCompleted: true, DatabaseUploadCompleted: true } && !host.CanRetryUpdate,
                "retry recovers only the failed MSI, publishes it and then performs the deferred compilation");
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private static async Task ParallelInstallationUploadRetryAsync(string root)
    {
        var runner = new InstallationRunner { HoldUploads = true };
        using var form = ParallelInstallationForm(runner, root);
        Field<CheckBox>(form, "chkCompileAfterInstallation").Checked = true;
        HostState host = Targets(form).Single();
        Task run = (Task)Call(form, "RunUpdatesAsync", false)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 4);
            runner.ReleaseInstallers();
            foreach (string kind in new[] { "ntc", "pos", "ntv", "oman" })
            {
                await WaitForInstallAsync(() => runner.Uploads.ContainsKey(kind));
                runner.Uploads[kind].TrySetResult(new() { Success = kind != "pos", Changed = kind != "pos", Error = kind == "pos" ? "Synthetic POS upload failed" : "" });
            }
            await run;
            Assert(host.Updates is { SoftwareCompilationCompleted: true, DatabaseUploadCompleted: false } && host.Error.Contains("Synthetic POS upload failed"),
                "later successful uploads retain an earlier package failure; compilation depends on installations");
            runner.HoldUploads = false;
            int before = runner.Requests.Count;
            await (Task)Call(form, "RunUpdatesAsync", true)!;
            WorkerRequest[] retry = runner.Requests.Skip(before).ToArray();
            Assert(retry is [{ Action: "publish-msi-packages" }] && retry[0].MsiPackages.Single().Kind == "pos" &&
                host.Updates!.DatabaseUploadCompleted && !host.CanRetryUpdate,
                "retry uploads only the failed package without reinstalling or recompiling anything");
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private static async Task ParallelInstallationSelectedMsiAsync(string root)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root);
        Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked = false;
        Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages").Remove(UpdateComponents.Ntc);
        Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages").Remove(UpdateComponents.Ntv);
        runner.ReleaseAll();
        await (Task)Call(form, "RunUpdatesAsync", false)!;
        Assert(runner.Requests.Where(request => request.MsiPackage is not null).Select(request => request.MsiPackage!.Kind).SequenceEqual(["pos", "oman"]) &&
            runner.Requests.All(request => request.Action is not ("publish-msi-packages" or "compile-software-packages")),
            "unselected MSIs are skipped in sequence and disabled post-installation switches remain effective");
    }

    private static async Task ParallelInstallationSkippedMsiAsync(string root, bool remote = false)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root, remote);
        Task run = (Task)Call(form, "RunUpdatesAsync", false)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 4);
            foreach (string kind in new[] { "payment", "fiscal", "serversuite" }) runner.Finish(kind);
            await WaitForInstallAsync(() => runner.Active.ContainsKey("ntc"));
            Assert(runner.Requests.All(request => request.Action != "publish-msi-packages"),
                "no zzzSoftware upload can start while the first MSI is still installing");
            runner.gates[("ntc", 1)].TrySetResult(new() { Success = true, Skipped = true, Message = "NTC not installed - skipped", NtcVersion = "Nicht installiert" });
            await WaitForInstallAsync(() => runner.Active.ContainsKey("pos"));
            Assert(runner.Requests.All(request => request.Action != "publish-msi-packages"),
                "skipping an absent NTC is not an installation and cannot release any MSI upload");
            runner.ReleaseAll();
            await run;
            Assert(runner.Requests.Where(request => request.Action == "publish-msi-packages").SelectMany(request => request.MsiPackages).Select(package => package.Kind)
                .SequenceEqual(["pos", "ntv", "oman"]), "only packages whose own MSI step was verified are published");
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private static async Task ParallelInstallationUploadPrerequisiteAsync(string root)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root);
        HostState host = Targets(form).Single();
        host.Updates = new(UpdateComponents.None);
        int discovery = 0;
        SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ =>
        {
            discovery++;
            return Task.FromResult(new[] { new IrisUploadTarget { Instance = "SYNTHETIC", Port = 1972, Namespaces = ["USER"] } });
        }));
        await (Task)Call(form, "CompleteUpdateRunAsync", new[] { host }, CancellationToken.None)!;
        Assert(discovery == 0 && runner.Requests.IsEmpty && host.Updates.DatabaseUploadError.Contains("MSI-Installation"),
            "finalization cannot upload or even discover database targets without an explicit MSI installation success");
        host.Updates.Complete(UpdateComponents.Ntc, new() { Success = true, Changed = true });
        await (Task)Call(form, "CompleteUpdateRunAsync", new[] { host }, CancellationToken.None)!;
        Assert(runner.Requests.ToArray() is [{ Action: "publish-msi-packages" }] && runner.Requests.Single().MsiPackages.Single().Kind == "ntc",
            "verifying NTC releases only NTC, not the other selected packages whose installations never ran");
    }

    private static async Task ParallelInstallationRemotePrerequisiteAsync(string root)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root);
        HostState local = Targets(form).Single();
        Guid runId = Guid.NewGuid();
        local.Updates = new(UpdateComponents.Ntv | UpdateComponents.Oman) { RunId = runId };
        local.Updates.Complete(UpdateComponents.Ntc, new() { Success = true, Skipped = true });
        local.Updates.Complete(UpdateComponents.Pos, new() { Success = true, Changed = true });
        var remote = new HostState { Host = "DATABASE.invalid", Scanned = true, HasIrisInstances = true,
            Updates = new(UpdateComponents.None) { RunId = runId } };
        await (Task)Call(form, "CompleteUpdateRunAsync", new[] { local, remote }, CancellationToken.None)!;
        Assert(runner.Requests.Count == 1 && runner.Requests.All(request => request.Action == "publish-msi-packages" && request.LocalExecution && request.MsiPackages.Single().Kind == "pos") &&
            remote.Updates!.DatabaseUploadError.Contains("SOL") && remote.CanRetryUpdate,
            "a successful local MSI installation never releases uploads for a remote target whose own MSI installation has not run");
        runner.Requests.Clear();
        local.Updates.Complete(UpdateComponents.Ntv, new() { Success = true, Changed = true });
        await (Task)Call(form, "CompleteUpdateRunAsync", new[] { remote }, CancellationToken.None)!;
        Assert(runner.Requests.IsEmpty, "remote-only finalization has no upload-only fallback even if a local MSI finished");
        remote.Updates!.Complete(UpdateComponents.Ntv, new() { Success = true });
        await (Task)Call(form, "CompleteUpdateRunAsync", new[] { remote }, CancellationToken.None)!;
        Assert(runner.Requests.ToArray() is [{ Action: "publish-msi-packages" }] && runner.Requests.Single().MsiPackages.Single().Kind == "ntv",
            "only the remote target's own completed MSI step releases that exact package for upload");
    }

    private static async Task ParallelInstallationRemoteControlFailureAsync(string root)
    {
        var runner = new InstallationRunner();
        using var form = ParallelInstallationForm(runner, root, remote: true);
        Field<CheckBox>(form, "chkCompileAfterInstallation").Checked = true;
        HostState host = Targets(form).Single();
        Task run = (Task)Call(form, "RunUpdatesAsync", false)!;
        try
        {
            await WaitForInstallAsync(() => runner.Active.Count == 4);
            foreach (string kind in new[] { "payment", "fiscal", "serversuite" }) runner.Finish(kind);
            await WaitForInstallAsync(() => runner.Active.ContainsKey("ntc"));
            runner.gates[("ntc", 1)].TrySetResult(new() { Success = false, AutomaticRetriesExhausted = true, Error = "Remote Control installer failed" });
            runner.ReleaseInstallers();
            await run;
            Assert(host.Updates!.Pending == UpdateComponents.Ntc && !host.Updates.SoftwareCompilationCompleted &&
                runner.Requests.Where(request => request.Action == "publish-msi-packages").SelectMany(request => request.MsiPackages).All(package => package.Kind != "ntc"),
                "a failed remote Control installer never releases Control upload or compilation, even after the other MSI steps complete");
            int before = runner.Requests.Count;
            await (Task)Call(form, "RunUpdatesAsync", true)!;
            WorkerRequest[] retry = runner.Requests.Skip(before).ToArray();
            Assert(retry.Where(request => request.Action == "update").Select(request => request.MsiPackage!.Kind).SequenceEqual(["ntc"]) &&
                retry.Where(request => request.Action == "publish-msi-packages").SelectMany(request => request.MsiPackages).Select(package => package.Kind).SequenceEqual(["ntc"]) &&
                host.Updates is { Pending: UpdateComponents.None, DatabaseUploadCompleted: true, SoftwareCompilationCompleted: true },
                "retry installs and verifies only remote Control, then uploads only Control and performs the deferred compilation");
        }
        finally { runner.ReleaseAll(); await run; }
    }

    private sealed class InstallationRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal ConcurrentDictionary<string, byte> Active { get; } = new();
        internal ConcurrentDictionary<string, Action<WorkerEvent>> InstallProgress { get; } = new();
        internal Dictionary<string, string> Locations { get; } = new();
        private readonly ConcurrentDictionary<string, int> counts = new();
        internal readonly ConcurrentDictionary<(string Kind, int Number), TaskCompletionSource<WorkerResult>> gates = new();
        internal ConcurrentDictionary<string, TaskCompletionSource<WorkerResult>> Uploads { get; } = new();
        internal bool HoldUploads;
        internal int ActiveUploads;
        private bool release;
        internal bool HoldCompilation;
        internal bool CompilationRunning;
        internal TaskCompletionSource<WorkerResult> Compilation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int msiActive;
        internal int MsiMaximum;
        internal int Count(string kind) => gates.Keys.Count(key => key.Kind == kind);
        internal void Finish(string kind, int number = 1, bool failure = false)
            => gates[(kind, number)].TrySetResult(failure ? new() { Success = false, Error = "Synthetic retry" } : Result(kind));
        internal void ReleaseAll()
        {
            ReleaseInstallers();
            Compilation.TrySetResult(new() { Success = true, Changed = true });
            HoldUploads = false;
            foreach (var upload in Uploads.Values) upload.TrySetResult(new() { Success = true, Changed = true });
        }
        internal void ReleaseInstallers()
        {
            release = true;
            foreach (var gate in gates) gate.Value.TrySetResult(Result(gate.Key.Kind));
        }
        private WorkerResult Result(string kind)
        {
            WorkerResult result = Snapshot(kind);
            foreach (var program in result.InstalledPrograms)
                if (Locations.TryGetValue(program.Kind, out string? location)) program.InstallLocation = location;
            return result;
        }
        internal static WorkerResult Snapshot(string kind, string version = "2.0.0") => new()
        {
            Success = true, Changed = true, HasIrisInstances = true, PosVersion = kind == "pos" ? version : "1.0.0", NtcVersion = kind == "ntc" ? version : "1.0.0",
            Services = installKinds.Take(3).Select(item => new ServiceInfo { Kind = item, Name = item, ProductVersion = item == kind ? version : "1.0.0", State = "Running", InstallDir = @"C:\Synthetic\" + item }).ToArray(),
            InstalledPrograms = installKinds.Skip(3).Select(item => new InstalledProgramInfo { Kind = item, Version = item == kind ? version : "1.0.0", InstallLocation = @"C:\Synthetic\" + item }).ToArray()
        };
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            if (request.Action == "database-inventory") return new() { Success = true, IrisInstances =
                [new() { Name = "SYNTHETIC", SuperServerPort = 1972, Namespaces = ["USER"] }] };
            if (request.Action == "publish-msi-packages" && HoldUploads)
            {
                string uploadKind = request.MsiPackages.Single().Kind;
                var upload = new TaskCompletionSource<WorkerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Uploads[uploadKind] = upload;
                Interlocked.Increment(ref ActiveUploads);
                try { return await upload.Task.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref ActiveUploads); }
            }
            if (request.Action == "compile-software-packages" && HoldCompilation)
            {
                CompilationRunning = true;
                progress(new() { Phase = "Softwarepakete kompilieren", Message = "SYNTHETIC:1972/USER · Klasse 10 von 20 · Novacom-NT-BOH.xml", ProgressPercent = 33 });
                try { return await Compilation.Task.WaitAsync(ct); }
                finally { CompilationRunning = false; }
            }
            if (request.Action != "update") return new() { Success = true, Changed = true, DatabaseUploadedKinds = ["pos", "ntc", "ntv", "oman"] };
            string kind = request.MsiPackage?.Kind ?? (request.PaymentZip.Length > 0 ? "payment" : request.FiscalZip.Length > 0 ? "fiscal" : "serversuite");
            int number = counts.AddOrUpdate(kind, 1, (_, previous) => previous + 1);
            var gate = new TaskCompletionSource<WorkerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            gates[(kind, number)] = gate;
            InstallProgress[kind] = progress;
            Active[kind] = 0;
            if (request.MsiPackage is not null)
            {
                int active = Interlocked.Increment(ref msiActive);
                MsiMaximum = Math.Max(MsiMaximum, active);
                Assert(active == 1, "MSI packages never overlap");
            }
            try
            {
                progress(new() { Phase = "Installieren", Message = kind, ProgressPercent = 40 });
                return release ? Result(kind) : await gate.Task.WaitAsync(ct);
            }
            finally { Active.TryRemove(kind, out _); if (request.MsiPackage is not null) Interlocked.Decrement(ref msiActive); }
        }
        public void Dispose() { }
    }
}
