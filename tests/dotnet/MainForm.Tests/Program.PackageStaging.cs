using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void PackageStagingTests()
    {
        string root = Path.GetFullPath(Path.Combine(TestWorkspace.Artifacts, "package-staging-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "staging fixtures remain inside the test workspace");
        Directory.CreateDirectory(root);
        try
        {
            string payment = Path.Combine(root, "nt.payment_1.0.0.zip"), ntc = Path.Combine(root, "Control.msi"), bundle = Path.Combine(root, "PosVoucher.zip"), cab = Path.Combine(root, "data.cab");
            foreach (string file in new[] { payment, ntc, bundle, cab }) File.WriteAllText(file, "Test data, never installed: " + Path.GetFileName(file));
            var worker = new StagingRunner();
            using var form = new MainForm(preview: true, worker, new FakeRunner());
            HostState[] remote = Populate(form);
            remote[1].IsSelected = false;
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
            Field<TextBox>(form, "txtRemotePath").Text = Path.Combine(root, "target");
            Field<CheckBox>(form, "chkForceUpdate").Checked = true;
            SetField(form, "paymentZip", payment); SetField(form, "paymentVersion", "1.0.0");
            var packages = Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages");
            packages.Add(UpdateComponents.Ntc, new() { Kind="ntc", ProductName="NovaTouch Control", ProductVersion="1.0.0", PayloadPath=ntc,
                CompanionFiles=[new() { LocalPath=cab, RelativePath="cabinets/data.cab" }] });
            packages.Add(UpdateComponents.Pos, new() { Kind="pos", ProductName="NovaTouch POS", ProductVersion="1.0.0", PayloadPath=bundle });
            packages.Add(UpdateComponents.Ntv, new() { Kind="ntv", ProductName="NovaTouch Voucher", ProductVersion="1.0.0", PayloadPath=bundle });
            var components = UpdateComponents.Payment | UpdateComponents.Ntc | UpdateComponents.Pos | UpdateComponents.Ntv;

            Complete((Task)Call(form, "RunScansAsync")!);
            Assert(worker.Requests.All(r => r.Action is "probe" or "scan") && worker.Requests.All(r => r.Host != remote[1].Host),
                "Check + Versions never stages, uploads or installs packages on local or remote hosts");
            Assert(remote[2].Result == "Fehler" && remote[2].Phase == "Verbindungsfehler", "checking retains an offline host's connection error");
            File.Delete(ntc);
            worker.Requests.Clear();
            Complete((Task)Call(form, "RunScansAsync")!);
            Assert(worker.Requests.All(r => r.Action is "probe" or "scan") && Targets(form).First(h => h.IsLocal).Result == "Bereit",
                "checking remains read-only even if a selected package source is missing and Force is enabled");
            File.WriteAllText(ntc, "Restored fixture payload");

            HostState local = Targets(form).First(h => h.IsLocal);
            local.Updates = new(components);
            worker.Requests.Clear();
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
            WorkerRequest staged = worker.Requests.Single(r => r.Action == "stage-packages");
            Assert(staged.LocalExecution && worker.Requests.First().Action == "stage-packages" && worker.Requests.Count(r => r.Action == "update") == 4,
                "the confirmed update execution stages all chosen LOCALHOST packages before individual component updates");
            Assert(worker.Requests.All(r => r.Action != "publish-msi-packages") &&
                !((string)Call(form, "UpdateConfirmation", (object)new[] { local }, components, false)!).Contains("zzzSoftware"),
                "local updates and their confirmation contain no database upload or database prerequisite");
            Assert(staged.TransferFiles.Length == 4 && staged.TransferFiles.Any(f => f.RelativePath == @"cabinets\data.cab") &&
                staged.TransferFiles.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4 && staged.TransferFiles.All(f => f.Sha256.Length == 64),
                "local staging includes ZIPs, MSI payloads and companion cabinets while deduplicating a shared ZIP");
            Assert(staged.ForceUpdate && local.NtcVersion == "9.0.0", "local package staging is independent of already newer installed versions");

            Field<CheckBox>(form, "chkSkipZipTransfer").Checked = true;
            Call(form, "CaptureCredentials");
            worker.Requests.Clear(); worker.Installed = false;
            local.Updates = new(components);
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
            Assert(worker.Requests.Single(r => r.Action == "stage-packages") is { LocalExecution: true, SkipZipTransfer: false } &&
                local.Result == "Übersprungen" && !local.Phase.Contains("zzzSoftware"),
                "absent products are skipped without a database upload and the remote transfer option stays remote-only");

            remote[0].Updates = new(components);
            worker.Requests.Clear();
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", remote[0], CancellationToken.None, NoRetryDelay)!);
            Assert(worker.Requests.All(r => r.Action == "update" && !r.LocalExecution && r.SkipZipTransfer && r.ForceUpdate),
                "remote updates perform no bulk staging and keep component-specific requests, Do not transfer and Force unchanged");

            Field<CheckBox>(form, "chkSkipLocalPackageCopy").Checked = true;
            Call(form, "CaptureCredentials");
            worker.Requests.Clear(); worker.Installed = true; worker.FailStaging = true;
            local.Updates = new(components);
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
            Assert(worker.Requests.Count == 4 && worker.Requests.All(r => r.Action == "update" && r.LocalExecution && r.SkipLocalPackageCopy && !r.SkipZipTransfer) &&
                worker.Requests.Any(r => r.PaymentZip == payment) && worker.Requests.Any(r => r.MsiPackage?.PayloadPath == ntc),
                "direct local updates skip bulk copying and retain the original ZIP and MSI sources");
            Field<CheckBox>(form, "chkSkipLocalPackageCopy").Checked = false;
            Field<TextBox>(form, "txtRemotePath").Clear();
            Call(form, "CaptureCredentials");
            worker.Requests.Clear(); local.Updates = new(components);
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
            Assert(worker.Requests.Count == 4 && worker.Requests.All(r => r.Action == "update" && r.SkipLocalPackageCopy && r.RemoteInstallPath == ""),
                "an empty local destination also skips all bulk copying");
            Field<TextBox>(form, "txtRemotePath").Text = Path.Combine(root, "target");
            Call(form, "CaptureCredentials");

            worker.Requests.Clear(); worker.FailStaging = false; local.Scanned = false; local.Updates = new(components);
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
            Assert(worker.Requests.First().Action == "stage-packages", "local copying still happens before installation inventory is refreshed");
            Assert(worker.Requests.Count(r => r.Action == "scan") == 1 && worker.Requests.All(r => r.Action != "probe"),
                "an unscanned local host skips the redundant PowerShell connectivity probe");
            worker.Requests.Clear(); worker.FailStaging = true;
            local.Updates = new(components);
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
            Assert(worker.Requests.Select(r => r.Action).SequenceEqual(["stage-packages"]) && local.Result == "Fehler" && local.UpdateFailed && local.Updates.Pending == components,
                "a failed local package copy prevents installation and retains unfinished components for retry");

            worker.Requests.Clear(); worker.FailStaging = false;
            foreach (UpdateComponents completed in new[] { UpdateComponents.Payment, UpdateComponents.Pos, UpdateComponents.Ntv })
                local.Updates.Complete(completed, new WorkerResult { Success=true });
            File.Delete(payment); File.Delete(bundle);
            Complete((Task)Call(form, "ExecuteHostUpdateAsync", local, CancellationToken.None, NoRetryDelay)!);
            Assert(worker.Requests.Single(r => r.Action == "stage-packages").TransferFiles.Length == 2 &&
                worker.Requests.Single(r => r.Action == "update").MsiPackage?.Kind == "ntc" && worker.Requests.All(r => r.Action != "publish-msi-packages"),
                "local retries stage and update only the pending component without needing completed package sources");
        }
        finally
        {
            Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "staging fixture cleanup stays scoped to test artifacts");
            Directory.Delete(root, true);
        }
    }

    private sealed class StagingRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal bool Installed { get; set; } = true;
        internal bool FailStaging { get; set; }
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action is not ("probe" or "scan" or "stage-packages" or "update")) throw new InvalidOperationException("Unexpected request in update fixture");
            if (request.Action == "stage-packages" && !request.LocalExecution) throw new InvalidOperationException("Bulk staging must never target a remote host");
            if (request.Host == "PC-C.invalid") return Task.FromResult(new WorkerResult { Success=false, Error="Offline test fixture" });
            if (request.Action == "stage-packages" && FailStaging) return Task.FromResult(new WorkerResult { Success=false, Error="Copy fixture failure" });
            return Task.FromResult(new WorkerResult { Success=true, Changed=request.Action == "stage-packages", Skipped=request.Action == "update" && !Installed,
                NtcVersion=Installed ? "9.0.0" : "Nicht installiert",
                InstalledPrograms=Installed ? [new() { Kind="ntc", DisplayName="NovaTouch Control", Version="9.0.0" }] : [] });
        }
        public void Dispose() { }
    }
}
