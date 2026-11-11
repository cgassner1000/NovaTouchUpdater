using NovaTouchUpdater;

internal static partial class Program
{
    private static void UpdateTargetingTests()
    {
        var worker = new TargetingRunner();
        using var form = new MainForm(preview:true, worker, new FakeRunner());
        _ = form.Handle;
        var hosts = Field<List<HostState>>(form, "hosts");
        foreach (UpdateComponents component in HostUpdateState.Each(UpdateComponents.All))
        {
            string kind = component.ToString().ToLowerInvariant();
            var host = new HostState { Host=kind + ".invalid", Scanned=true };
            var inventory = new WorkerResult { Success=true,
                Services=(component & UpdateComponents.ZipServices) != 0 ? [new ServiceInfo { Kind=kind, Name=kind, ProductVersion="1.0.0" }] : [],
                InstalledPrograms=(component & UpdateComponents.Msi) != 0 ? [new InstalledProgramInfo { Kind=kind, Version="1.0.0" }] : [],
                PosVersion=component == UpdateComponents.Pos ? "1.0.0" : "Nicht installiert",
                NtcVersion=component == UpdateComponents.Ntc ? "1.0.0" : "Nicht installiert" };
            worker.Inventory[host.Host]=inventory;
            Call(form, "ReadScanInventory", host, inventory);
            hosts.Add(host);
        }
        Call(form, "RebuildRows");
        HostState[] remote = hosts.Where(host => !host.IsLocal).ToArray();
        SoftwarePackage Package(UpdateComponents component) => new(component, component + ".fixture", "2.0.0",
            (component & UpdateComponents.Msi) != 0 ? new MsiPackageRequest { Kind=component.ToString().ToLowerInvariant(), ProductVersion="2.0.0" } : null);
        foreach (UpdateComponents component in HostUpdateState.Each(UpdateComponents.All))
        {
            Call(form, "ClearPackageSelection");
            Call(form, "SelectIndividualPackage", Package(component));
            Assert(Updates(form, false).Select(host => host.Host).SequenceEqual([component.ToString().ToLowerInvariant() + ".invalid"]),
                "only known hosts with installed " + component + " are eligible for update WinRM");
        }
        var registryOnly = new HostState { Host="registry-only.invalid", Scanned=true };
        var registryService = new ServiceInfo { Name="nt3_server_suite_C4U1", Kind="serversuite", ServiceExists=false, State="Dienst fehlt",
            RegistryInstance="C4U1", RegistryInstallDir=@"C:\Novacom\novatouch3\C4U1", RegistryView="Registry64", ProductVersion="2.0.0",
            InstallDir=@"C:\Novacom\novatouch3\C4U1\apps\nt3-server-suite" };
        Call(form, "ReadScanInventory", registryOnly, new WorkerResult { Success=true, Services=[registryService] });
        hosts.Add(registryOnly);
        Call(form, "ClearPackageSelection"); Call(form, "SelectIndividualPackage", Package(UpdateComponents.ServerSuite));
        Assert(Updates(form,false).Contains(registryOnly) && registryOnly.InstalledComponents == UpdateComponents.ServerSuite &&
            DisplayText.ServiceInstances(registryOnly.Services).Contains("Dienst fehlt"), "registry-only suite instances remain selectable for repair and show their missing Windows service");
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<ServiceInfo>(System.Text.Json.JsonSerializer.Serialize(registryService))!;
        Assert(!roundtrip.ServiceExists && roundtrip.RegistryInstance == "C4U1" && roundtrip.RegistryInstallDir == registryService.RegistryInstallDir,
            "registry identity and missing-service state survive the worker JSON boundary");
        Call(form, "ClearPackageSelection");
        Call(form, "SelectIndividualPackage", Package(UpdateComponents.Payment));
        Call(form, "SelectIndividualPackage", Package(UpdateComponents.Fiscal));
        Assert(Updates(form, false).Select(host => host.Host).ToHashSet().SetEquals(["payment.invalid", "fiscal.invalid"]),
            "multiple selected products target the union of installed products");
        Assert((UpdateComponents)Call(form, "ComponentsForUpdate", hosts.Single(host => host.Host == "payment.invalid"), false)! == UpdateComponents.Payment,
            "a matching host does not open another update connection for an absent selected component");
        Call(form, "ClearPackageSelection");
        Call(form, "SelectIndividualPackage", Package(UpdateComponents.Payment));
        worker.Requests.Clear();
        Complete((Task)Call(form, "RunScanPhaseAsync", (object)remote)!);
        Assert(worker.Requests.Count == 2 && worker.Requests.All(request => request.Host == "payment.invalid"),
            "known unrelated hosts receive neither probe nor scan connections");
        worker.Requests.Clear();
        Complete((Task)Call(form, "PrepareUploadDirectoriesAsync", remote, true,
            (Action<HostState,string>)((_, error) => throw new InvalidOperationException(error)), CancellationToken.None)!);
        Assert(worker.Requests.Count == 1 && worker.Requests[0].Host == "payment.invalid", "upload preflight also excludes known unrelated clients");
        Assert((bool)Call(form, "MatchesSelectedSoftware", new HostState { Host="unknown.invalid" })!,
            "unknown inventory remains discoverable because installed products cannot be known before the first inventory scan");
        Call(form, "ReadScanInventory", remote[0], new WorkerResult { Success=false });
        Assert(remote[0].InstalledComponents == UpdateComponents.Payment, "a failed later scan preserves the last known product snapshot");

        HostState databaseServer=hosts.Single(host => host.Host == "ntc.invalid");
        databaseServer.HasIrisInstances=true;
        Call(form, "ClearPackageSelection");
        Call(form, "SelectIndividualPackage", Package(UpdateComponents.Pos));
        Assert(Updates(form, false).Select(host => host.Host).ToHashSet().SetEquals(["pos.invalid", "ntc.invalid"]) &&
            (UpdateComponents)Call(form, "ComponentsForUpdate", databaseServer, false)! == UpdateComponents.None,
            "known IRIS server without POS remains a database-upload destination without receiving a remote MSI installation");
        Call(form, "ClearPackageSelection");
        Call(form, "SelectIndividualPackage", Package(UpdateComponents.Payment));
        Assert(!Updates(form, false).Contains(databaseServer), "service-only updates do not connect to an unrelated IRIS server");

        string notice="";
        SetField(form, "isProcessElevated", new Func<bool>(() => false));
        SetField(form, "showLocalAdministratorNotice", new Action<string>(text => notice=text));
        var local = new HostState { Host="LOCALHOST", InstalledComponents=UpdateComponents.Payment };
        worker.Requests.Clear();
        Assert(!(bool)Call(form, "EnsureLocalInstallationAccess", new[] { local }, false)! && notice.Contains("Administrator") &&
            notice.Contains("noch keine Update-Dateien") && worker.Requests.Count == 0,
            "local installation without elevation shows restart instructions before copying or worker calls");
        notice="";
        Assert((bool)Call(form, "EnsureLocalInstallationAccess", remote, false)! && notice.Length == 0,
            "remote updates do not require local administrator rights");
        SetField(form, "isProcessElevated", new Func<bool>(() => true));
        Assert((bool)Call(form, "EnsureLocalInstallationAccess", new[] { local }, false)!, "already elevated local installation proceeds");
        var startup=Field<CheckBox>(form, "chkScanOnStartup");
        var settings=(TableLayoutPanel)startup.Parent!;
        var generalDivider=settings.Controls["allgemeinSettingsDivider"];
        Assert(startup.Text == "Version beim Programmstart prüfen" && generalDivider is not null &&
            settings.GetRow(startup) == settings.GetRow(generalDivider) + 1,
            "startup version switch is the first option below the general settings divider");
        Assert(Field<CheckBox>(form, "chkIrisTls").Text == "IRIS-Verbindung mit TLS", "TLS option uses its regular caption behind beta activation");
    }

    private sealed class TargetingRunner : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal Dictionary<string,WorkerResult> Inventory { get; } = [];
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(request.Action == "scan" ? Inventory[request.Host] : new WorkerResult { Success=true, UploadRequired=false });
        }
        public void Dispose() { }
    }
}
