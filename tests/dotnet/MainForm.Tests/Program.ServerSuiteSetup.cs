using System.IO.Compression;
using System.Text.Json;
using NovaTouchUpdater;

internal static partial class Program
{
    private static async Task ServerSuiteSetupTests()
    {
        string manifest = Path.Combine(TestWorkspace.Artifacts, "suite-setup-fixture.json");
        if (!File.Exists(manifest)) throw new InvalidOperationException("Run tests/powershell/ServerSuiteSetup.Tests.ps1 first to compile the fixture, without executing it.");
        string source = JsonSerializer.Deserialize<string>(File.ReadAllText(manifest))!;
        Assert(ServerSuiteInstaller.ReadVersion(source) == "2026.4.9999", "Inno metadata identifies supported setup and target version");
        string v2Setup = Path.Combine(Path.GetDirectoryName(source)!, "compat-v2.exe");
        string markerlessSetup = Path.Combine(Path.GetDirectoryName(source)!, "legacy-no-marker.exe");
        Assert(ServerSuiteInstaller.ReadVersion(v2Setup) == "2026.4.9999",
            "existing v1 and v2 Server Suite setups are accepted without requiring installer changes");
        string root = Path.Combine(TestWorkspace.Artifacts, "setup-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Unterordner"));
        string exe = Path.Combine(root, "Unterordner", Path.GetFileName(source));
        File.Copy(source, exe);
        string zip = Path.Combine(root, "nt.server.suite-2026.4.9999.zip");
        using (ZipFile.Open(zip, ZipArchiveMode.Create)) { }
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddHours(1));
        using var catalog = new SoftwarePackageCatalog();
        var result = await catalog.ReadAsync(root, null, CancellationToken.None);
        Assert(result.Packages is [{ Component: UpdateComponents.ServerSuite }] && result.Packages[0].Path == exe && result.Warnings.Length == 0,
            "recursive scan prefers matching setup over newer ZIP of same version");
        string generic = Path.Combine(root, "NovaTouch Server Suite Setup.exe");
        File.Copy(source, generic);
        Assert((await catalog.ReadFileAsync(generic, CancellationToken.None)) is [{ Version: "2026.4.9999" }], "default Inno filename also works via metadata");
        string wrongName = Path.Combine(root, "nt.server.suite-2026.4.8888.exe");
        File.Copy(source, wrongName);
        bool rejected = false;
        try { await catalog.ReadFileAsync(wrongName, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "filename/version mismatch cannot be selected");
        File.Delete(wrongName);
        string unrelated = Path.Combine(root, "unrelated.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), unrelated);
        Assert((await catalog.ReadFileAsync(unrelated, CancellationToken.None)).Length == 0, "unrelated EXEs in software tree are ignored");
        string invalidSetup = Path.Combine(root, "nt.server.suite-2026.4.9999.exe");
        File.Copy(unrelated, invalidSetup);
        result = await catalog.ReadAsync(root, null, CancellationToken.None);
        Assert(result.Warnings.Length == 1 && result.Warnings[0].Contains("kein NovaTouch"), "an unrelated renamed EXE is reported without executing or selecting it");

        var selectionWorker = new FakeRunner();
        using var form = new MainForm(preview: true, selectionWorker, new FakeRunner());
        using var picker = MainForm.CreateServerSuiteFileDialog();
        Assert(picker.FilterIndex == 1 && picker.Filter.Split('|')[1] == "*.exe;*.zip",
            "the default Server Suite picker shows EXE and ZIP packages together");
        var warnings = new List<string>();
        Action<string> warn = warnings.Add;
        Assert((bool)Call(form, "TrySelectServerSuitePackage", v2Setup, warn)! && warnings.Count == 0 && selectionWorker.Requests.IsEmpty,
            "compatibility selection accepts v2 without a warning, rebuild requirement or worker execution");
        Assert((bool)Call(form, "TrySelectServerSuitePackage", exe, warn)!, "supported EXE is selected through the real UI validation path");
        Assert(!(bool)Call(form, "TrySelectServerSuitePackage", markerlessSetup, warn)! && warnings.Count == 1 &&
            warnings[0].Contains("ab v1") && warnings[0].Contains("ZIP") &&
            Field<string>(form, "serverSuiteZip") == exe && selectionWorker.Requests.IsEmpty,
            "an older setup warns that ZIP is required, preserves the valid selection and starts no worker");
        warnings.Clear();
        Field<NumericUpDown>(form, "nUpdate").Value = 3;
        Call(form, "CaptureCredentials");
        Field<NumericUpDown>(form, "nUpdate").Value = 7;
        foreach (bool local in new[] { false, true })
        {
            var host = new HostState { Host = local ? "LOCALHOST" : "fixture.invalid" };
            var request = (WorkerRequest)Call(form, "Request", "update", host, UpdateComponents.ServerSuite)!;
            Assert(request.ServerSuiteSetup == exe && request.ServerSuiteZip.Length == 0 && request.ServerSuiteVersion == "2026.4.9999" && request.Parallelism == 3,
                (local ? "local" : "remote") + " setup request includes target version and the update parallelism captured before starting work");
        }
        Assert(!(bool)Call(form, "TrySelectServerSuitePackage", invalidSetup, warn)! && warnings.Count == 1 &&
            warnings[0].Contains("kein NovaTouch") &&
            Field<string>(form, "serverSuiteZip") == exe && selectionWorker.Requests.IsEmpty,
            "an unrelated EXE gives one warning without crashing, replacing the valid selection or starting a worker");
        Assert(!(bool)Call(form, "TrySelectServerSuitePackage", Path.Combine(root, "invalid-name.zip"), warn)! && warnings.Count == 2 &&
            Field<string>(form, "serverSuiteZip") == exe, "an invalid ZIP name also warns and preserves the selected setup");
        Assert(!(bool)Call(form, "TrySelectServerSuitePackage", Path.Combine(root, "missing.exe"), warn)! && warnings.Count == 3 &&
            Field<string>(form, "serverSuiteZip") == exe, "an unreadable setup warns without changing the current selection");
        File.Copy(source, wrongName);
        Assert(!(bool)Call(form, "TrySelectServerSuitePackage", wrongName, warn)! && warnings.Count == 4 &&
            Field<string>(form, "serverSuiteZip") == exe, "a mismatched setup version warns instead of escaping the UI event handler");
        Assert((bool)Call(form, "TrySelectServerSuitePackage", zip, warn)!, "ZIP selection remains available after an unsupported EXE warning");
        foreach (bool local in new[] { false, true })
        {
            var zipRequest = (WorkerRequest)Call(form, "Request", "update", new HostState { Host=local ? "LOCALHOST" : "fixture.invalid" }, UpdateComponents.ServerSuite)!;
            Assert(zipRequest.ServerSuiteZip == zip && zipRequest.ServerSuiteSetup.Length == 0 && zipRequest.ServerSuiteVersion == "2026.4.9999",
                (local ? "local" : "remote") + " ZIP selection routes to the existing stop/process-check/cleanup/copy/start workflow");
        }
        string oldZip = Path.Combine(root, "nt.server.suite-2025.1.1.zip");
        using (ZipFile.Open(oldZip, ZipArchiveMode.Create)) { }
        Assert((bool)Call(form, "TrySelectServerSuitePackage", oldZip, warn)! && Field<string>(form, "serverSuiteVersion") == "2025.1.1" && warnings.Count == 4,
            "older ZIP releases can be selected without requiring EXE silent parameters");
        var legacy = (WorkerRequest)Call(form, "Request", "update", new HostState { Host="fixture.invalid" }, UpdateComponents.ServerSuite)!;
        Assert(legacy.ServerSuiteZip == oldZip && legacy.ServerSuiteSetup.Length == 0, "explicit legacy ZIP selection keeps existing deployment path");
        Assert(Field<Button>(form, "btnServerSuite").Text == "Server-Suite-Paket wählen", "manual selector advertises both package formats");
        Assert(!JsonSerializer.Serialize(legacy).Contains("UPDATER=1"), "command lines are constructed only inside worker from validated instance data");

        var worker = new UpdateRunner();
        using var retryForm = UpdateForm(worker, force:true);
        SetField(retryForm, "serverSuiteZip", exe);
        HostState retryHost = Populate(retryForm)[0];
        retryHost.Updates = new(UpdateComponents.ServerSuite);
        worker.Outcome = request => worker.Count(retryHost.Host, UpdateComponents.ServerSuite) == 1
            ? new WorkerResult { Success=false, Error="Second instance failed", RebootRequired=true, CompletedServiceNames=["nt3_server_suite_A"] }
            : worker.Success(request);
        Update(retryForm, retryHost);
        var attempts = worker.Requests.Where(r => r.Action == "update").ToArray();
        Assert(attempts.Length == 2 && attempts.All(r => r.ServerSuiteSetup == exe && r.ServerSuiteZip.Length == 0) &&
            attempts[1].CompletedServiceNames.SequenceEqual(["nt3_server_suite_A"]) && retryHost.RebootRequired,
            "setup retry retains verified instances and reboot requirement from partial result");
    }
}
