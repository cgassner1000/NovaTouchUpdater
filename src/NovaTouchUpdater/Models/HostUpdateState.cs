namespace NovaTouchUpdater;

[Flags]
internal enum UpdateComponents
{
    None = 0, Payment = 1, Fiscal = 2, ServerSuite = 4, Pos = 8, Ntc = 16, Ntv = 32, Oman = 64,
    ZipServices = Payment | Fiscal | ServerSuite, Msi = Pos | Ntc | Ntv | Oman, All = ZipServices | Msi
}

internal enum ComponentUpdateStatus { Updating, Succeeded, Failed, Cancelled }
internal readonly record struct ComponentUpdateProgress(ComponentUpdateStatus Status, int? Percent = null, string Phase = "", string Message = "");

// One update run, including automatic attempts and later manual retries.
// Only verified successes leave Pending; scans and commands do not reset it.
internal sealed class HostUpdateState(UpdateComponents components)
{
    internal Guid RunId { get; init; } = Guid.NewGuid();
    internal UpdateComponents Pending { get; private set; } = components;
    internal UpdateComponents Attempted { get; private set; }
    internal bool Changed { get; private set; }
    internal bool AllSkipped { get; private set; } = true;
    internal bool NtcInstalled { get; private set; }
    internal bool SoftwareCompilationCompleted { get; private set; }
    internal bool SoftwareCompilationRunning { get; set; }
    internal bool DatabaseUploadRunning { get; set; }
    internal string SoftwareCompilationMessage { get; private set; } = "";
    internal string SoftwareCompilationError { get; set; } = "";
    internal void CompleteSoftwareCompilation(WorkerResult result)
    {
        SoftwareCompilationCompleted = true;
        SoftwareCompilationError = "";
        SoftwareCompilationMessage = "Softwarepakete nach NTC-Installation kompiliert";
        Changed |= result.Changed;
        AllSkipped &= result.Skipped;
    }
    internal bool DatabaseUploadCompleted { get; set; }
    internal bool DatabaseUploadChanged { get; private set; }
    internal bool SuccessfulPosUploadInRun { get; set; }
    internal string PosVersionBeforeUpdate { get; set; } = "";
    internal string DatabaseUploadMessage { get; set; } = "";
    internal string PosRestartSkipMessage { get; set; } = "";
    internal bool PosRestartCompleted { get; set; }
    internal string PosRestartMessage { get; private set; } = "";
    internal string DatabaseUploadError { get; set; } = "";
    internal string PosRestartError { get; set; } = "";
    internal bool AdditionalFilesCompleted { get; private set; }
    internal string AdditionalFilesMessage { get; private set; } = "";
    internal string AdditionalFilesError { get; set; } = "";
    internal void CompleteAdditionalFiles(bool changed, string message)
    {
        AdditionalFilesCompleted = true;
        AdditionalFilesError = "";
        AdditionalFilesMessage = message;
        Changed |= changed;
        AllSkipped &= !changed;
    }
    internal void ResetAdditionalFiles()
    {
        AdditionalFilesCompleted = false;
        AdditionalFilesMessage = AdditionalFilesError = "";
    }
    internal HashSet<string> CompletedServiceNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal void CompletePosRestart(WorkerResult result)
    {
        PosRestartCompleted = true;
        PosRestartError = "";
        PosRestartMessage = result.Message.Length > 0 ? result.Message : "POS-Neustart nach Update abgeschlossen";
        Changed |= !result.Skipped;
        AllSkipped &= result.Skipped;
    }
    internal void CompleteDatabaseUpload(bool changed, string message = "")
    {
        DatabaseUploadCompleted = true;
        DatabaseUploadChanged |= changed;
        DatabaseUploadMessage = message.Length > 0 ? message : changed ? "zzzSoftware-Upload abgeschlossen" : "zzzSoftware geprüft – kein Upload erforderlich";
        DatabaseUploadError = "";
        Changed |= changed;
        AllSkipped &= !changed;
    }

