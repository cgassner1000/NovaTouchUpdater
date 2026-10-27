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

            Complete((Task)Call(form, "RunScansAsync")!);
            WorkerRequest[] staged = worker.Requests.Where(r => r.Action == "stage-packages").ToArray();
            Assert(staged.Length == 2 && staged.Any(r => r.LocalExecution) && staged.Any(r => !r.LocalExecution) &&
                staged.All(r => r.Host != remote[1].Host && r.Host != remote[2].Host),
                "version checking stages packages only for selected reachable local and remote hosts");
            Assert(staged.All(r => r.TransferFiles.Length == 4 && r.TransferFiles.Any(f => f.RelativePath == @"cabinets\data.cab") &&
                r.TransferFiles.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4 && r.TransferFiles.All(f => f.Sha256.Length == 64)),
                "all selected ZIP/MSI payloads and external cabinets are hashed, with shared ZIPs transferred once");
            Assert(staged.All(r => r.ForceUpdate) && worker.Requests.All(r => r.Action is "probe" or "scan" or "stage-packages") &&
                remote[0].NtcVersion == "9.0.0" && remote[0].Result == "Bereit",
                "newer installed versions and Force do not prevent package staging or trigger an installation");
            Assert(remote[2].Result == "Fehler" && remote[2].Phase == "Verbindungsfehler", "package staging preserves an offline host's connection error");

            worker.Requests.Clear();
            Field<CheckBox>(form, "chkSkipZipTransfer").Checked = true;
            Complete((Task)Call(form, "RunScansAsync")!);
            staged = worker.Requests.Where(r => r.Action == "stage-packages").ToArray();
            Assert(staged is [{ LocalExecution: true, SkipZipTransfer: false }] && remote[0].Phase.Contains("deaktiviert") && remote[0].Result == "Bereit",
                "Do not transfer keeps remote hosts untouched while local package staging remains available");

            worker.Requests.Clear(); worker.Installed = false;
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.Local;
            Complete((Task)Call(form, "RunScansAsync")!);
            Assert(worker.Requests.Any(r => r.Action == "stage-packages" && r.TransferFiles.Length == 4) && Targets(form).Single().NtcVersion == "Nicht installiert",
                "packages are staged even when none of their products is installed");

            worker.Requests.Clear();
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.Remote;
            File.Delete(ntc);
            Complete((Task)Call(form, "RunScansAsync")!);
            Assert(worker.Requests.All(r => r.Action != "stage-packages") && remote[0].Result == "Bereit",
                "disabled remote transfer neither reads missing source packages nor alters scan success");

            File.WriteAllText(ntc, "Restored fixture payload");
            worker.Requests.Clear(); worker.FailScan = true;
            Field<CheckBox>(form, "chkSkipZipTransfer").Checked = false;
            Complete((Task)Call(form, "RunScansAsync")!);
            Assert(worker.Requests.Any(r => r.Action == "stage-packages" && r.Host == remote[0].Host) && !remote[0].Scanned &&
                remote[0].Result == "Fehler" && remote[0].Error.Contains("Inventory fixture failure") && remote[0].Phase.Contains("bereitgestellt"),
                "a reachable host receives packages even after an inventory error, while the original scan error stays visible");
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
        internal bool FailScan { get; set; }
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action is not ("probe" or "scan" or "stage-packages")) throw new InvalidOperationException("Staging must never run an update or command");
            if (request.Host == "PC-C.invalid") return Task.FromResult(new WorkerResult { Success=false, Error="Offline test fixture" });
            if (request.Action == "scan" && FailScan) return Task.FromResult(new WorkerResult { Success=false, Error="Inventory fixture failure" });
            return Task.FromResult(new WorkerResult { Success=true, Changed=request.Action == "stage-packages", NtcVersion=Installed ? "9.0.0" : "Nicht installiert",
                InstalledPrograms=Installed ? [new() { Kind="ntc", DisplayName="NovaTouch Control", Version="9.0.0" }] : [] });
        }
        public void Dispose() { }
    }
}
