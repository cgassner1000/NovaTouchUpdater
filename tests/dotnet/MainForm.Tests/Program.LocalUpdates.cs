using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using NovaTouchUpdater;

internal static partial class Program
{
    private static async Task PackageCatalogTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "local-msi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var msiPaths = new Dictionary<string, string>();
        foreach (var pair in new Dictionary<string, string> { ["pos"] = "NovaTouch POS", ["ntc"] = "NovaTouch Control", ["ntv"] = "NovaTouch Voucher", ["oman"] = "NovaTouch Orderman SOL" })
        {
            string file = Path.Combine(root, pair.Key + ".msi");
            MsiFixture.Create(file, pair.Value, "2.0.0", pair.Key == "ntv" ? "data.cab" : null);
            msiPaths[pair.Key] = file;
            MsiMetadata metadata = MsiPackageReader.Read(file);
            Assert(metadata.ProductName == pair.Value && metadata.ProductVersion == "2.0.0" && Guid.TryParse(metadata.ProductCode, out _) && metadata.Component.ToString().Equals(pair.Key, StringComparison.OrdinalIgnoreCase),
                "real Windows Installer metadata identifies " + pair.Value + " without installing it");
        }
        File.WriteAllText(Path.Combine(root, "data.cab"), "fixture only, never installed");
        foreach (string file in new[] { "nt.payment_1.0.0.zip", "nt.payment_2.0.0.zip", "nt.fiscal_2.0.0.zip", "nt.server.suite-2026.4.9792.zip" })
            using (var archive = ZipFile.Open(Path.Combine(root, file), ZipArchiveMode.Create)) archive.CreateEntry("fixture.txt");
        using (var archive = ZipFile.Open(Path.Combine(root, "arbitrary-control-name.zip"), ZipArchiveMode.Create))
            archive.CreateEntryFromFile(msiPaths["ntc"], "installer/Control.msi");
        File.WriteAllText(Path.Combine(root, "broken.msi"), "not an MSI");
        using var catalog = new SoftwarePackageCatalog();
        SoftwareCatalogResult result = await catalog.ReadAsync(root, null, CancellationToken.None);
        Assert(result.Packages.Length == 7 && result.Packages.Single(p => p.Component == UpdateComponents.Payment).Version == "2.0.0" && result.Warnings.Length == 1,
            "a software directory selects all seven supported components and the latest service ZIP");
        SoftwarePackage[] zipped = await catalog.ReadFileAsync(Path.Combine(root, "arbitrary-control-name.zip"), CancellationToken.None);
        Assert(zipped is [{ Component: UpdateComponents.Ntc, Msi.RelativeMsiPath: "installer/Control.msi" }],
            "MSI packages inside arbitrarily named ZIPs are identified by product metadata");
        Assert(result.Packages.Single(p => p.Component == UpdateComponents.Ntv).Msi!.CompanionFiles is [{ RelativePath: "data.cab" }],
            "external MSI cabinet files are retained as verified companion files");
        using (var archive = ZipFile.Open(Path.Combine(root, "bad-path.zip"), ZipArchiveMode.Create)) archive.CreateEntryFromFile(msiPaths["ntc"], "../Control.msi");
        bool rejected = false;
        try { await catalog.ReadFileAsync(Path.Combine(root, "bad-path.zip"), CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "MSI archive paths cannot escape the staging directory");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { await catalog.ReadAsync(root, null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "software-directory discovery honors cancellation without running an installer");
        File.WriteAllText(Path.Combine(TestWorkspace.Artifacts, "local-msi-fixtures.json"), JsonSerializer.Serialize(msiPaths));
    }

    private static void LocalExecutionTests()
    {
        var worker = new LocalRunner();
        var monitor = new FakeRunner();
        using var form = new MainForm(preview: true, worker, monitor);
        Populate(form);
        var mode = Field<ComboBox>(form, "executionMode");
        Assert(mode.Items.Cast<string>().SequenceEqual(new[] { "Lokal", "Lokal & Remote", "Remote" }) && mode.SelectedIndex == 2,
            "execution offers exactly Local, Local & Remote, and Remote with the existing remote default");
        mode.SelectedIndex = 0;
        HostState local = Targets(form).Single();
        Assert(local.IsLocal && local.Host == "LOCALHOST" && Field<DataGridView>(form, "table").Rows.Count == 1,
            "local-only execution automatically adds and selects LOCALHOST while hiding remote rows");
        Field<TextBox>(form, "txtUser").Text = "unused-domain-user";
        Field<TextBox>(form, "txtPassword").Text = "";
        Assert((bool)Call(form, "ValidateCredentials")! && !Field<Button>(form, "btnWinRm").Enabled && !Field<TextBox>(form, "txtUser").Enabled,
            "local-only work requires no WinRM credentials or WinRM controls");
        Complete((Task)Call(form, "RunScansAsync")!);
        Assert(worker.Requests.Count == 2 && worker.Requests.All(r => r.LocalExecution && r.Host == "LOCALHOST" && r.Username.Length == 0 && r.Password.Length == 0) && monitor.Requests.IsEmpty,
            "local scans and version reads run directly without any WinRM monitor request");
        Assert(local.NtcVersion == "1.0.0" && local.PosVersion == "1.0.0" && local.Scanned && local.Connection == "Lokal",
            "local software versions, including NTC, populate the local table row");
        var package = new MsiPackageRequest { Kind = "ntc", ProductName = "NovaTouch Control", ProductVersion = "2.0.0", PayloadPath = "control.msi", ProductCode = Guid.NewGuid().ToString("B") };
        Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages")[UpdateComponents.Ntc] = package;
        local.Updates = new(UpdateComponents.Ntc);
        Update(form, local);
        WorkerRequest request = worker.Requests.Single(r => r.Action == "update");
        Assert(request.LocalExecution && request.MsiPackage?.Kind == "ntc" && !request.SkipZipTransfer && string.IsNullOrEmpty(request.PaymentZip) && local.NtcVersion == "2.0.0" && local.RebootRequired,
            "a local MSI update uses the direct path, changes only its component, and retains a required-reboot result");
        Assert(local.Phase.Contains("Neustart erforderlich") && local.Result == "Erfolgreich", "MSI success includes an explicit required-reboot notice");
        mode.SelectedIndex = 1;
        Assert(Targets(form).Length == 4 && Targets(form).Count(h => h.IsLocal) == 1, "combined execution includes LOCALHOST and selected remote computers once");
        mode.SelectedIndex = 2;
        Assert(Targets(form).Length == 3 && Targets(form).All(h => !h.IsLocal), "remote-only execution excludes LOCALHOST from all remote requests");
    }

    private sealed class LocalRunner : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        private string ntc = "1.0.0";
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Add(request);
            if (!request.LocalExecution) throw new InvalidOperationException("Local test must never contact a remote computer");
            if (request.Action == "update") ntc = request.MsiPackage!.ProductVersion;
            return Task.FromResult(new WorkerResult
            {
                Success = true, Changed = request.Action == "update", NtcVersion = ntc, PosVersion = "1.0.0", RebootRequired = request.Action == "update",
                InstalledPrograms = [new() { Kind = "ntc", DisplayName = "NovaTouch Control", Version = ntc }]
            });
        }
        public void Dispose() { }
    }

    private static class MsiFixture
    {
        internal static void Create(string path, string name, string version, string? cabinet)
        {
            Check(Open(path, new IntPtr(3), out uint database));
            try
            {
                Sql(database, "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)");
                Sql(database, "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `Cabinet` CHAR(255) PRIMARY KEY `DiskId`)");
                foreach (var pair in new Dictionary<string, string> { ["ProductName"] = name, ["ProductVersion"] = version, ["ProductCode"] = Guid.NewGuid().ToString("B"), ["UpgradeCode"] = Guid.NewGuid().ToString("B") })
                    Sql(database, $"INSERT INTO `Property` (`Property`, `Value`) VALUES ('{pair.Key}', '{pair.Value.Replace("'", "''")}')");
                if (cabinet is not null) Sql(database, $"INSERT INTO `Media` (`DiskId`, `Cabinet`) VALUES (1, '{cabinet}')");
                Check(Commit(database));
            }
            finally { Close(database); }
        }
        private static void Sql(uint database, string sql)
        {
            Check(View(database, sql, out uint view));
            try { Check(Execute(view, 0)); } finally { Close(view); }
        }
        private static void Check(uint error) { if (error != 0) throw new InvalidOperationException("MSI fixture error: " + error); }
        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")] private static extern uint Open(string path, IntPtr persist, out uint database);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")] private static extern uint View(uint database, string sql, out uint view);
        [DllImport("msi.dll", EntryPoint = "MsiViewExecute")] private static extern uint Execute(uint view, uint record);
        [DllImport("msi.dll", EntryPoint = "MsiDatabaseCommit")] private static extern uint Commit(uint database);
        [DllImport("msi.dll", EntryPoint = "MsiCloseHandle")] private static extern uint Close(uint handle);
    }
}
