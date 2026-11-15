namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private static readonly UpdateComponents[] msiInstallationOrder =
        [UpdateComponents.Ntc, UpdateComponents.Pos, UpdateComponents.Ntv, UpdateComponents.Oman];
    private async Task<bool> PrepareHostInstallationAsync(HostState host, CancellationToken ct, Func<TimeSpan, CancellationToken, Task> delay)
    {
        for (int attempt = 1; !host.Scanned && attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Mark(host, h => { h.Attempt = attempt; h.Result = "Läuft"; h.Error = ""; h.Phase = "Prüfung vor Update"; h.DotNet48Installed = null; h.PosVersion = "–"; });
            WorkerResult result = host.IsLocal ? new() { Success = true } : await DoWorkerAsync(host, "probe", ct);
            if (result.Success)
            {
                Mark(host, h => h.ConnectionState.SetForegroundStatus(h.IsLocal ? "Lokal" : "Verbunden", h.IsLocal ? null : activeSsl));
                result = await DoWorkerAsync(host, "scan", ct);
                Mark(host, h =>
                {
                    ReadScanInventory(h, result);
                    if (!result.Success) return;
                    h.Scanned = true; h.PaymentBefore = h.Payment; h.FiscalBefore = h.Fiscal; h.ServerSuiteBefore = h.ServerSuite;
                });
            }
            else Mark(host, h => h.Connection = "Nicht erreichbar");
            if (result.Success) return true;
            Mark(host, h =>
            {
                h.Error = FriendlyError(result);
                if (attempt == 3)
                    foreach (var component in HostUpdateState.Each(h.Updates!.Pending)) h.Updates.Fail(component, h.Error);
            });
            if (attempt < 3) await delay(TimeSpan.FromSeconds(attempt * 3), ct);
        }
        return host.Scanned;
    }

    private async Task<int> InstallComponentAsync(HostState host, UpdateComponents component, CancellationToken ct, Func<TimeSpan, CancellationToken, Task> delay)
    {
        HostUpdateState state = host.Updates!;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            bool retry = false;
            Mark(host, h =>
            {
                retry = state.WasAttempted(component);
                h.Result = "Läuft";
                if (!state.SoftwareCompilationRunning)
                { h.Attempt = attempt; h.Phase = "Update: " + HostUpdateState.Label(component); h.Error = ""; }
                state.BeginComponent(component, attempt);
            });
            WorkerResult result;
            try { result = await DoWorkerAsync(host, "update", ct, component, retry); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = new() { Success = false, Error = ex.Message }; }
            Mark(host, h =>
            {
                state.CompletedServiceNames.UnionWith(result.CompletedServiceNames);
                h.RebootRequired |= result.RebootRequired;
                MergeComponentInventory(h, component, result);
                if (result.Success) state.Complete(component, result);
                else state.Fail(component, FriendlyError(result));
                if (!state.SoftwareCompilationRunning)
                {
                    h.Attempt = attempt;
                    h.Phase = HostUpdateState.Label(component) + ": " + (result.Success
                        ? result.Message.Length > 0 && (result.Skipped || !result.Changed) ? result.Message : "verifiziert"
                        : "Update fehlgeschlagen");
                    h.Error = result.Success ? "" : FriendlyError(result);
                }
            });
            if (result.Success && (component & UpdateComponents.Msi) != 0)
                QueueInstallationUpload(host, component, ct);
            if (result.Success || result.AutomaticRetriesExhausted || attempt == 3) return attempt;
            await delay(TimeSpan.FromSeconds(attempt * 3), ct);
        }
        return 3;
    }

    private static string ComponentKind(UpdateComponents component) => component switch
    {
        UpdateComponents.Payment => "payment", UpdateComponents.Fiscal => "fiscal", UpdateComponents.ServerSuite => "serversuite",
        UpdateComponents.Pos => "pos", UpdateComponents.Ntc => "ntc", UpdateComponents.Ntv => "ntv", UpdateComponents.Oman => "oman",
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    // A worker's full inventory may predate another concurrent installation.
    // Only the result for its own component is authoritative here.
    private void MergeComponentInventory(HostState host, UpdateComponents component, WorkerResult result)
    {
        string kind = ComponentKind(component);
        if ((component & UpdateComponents.ZipServices) != 0)
        {
            if (!result.Success && result.Services.Length == 0) return;
            host.Services = host.Services.Where(service => !service.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .Concat(result.Services.Where(service => service.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))).ToList();
            ReadVersions(host);
            return;
        }
        if (!result.Success) return;
        host.InstalledPrograms = host.InstalledPrograms.Where(program => !program.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Concat(result.InstalledPrograms.Where(program => program.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))).ToList();
        if (component == UpdateComponents.Pos) host.PosVersion = string.IsNullOrWhiteSpace(result.PosVersion) ? "–" : DisplayText.Clean(result.PosVersion);
        if (component == UpdateComponents.Ntc) host.NtcVersion = string.IsNullOrWhiteSpace(result.NtcVersion) ? "–" : DisplayText.Clean(result.NtcVersion);
        if (component is UpdateComponents.Ntv or UpdateComponents.Oman)
        {
            string[] versions = host.InstalledPrograms.Where(program => program.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .Select(program => string.IsNullOrWhiteSpace(program.Version) ? "Version unbekannt" : DisplayText.Clean(program.Version)).Distinct().ToArray();
            string version = versions.Length == 0 ? "Nicht installiert" : string.Join(", ", versions);
            if (component == UpdateComponents.Ntv) host.NtvVersion = version;
            else host.OmanVersion = version;
        }
    }

    private UpdateComponents[][] InstallationLanes(HostState host, UpdateComponents pending)
    {
        var lanes = HostUpdateState.Each(pending & UpdateComponents.ZipServices).Select(component => new[] { component }).ToList();
        UpdateComponents[] msi = msiInstallationOrder.Where(component => (pending & component) != 0).ToArray();
        if (msi.Length > 0) lanes.Add(msi);
        for (int i = 0; i < lanes.Count; i++)
            for (int j = i + 1; j < lanes.Count; j++)
            {
                string[] left = InstallationResources(host, lanes[i]);
                string[] right = InstallationResources(host, lanes[j]);
                if (!SharedInstallationResources(left, right)) continue;
                Log(host.Host, "Gemeinsame Installationsziele oder Paketdateien: " + string.Join(", ", lanes[i].Concat(lanes[j]).Select(HostUpdateState.Label)) + " werden nacheinander installiert.");
                UpdateComponents combined = lanes[i].Concat(lanes[j]).Aggregate(UpdateComponents.None, (all, component) => all | component);
                lanes[i] = HostUpdateState.Each(combined & UpdateComponents.ZipServices)
                    .Concat(msiInstallationOrder.Where(component => (combined & component) != 0)).ToArray();
                lanes.RemoveAt(j);
                i = -1;
                break;
            }
        return lanes.ToArray();
    }

    private static bool SharedInstallationResources(string[] left, string[] right) => left.Any(a => right.Any(b =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

    private void StartInstallationCompilation(HostState host, CancellationToken ct)
    {
        if (cancellation is null || !activeCompileAfterInstallation || installationCompilations.ContainsKey(host)) return;
        CancellationToken runToken = cancellation?.Token ?? ct;
        installationCompilations[host] = CompileAfterNtcInstallationAsync(host, runToken, InstallationDatabaseTargets(host, runToken));
    }

    private Lazy<Task<IrisUploadTarget[]>> InstallationDatabaseTargets(HostState host, CancellationToken ct)
    {
        if (host.IsLocal) return installationDatabaseTargets ??= new Lazy<Task<IrisUploadTarget[]>>(() => prepareDatabaseTargets(ct));
        return remoteInstallationTargets.GetOrAdd(host, h => new Lazy<Task<IrisUploadTarget[]>>(async () =>
        {
            WorkerResult inventory = await runner.RunAsync(Request("database-inventory", h, UpdateComponents.None), _ => { }, ct);
            if (!inventory.Success) throw new InvalidDataException(FriendlyError(inventory));
            return IrisUploadPlan.Create(new IrisInventoryResult(inventory.IrisInstances.Select(instance => new IrisInstanceInfo(
                instance.Name, instance.Directory, instance.ConfigurationFile, instance.Version, instance.Namespaces, instance.Error,
                instance.SuperServerPort, instance.ConnectionError)).ToArray(), []), activeIrisUseTls);
        }));
    }

    private string[] InstallationResources(HostState host, UpdateComponents[] components)
    {
        var kinds = components.Select(ComponentKind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> resources = host.Services.Where(service => kinds.Contains(service.Kind))
            .SelectMany(service => new[] { service.InstallDir, service.Kind.Equals("serversuite", StringComparison.OrdinalIgnoreCase) ? service.RegistryInstallDir : "" })
            .Concat(host.InstalledPrograms.Where(program => kinds.Contains(program.Kind)).Select(program => program.InstallLocation));
        if (!host.IsLocal && !activeSkipZipTransfer && !activeSkipRemotePackageCopy)
            resources = resources.Concat(PackageFileCandidates(components.Aggregate(UpdateComponents.None, (all, component) => all | component))
                .Select(file => Path.Combine(activeRemoteInstallPath, file.RelativePath)));
        return resources.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
