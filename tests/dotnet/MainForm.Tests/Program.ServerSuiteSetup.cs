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
        string oldSetup = Path.Combine(root, "nt.server.suite-2026.4.9999.exe");
        File.Copy(unrelated, oldSetup);
        result = await catalog.ReadAsync(root, null, CancellationToken.None);
        Assert(result.Warnings.Length == 1 && result.Warnings[0].Contains("Inno-Skript"), "unsupported setup is reported without executing or selecting it");

        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.ServerSuite, exe, "2026.4.9999"));
        foreach (bool local in new[] { false, true })
        {
            var host = new HostState { Host = local ? "LOCALHOST" : "fixture.invalid" };
            var request = (WorkerRequest)Call(form, "Request", "update", host, UpdateComponents.ServerSuite)!;
            Assert(request.ServerSuiteSetup == exe && request.ServerSuiteZip.Length == 0 && request.ServerSuiteVersion == "2026.4.9999",
                (local ? "local" : "remote") + " request routes setup separately from legacy ZIP without losing target version");
        }
        Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.ServerSuite, zip, "2026.4.9999"));
        var legacy = (WorkerRequest)Call(form, "Request", "update", new HostState { Host="fixture.invalid" }, UpdateComponents.ServerSuite)!;
        Assert(legacy.ServerSuiteZip == zip && legacy.ServerSuiteSetup.Length == 0, "explicit legacy ZIP selection keeps existing deployment path");
        Assert(Field<Button>(form, "btnServerSuite").Text == "Server-Suite-Setup wählen", "manual selector advertises the setup workflow");
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