    internal void ResetDatabaseUpload()
    {
        DatabaseUploadCompleted = DatabaseUploadChanged = SuccessfulPosUploadInRun = PosRestartCompleted = false;
        DatabaseUploadError = DatabaseUploadMessage = PosRestartError = PosRestartSkipMessage = "";
    }
    internal void RecordDatabaseUploads(IEnumerable<string> kinds) => SuccessfulPosUploadInRun |= kinds.Contains("pos", StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<UpdateComponents, string> errors = [];
    private readonly Dictionary<UpdateComponents, ComponentUpdateProgress> componentProgress = [];

    internal ComponentUpdateProgress? ProgressFor(UpdateComponents component)
        => componentProgress.TryGetValue(component, out var progress) ? progress : null;

    internal void ReportProgress(UpdateComponents component, WorkerEvent progress)
    {
        foreach (var part in Each(component))
            if (componentProgress.TryGetValue(part, out var current) && current.Status == ComponentUpdateStatus.Updating)
                componentProgress[part] = new(ComponentUpdateStatus.Updating,
                    progress.ProgressPercent is int percent ? Math.Clamp(percent, 0, 100) : null, progress.Phase, progress.Message);
    }

    internal void StopActiveComponents(string error = "")
    {
        foreach (var part in componentProgress.Where(pair => pair.Value.Status == ComponentUpdateStatus.Updating).Select(pair => pair.Key).ToArray())
            if (error.Length > 0) Fail(part, error);
            else componentProgress[part] = new(ComponentUpdateStatus.Cancelled);
    }

    internal static IEnumerable<UpdateComponents> Each(UpdateComponents components) =>
        new[] { UpdateComponents.Payment, UpdateComponents.Fiscal, UpdateComponents.ServerSuite, UpdateComponents.Pos,
            UpdateComponents.Ntc, UpdateComponents.Ntv, UpdateComponents.Oman }.Where(c => (components & c) != 0);

    internal static string Label(UpdateComponents component) => component switch
    {
        UpdateComponents.Payment => "nt.payment",
        UpdateComponents.Fiscal => "nt.fiscal",
        UpdateComponents.ServerSuite => "Server Suite",
        UpdateComponents.Pos => "NovaTouch POS",
        UpdateComponents.Ntc => "NovaTouch Control",
        UpdateComponents.Ntv => "NovaTouch Voucher",
        UpdateComponents.Oman => "NovaTouch Orderman SOL",
        _ => string.Join(", ", Each(component).Select(Label))
    };

    internal bool WasAttempted(UpdateComponents component) => (Attempted & component) != 0;
    internal void BeginComponent(UpdateComponents component)
    {
        Attempted |= component;
        foreach (var part in Each(component))
        {
            errors.Remove(part);
            componentProgress[part] = new(ComponentUpdateStatus.Updating);
        }
    }
    internal void RestrictTo(UpdateComponents allowed)
    {
        Pending &= allowed;
        Attempted &= allowed;
        foreach (UpdateComponents component in errors.Keys.Where(component => (component & allowed) == 0).ToArray())
            errors.Remove(component);
        foreach (var component in componentProgress.Keys.Where(component => (component & allowed) == 0).ToArray())
            componentProgress.Remove(component);
    }
    internal string ErrorText => string.Join(" | ", Each(Pending).Where(errors.ContainsKey).Select(c => Label(c) + ": " + errors[c])
        .Concat(new[] { SoftwareCompilationError, DatabaseUploadError, PosRestartError, AdditionalFilesError }.Where(error => error.Length > 0)));

    internal void Complete(UpdateComponents component, WorkerResult result)
    {
        if (!result.Success) throw new ArgumentException("Nur verifizierte Komponenten dürfen als abgeschlossen markiert werden.", nameof(result));
        Pending &= ~component;
        foreach (var part in Each(component))
        {
            errors.Remove(part);
            componentProgress[part] = new(ComponentUpdateStatus.Succeeded, 100, "Update verifiziert", result.Message);
        }
        Changed |= result.Changed;
        AllSkipped &= result.Skipped;
        NtcInstalled |= (component & UpdateComponents.Ntc) != 0 && result.Changed && !result.Skipped;
    }

    internal void Fail(UpdateComponents component, string error)
    {
        foreach (var part in Each(component))
        {
            errors[part] = error;
            componentProgress[part] = new(ComponentUpdateStatus.Failed, null, "Update fehlgeschlagen", error);
        }
    }
}
