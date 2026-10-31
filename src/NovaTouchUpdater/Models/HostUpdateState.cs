namespace NovaTouchUpdater;

[Flags]
internal enum UpdateComponents
{
    None = 0, Payment = 1, Fiscal = 2, ServerSuite = 4, Pos = 8, Ntc = 16, Ntv = 32, Oman = 64,
    ZipServices = Payment | Fiscal | ServerSuite, Msi = Pos | Ntc | Ntv | Oman, All = ZipServices | Msi
}

// One update run, including automatic attempts and later manual retries.
// Only verified successes leave Pending; scans and commands do not reset it.
internal sealed class HostUpdateState(UpdateComponents components)
{
    internal UpdateComponents Pending { get; private set; } = components;
    internal UpdateComponents Attempted { get; private set; }
    internal bool Changed { get; private set; }
    internal bool AllSkipped { get; private set; } = true;
    internal bool DatabaseUploadCompleted { get; set; }
    internal bool PosRestartCompleted { get; set; }
    internal string PosRestartMessage { get; private set; } = "";
    internal string DatabaseUploadError { get; set; } = "";
    internal string PosRestartError { get; set; } = "";
    internal HashSet<string> CompletedServiceNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal void CompletePosRestart(WorkerResult result)
    {
        PosRestartCompleted = true;
        PosRestartError = "";
        PosRestartMessage = result.Message.Length > 0 ? result.Message : "POS-Neustart nach Update abgeschlossen";
        Changed |= !result.Skipped;
        AllSkipped &= result.Skipped;
    }
    internal void CompleteDatabaseUpload(bool changed)
    {
        DatabaseUploadCompleted = true;
        DatabaseUploadError = "";
        Changed |= changed;
        AllSkipped &= !changed;
    }
    private readonly Dictionary<UpdateComponents, string> errors = [];

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
    internal void BeginComponent(UpdateComponents component) => Attempted |= component;
    internal void RestrictTo(UpdateComponents allowed)
    {
        Pending &= allowed;
        Attempted &= allowed;
        foreach (UpdateComponents component in errors.Keys.Where(component => (component & allowed) == 0).ToArray())
            errors.Remove(component);
    }
    internal string ErrorText => string.Join(" | ", Each(Pending).Where(errors.ContainsKey).Select(c => Label(c) + ": " + errors[c])
        .Concat(new[] { DatabaseUploadError, PosRestartError }.Where(error => error.Length > 0)));

    internal void Complete(UpdateComponents component, WorkerResult result)
    {
        if (!result.Success) throw new ArgumentException("Nur verifizierte Komponenten dürfen als abgeschlossen markiert werden.", nameof(result));
        Pending &= ~component;
        errors.Remove(component);
        Changed |= result.Changed;
        AllSkipped &= result.Skipped;
    }

    internal void Fail(UpdateComponents component, string error) => errors[component] = error;
}
