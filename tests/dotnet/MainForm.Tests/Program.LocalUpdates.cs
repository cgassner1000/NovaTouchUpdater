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
        string msiFolder = Path.Combine(root, "MSI", "2026", "Aktuell"), zipFolder = Path.Combine(root, "ZIP", "Aktuell");
        Directory.CreateDirectory(msiFolder);
        Directory.CreateDirectory(zipFolder);
        var msiPaths = new Dictionary<string, string>();
        foreach (var pair in new Dictionary<string, string> { ["pos"] = "NovaTouch POS", ["ntc"] = "NovaTouch Control", ["ntv"] = "NovaTouch Voucher", ["oman"] = "NovaTouch Orderman SOL" })
        {
            string file = Path.Combine(msiFolder, pair.Key + ".msi");
            MsiFixture.Create(file, pair.Value, "2.0.0", pair.Key == "ntv" ? "data.cab" : null);
            msiPaths[pair.Key] = file;
            MsiMetadata metadata = MsiPackageReader.Read(file);
            Assert(metadata.ProductName == pair.Value && metadata.ProductVersion == "2.0.0.0" && metadata.MsiProductVersion == "2.0.0" && Guid.TryParse(metadata.ProductCode, out _) && metadata.Component.ToString().Equals(pair.Key, StringComparison.OrdinalIgnoreCase),
                "real Windows Installer metadata identifies " + pair.Value + " without installing it");
        }
        File.WriteAllText(Path.Combine(msiFolder, "data.cab"), "fixture only, never installed");
        foreach (string file in new[] { "nt.payment_1.0.0.zip", "nt.payment_2.0.0.zip", "nt.fiscal_2.0.0.zip", "nt.server.suite-2026.4.9792.zip" })
            using (var archive = ZipFile.Open(Path.Combine(file.Contains("1.0.0") ? root : zipFolder, file), ZipArchiveMode.Create)) archive.CreateEntry("fixture.txt");
        using (var archive = ZipFile.Open(Path.Combine(msiFolder, "arbitrary-control-name.zip"), ZipArchiveMode.Create))
            archive.CreateEntryFromFile(msiPaths["ntc"], "installer/Control.msi");
        string brokenMsi = Path.Combine(msiFolder, "broken.msi");
        File.WriteAllText(brokenMsi, "not an MSI");
        using var catalog = new SoftwarePackageCatalog();
        SoftwareCatalogResult result = await catalog.ReadAsync(root, null, CancellationToken.None);
        Assert(result.Packages.Length == 7 && result.Packages.Single(p => p.Component == UpdateComponents.Payment) is { Version: "2.0.0" } payment &&
            payment.Path == Path.Combine(zipFolder, "nt.payment_2.0.0.zip") && result.Warnings.Length == 1,
            "recursive discovery finds all seven components across multiple folder levels and selects a newer nested ZIP over the root version");
        Assert(result.Warnings[0].StartsWith(Path.GetRelativePath(root, brokenMsi) + ": ", StringComparison.OrdinalIgnoreCase),
            "an unreadable nested package is reported with its relative folder path while discovery continues");
        SoftwarePackage[] zipped = await catalog.ReadFileAsync(Path.Combine(msiFolder, "arbitrary-control-name.zip"), CancellationToken.None);
        Assert(zipped is [{ Component: UpdateComponents.Ntc, Msi.RelativeMsiPath: "installer/Control.msi" }],
            "MSI packages inside arbitrarily named ZIPs are identified by product metadata");
        Assert(result.Packages.Single(p => p.Component == UpdateComponents.Ntv).Msi!.CompanionFiles is [{ RelativePath: "data.cab" } cabinet] &&
            cabinet.LocalPath == Path.Combine(msiFolder, "data.cab"),
            "a deeply nested MSI retains its external cabinet from the same source folder");
        string fourPart = Path.Combine(msiFolder, "control-four-part.msi");
        string olderRevision = Path.Combine(msiFolder, "control-older-revision.msi");
        MsiFixture.Create(fourPart, "NovaTouch Control", "2026.4.9800.12345", null);
        MsiFixture.Create(olderRevision, "NovaTouch Control", "2026.4.9800.12344", null);
        File.SetLastWriteTimeUtc(fourPart, DateTime.UtcNow.AddHours(-1));
        SoftwarePackage[] fourPartPackages = await catalog.ReadFileAsync(fourPart, CancellationToken.None);
        Assert(fourPartPackages is [{ Version: "2026.4.9800.12345", Msi.ProductVersion: "2026.4.9800.12345" }],
            "MSI metadata and the selected package retain all four version fields");
        result = await catalog.ReadAsync(root, null, CancellationToken.None);
        Assert(result.Packages.Single(p => p.Component == UpdateComponents.Ntc) is { Version: "2026.4.9800.12345" } newest && newest.Path == fourPart,
            "recursive discovery chooses the higher MSI revision even when the older revision has a newer file timestamp");
        msiPaths["ntcFourPart"] = fourPart;
        foreach (var product in new[] {
            (Kind: "ntc", Name: "NovaTouch Control", File: "Nov.WaWi.exe"),
            (Kind: "pos", Name: "NovaTouch POS", File: "Nov.NT.Communication.Devices.dll"),
            (Kind: "ntv", Name: "NovaTouch Voucher", File: "Nov.Gutscheinverwaltung.exe"),
            (Kind: "oman", Name: "NovaTouch Orderman SOL", File: "Nov.Oman.SOL.Config.exe")
        })
        {
            string releaseMsi = Path.Combine(msiFolder, product.Kind + "-release.msi");
            MsiFixture.Create(releaseMsi, product.Name, "2026.4.9800", null, product.File, "2026.4.9800.12345");
            var releasePackages = await catalog.ReadFileAsync(releaseMsi, CancellationToken.None);
            Assert(releasePackages is [{ Version: "2026.4.9800.12345", Msi.ProductVersion: "2026.4.9800.12345", Msi.MsiProductVersion: "2026.4.9800" }],
                product.Kind + " reads the complete application release from the MSI File table while preserving its three-part installer property");
            msiPaths[product.Kind + "Release"] = releaseMsi;
        }
        string releaseZip = Path.Combine(zipFolder, "control-release.zip");
        using (var archive = ZipFile.Open(releaseZip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(msiPaths["ntcRelease"], "installer/setup.msi");
        Assert(await catalog.ReadFileAsync(releaseZip, CancellationToken.None) is [{ Version: "2026.4.9800.12345", Msi.MsiProductVersion: "2026.4.9800" }],
            "the application file version survives MSI extraction from an arbitrarily named ZIP and cache filename");
        string namedRelease = Path.Combine(msiFolder, "novatouch.control-2026.4.9800.12345-beta.msi");
        MsiFixture.Create(namedRelease, "NovaTouch Control", "2026.4.9800", null);
        Assert(await catalog.ReadFileAsync(namedRelease, CancellationToken.None) is [{ Version: "2026.4.9800.12345" }],
            "the original MSI filename supplies the revision when no application File table entry is available");
        string namedZip = Path.Combine(zipFolder, "novatouch.control-2026.4.9800.12345-beta.zip");
        using (var archive = ZipFile.Open(namedZip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(namedRelease, "setup.msi");
        Assert(await catalog.ReadFileAsync(namedZip, CancellationToken.None) is [{ Version: "2026.4.9800.12345" }],
            "a generic MSI entry can use the original ZIP filename instead of the hashed cache filename");
        string misleadingName = Path.Combine(msiFolder, "control-2026.4.9800.99999.msi");
        File.Copy(msiPaths["ntcRelease"], misleadingName);
        Assert(await catalog.ReadFileAsync(misleadingName, CancellationToken.None) is [{ Version: "2026.4.9800.12345" }],
            "the application version inside the MSI takes precedence over a renamed package");
        string wrongRelease = Path.Combine(msiFolder, "control-2027.1.1.99999.msi");
        File.Copy(namedRelease, wrongRelease);
        Assert(await catalog.ReadFileAsync(wrongRelease, CancellationToken.None) is [{ Version: "2026.4.9800.0" }],
            "a filename from a different release cannot replace the MSI product version");
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

    private static void PackageSelectionTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "package-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (string name in new[] { "nt.payment_2.0.0.zip", "nt.fiscal_2.0.0.zip", "nt.server.suite-2026.4.9792.zip" })
            File.WriteAllText(Path.Combine(root, name), "fixture only");
        string nested = Path.Combine(root, "MSI", "NTC");
        Directory.CreateDirectory(nested);
        foreach (var product in new[] {
            (Kind: "control", Name: "NovaTouch Control", File: "Nov.WaWi.exe"),
            (Kind: "pos", Name: "NovaTouch POS", File: "Nov.NT.Communication.Devices.dll"),
            (Kind: "voucher", Name: "NovaTouch Voucher", File: "Nov.Gutscheinverwaltung.exe"),
            (Kind: "sol", Name: "NovaTouch Orderman SOL", File: "Nov.Oman.SOL.Config.exe")
        })
            MsiFixture.Create(Path.Combine(nested, product.Kind + ".msi"), product.Name, "2026.4.9800", null, product.File, "2026.4.9800.12345");
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        Populate(form);
        Field<TextBox>(form, "softwarePath").Text = root;
        Complete((Task)Call(form, "LoadSoftwarePackagesAsync")!);
        var list = Field<Label>(form, "selectedPackagesList");
        var folderButton = Field<Button>(form, "useFolderPackages");
        UpdateComponents folderComponents = UpdateComponents.All;
        var msiPackages = Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages");
        var originalMsiPackages = msiPackages.ToDictionary();
        string originalFiscal = Field<string>(form, "fiscalZip"), originalSuite = Field<string>(form, "serverSuiteZip");
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == folderComponents && list.Text.Split(Environment.NewLine).Length == 7,
            "folder discovery selects and lists all seven components, including nested MSIs");
        void AssertFullVersionDisplayed(string selection, string version = "2026.4.9800.12345")
        {
            var controls = Field<Dictionary<UpdateComponents, (Button Button, Label Label)>>(form, "msiControls");
            var table = Field<TableLayoutPanel>(form, "selectedPackagesTable");
            Assert(controls[UpdateComponents.Ntc].Label.Text == "NTC Target: " + version &&
                table.Controls.OfType<Label>().Any(label => label.Text == version) && list.Text.Contains(version),
                selection + " displays all four fields in both selected packages and the individual update-file label");
        }
        AssertFullVersionDisplayed("folder selection");
        string individual = Path.Combine(root, "nt.payment_1.0.0.zip");
        File.WriteAllText(individual, "fixture only");
        Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.Payment, individual, "1.0.0"));
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == folderComponents && list.Text.Split(Environment.NewLine).Length == 7 &&
            Field<string>(form, "paymentZip") == individual && Field<string>(form, "fiscalZip") == originalFiscal && Field<string>(form, "serverSuiteZip") == originalSuite &&
            originalMsiPackages.All(pair => ReferenceEquals(msiPackages[pair.Key], pair.Value)) && folderButton.Enabled,
            "replacing Payment individually preserves Fiscal, Server Suite and all four MSI packages in the displayed selection");
        Complete((Task)Call(form, "LoadSoftwarePackagesAsync")!);
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == folderComponents && Field<string>(form, "paymentZip") == individual &&
            originalMsiPackages.All(pair => ReferenceEquals(msiPackages[pair.Key], pair.Value)),
            "rereading the folder preserves the individually replaced package and every other selected package");
        Field<ComboBox>(form, "executionMode").SelectedIndex = 0;
        var request = (WorkerRequest)Call(form, "Request", "update", Targets(form)[0], (UpdateComponents)Call(form, "SelectedUpdateComponents")!)!;
        Assert(request.PaymentZip == individual && request.FiscalZip == originalFiscal && request.ServerSuiteZip == originalSuite && request.MsiPackages.Length == 4,
            "the actual local update request combines the individually replaced package with every retained folder package");
        string individualFiscal = Path.Combine(root, "nt.fiscal_1.0.0.zip");
        File.WriteAllText(individualFiscal, "fixture only");
        Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.Fiscal, individualFiscal, "1.0.0"));
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == folderComponents && Field<string>(form, "paymentZip") == individual &&
            Field<string>(form, "fiscalZip") == individualFiscal && Field<string>(form, "serverSuiteZip") == originalSuite && msiPackages.Count == 4,
            "a second individual replacement changes only Fiscal and retains the first replacement and the other products");
        typeof(Button).GetMethod("OnClick", Private)!.Invoke(folderButton, [EventArgs.Empty]);
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == folderComponents && list.Text.Split(Environment.NewLine).Length == 7 && !folderButton.Enabled &&
            Field<string>(form, "paymentZip") == Path.Combine(root, "nt.payment_2.0.0.zip") && Field<string>(form, "fiscalZip") == originalFiscal,
            "the user can explicitly restore all folder packages and discard individual replacements");
        string individualMsi = Path.Combine(root, "control-manual.msi");
        MsiFixture.Create(individualMsi, "NovaTouch Control", "2026.4.9800", null, "Nov.WaWi.exe", "2026.4.9800.12346");
        Task<SoftwarePackage[]> read = Field<SoftwarePackageCatalog>(form, "packageCatalog").ReadFileAsync(individualMsi, CancellationToken.None);
        Complete(read);
        SoftwarePackage replacement = read.GetAwaiter().GetResult().Single();
        Call(form, "SelectIndividualPackage", replacement);
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == folderComponents && list.Text.Contains("control-manual.msi") &&
            msiPackages[UpdateComponents.Ntc].PayloadPath == individualMsi && Field<string>(form, "fiscalZip") == originalFiscal &&
            originalMsiPackages.Where(pair => pair.Key != UpdateComponents.Ntc).All(pair => msiPackages[pair.Key].PayloadPath == pair.Value.PayloadPath),
            "replacing a folder MSI changes only that product while preserving all service packages and the other three MSIs");
        AssertFullVersionDisplayed("individual MSI replacement", "2026.4.9800.12346");
        using var individualOnly = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        Call(individualOnly, "SelectIndividualPackage", replacement);
        Assert((UpdateComponents)Call(individualOnly, "SelectedUpdateComponents")! == UpdateComponents.Ntc,
            "without a loaded folder an individual choice selects only its own product");
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
        string targetPath = Path.Combine(TestWorkspace.Artifacts, "Local update destination");
        Field<TextBox>(form, "txtRemotePath").Text = " " + targetPath + " ";
        Field<TextBox>(form, "softwarePath").Text = @" D:\Software Quellen ";
        Assert(Field<TextBox>(form, "txtRemotePath").Enabled, "the update-file destination remains editable in local-only mode");

        string configPath = Path.Combine(TestWorkspace.Artifacts, "local-paths-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var captured = (UpdaterConfig)Call(form, "CaptureConfig")!;
            AppConfigStore.Save(configPath, captured, ""); // Synthetic settings and no credentials; never use the personal config.
            var loaded = AppConfigStore.Load(configPath);
            using var restored = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            Call(restored, "ApplyLoadedConfig", loaded);
            Call(restored, "CaptureCredentials");
            HostState restoredLocal = Targets(restored).Single();
            var restoredRequest = (WorkerRequest)Call(restored, "Request", "update", restoredLocal, UpdateComponents.Payment)!;
            Assert(loaded.Settings.SoftwarePath == @"D:\Software Quellen" && loaded.Settings.RemotePath == targetPath &&
                Field<TextBox>(restored, "softwarePath").Text == @"D:\Software Quellen" && restoredRequest.RemoteInstallPath == targetPath && restoredRequest.LocalExecution,
                "source directory, update destination and Local mode persist through the actual form capture, config file and startup apply path");
            Assert(loaded.Settings.Hosts!.Length == 3 && !loaded.Settings.Hosts.Contains("LOCALHOST"), "saving local settings retains remote hosts and recreates LOCALHOST from the execution mode");
        }
        finally { File.Delete(configPath); }
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
        Assert(request.RemoteInstallPath == targetPath, "local MSI packages use the configured destination instead of a hidden TEMP workspace");
        var zipRequest = (WorkerRequest)Call(form, "Request", "update", local, UpdateComponents.Payment)!;
        Assert(zipRequest.LocalExecution && zipRequest.RemoteInstallPath == targetPath, "local ZIP updates use the same configured destination as MSI packages");
        Assert(local.Phase.Contains("Neustart erforderlich") && local.Result == "Erfolgreich", "MSI success includes an explicit required-reboot notice");
        mode.SelectedIndex = 1;
        Assert(Targets(form).Length == 4 && Targets(form).Count(h => h.IsLocal) == 1, "combined execution includes LOCALHOST and selected remote computers once");
        var remoteRequest = (WorkerRequest)Call(form, "Request", "update", Targets(form).First(h => !h.IsLocal), UpdateComponents.Payment)!;
        Assert(!remoteRequest.LocalExecution && remoteRequest.RemoteInstallPath == targetPath,
            "combined mode uses the configured path on each respective local or remote machine");
        mode.SelectedIndex = 2;
        Assert(Targets(form).Length == 3 && Targets(form).All(h => !h.IsLocal), "remote-only execution excludes LOCALHOST from all remote requests");
    }

    private static void RemotePackageRestrictionTests()
    {
        var worker = new StagingRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        HostState remote = Populate(form)[0];
        Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.LocalAndRemote;
        HostState local = Targets(form).Single(h => h.IsLocal);
        local.Scanned = true;
        foreach (string kind in new[] { "payment", "fiscal", "serverSuite" })
        {
            SetField(form, kind + "Zip", kind + ".zip");
            SetField(form, kind + "Version", "2.0.0");
        }
        var packages = Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages");
        foreach (UpdateComponents component in HostUpdateState.Each(UpdateComponents.Msi))
        {
            packages[component] = new() { Kind = component.ToString().ToLowerInvariant(), ProductVersion = "2.0.0", PayloadPath = component + ".msi" };
            var localRequest = (WorkerRequest)Call(form, "Request", "update", local, component)!;
            var remoteRequest = (WorkerRequest)Call(form, "Request", "update", remote, component)!;
            Assert(localRequest.MsiPackage == packages[component] && remoteRequest.MsiPackage is null && remoteRequest.MsiPackages.Length == 0,
                component + " is permitted locally and stripped from remote update requests");
        }
        Assert((UpdateComponents)Call(form, "ComponentsForUpdate", remote, false)! == UpdateComponents.ZipServices &&
            (UpdateComponents)Call(form, "ComponentsForUpdate", local, false)! == UpdateComponents.All,
            "combined execution plans all seven components locally and only the three service ZIPs remotely");
        var preflight = (WorkerRequest)Call(form, "Request", "check-upload-directory", remote, UpdateComponents.All)!;
        Assert(preflight.MsiPackage is null && preflight.MsiPackages.Length == 0 && preflight.PaymentZip.Length > 0 && preflight.FiscalZip.Length > 0 && preflight.ServerSuiteZip.Length > 0,
            "remote upload preparation excludes MSIs and retains all allowed service packages");
        string confirmation = (string)Call(form, "UpdateConfirmation", new[] { local, remote }, UpdateComponents.All, false)!;
        Assert(confirmation.Contains("NovaTouch Control 2.0.0: 1 Host(s)") && confirmation.Contains("nt.payment 2.0.0: 2 Host(s)") && confirmation.Contains("MSIs nur LOKAL"),
            "the confirmation counts MSI targets locally and clearly states the remote restriction");
        remote.Updates = new(UpdateComponents.All);
        Update(form, remote);
        Assert(worker.Requests.Count == 3 && worker.Requests.All(r => !r.LocalExecution && r.MsiPackage is null && r.MsiPackages.Length == 0) && remote.Updates.Pending == UpdateComponents.None,
            "even an existing mixed remote update state executes only Payment, Fiscal and Server Suite");
        worker.Requests.Clear();
        remote.Updates = new(UpdateComponents.Msi);
        remote.Updates.BeginComponent(UpdateComponents.Ntc);
        remote.Updates.Fail(UpdateComponents.Ntc, "Old remote MSI failure");
        remote.UpdateFailed = true;
        Assert((UpdateComponents)Call(form, "ComponentsForUpdate", remote, true)! == UpdateComponents.None,
            "a stale MSI-only remote retry requires no MSI package source");
        Update(form, remote);
        Assert(worker.Requests.IsEmpty && remote.Updates.Pending == UpdateComponents.None && remote.Updates.ErrorText == "" && !remote.CanRetryUpdate,
            "stale remote MSI failures are removed without probe, upload, installation or repeated attempts");
        local.Updates = new(UpdateComponents.Msi);
        Update(form, local);
        Assert(worker.Requests.Count == 4 && worker.Requests.All(r => r.LocalExecution && r.MsiPackage is not null),
            "all four MSI products remain available for local updates");
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
        internal static void Create(string path, string name, string version, string? cabinet, string? primaryFile = null, string? fileVersion = null)
        {
            Check(Open(path, new IntPtr(3), out uint database));
            try
            {
                Sql(database, "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)");
                Sql(database, "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `Cabinet` CHAR(255) PRIMARY KEY `DiskId`)");
                foreach (var pair in new Dictionary<string, string> { ["ProductName"] = name, ["ProductVersion"] = version, ["ProductCode"] = Guid.NewGuid().ToString("B"), ["UpgradeCode"] = Guid.NewGuid().ToString("B") })
                    Sql(database, $"INSERT INTO `Property` (`Property`, `Value`) VALUES ('{pair.Key}', '{pair.Value.Replace("'", "''")}')");
                if (cabinet is not null) Sql(database, $"INSERT INTO `Media` (`DiskId`, `Cabinet`) VALUES (1, '{cabinet}')");
                if (primaryFile is not null)
                {
                    Sql(database, "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `Version` CHAR(72) PRIMARY KEY `File`)");
                    Sql(database, $"INSERT INTO `File` (`File`, `FileName`, `Version`) VALUES ('Application', 'APP~1.EXE|{primaryFile}', '{fileVersion}')");
                    Sql(database, "INSERT INTO `File` (`File`, `FileName`, `Version`) VALUES ('Unrelated', 'Other.dll', '9999.1.1.99999')");
                }
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
