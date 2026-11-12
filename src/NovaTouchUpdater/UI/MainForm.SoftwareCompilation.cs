namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Func<ServicePackHostPlan[], ServicePackHostPlan[]?>? selectSoftwareCompilationTargets = null;
    private Action<string>? showSoftwareCompilationWarning = null;
    private Action<IReadOnlyList<SoftwareCompilationHostOutcome>>? showSoftwareCompilationResults = null;
    private bool activeCompileAfterInstallation;

    private Task<WorkerResult> RunSoftwareCompilationAsync(HostState host, IrisUploadTarget[] targets)
    {
        var request = new WorkerRequest { Action = "compile-software-packages", Host = host.Host, LocalExecution = host.IsLocal,
            Username = host.IsLocal ? "" : activeUser, Password = host.IsLocal ? "" : activePassword,
            UseSsl = !host.IsLocal && activeSsl, HttpsPort = activeHttpsPort,
            DatabaseTargets = targets, IrisUseTls = activeIrisUseTls };
        // An accepted host finishes; cancellation skips subsequent hosts/phases.
        return runner.RunAsync(request, progress =>
        {
            Log(host.Host, progress.Phase + ": " + progress.Message);
            if (progress.Phase == "Softwarepaket-Zeiten") return;
            Mark(host, h =>
            {
                h.Error = "";
                h.Phase = progress.Phase == "Softwarepakete vorbereiten" ? progress.Phase : DisplayText.Clean(progress.Message);
                h.SetProgress(progress.ProgressPercent);
            });
        }, CancellationToken.None);
    }

    private async Task CompileAfterNtcInstallationAsync(HostState host, CancellationToken ct, Lazy<Task<IrisUploadTarget[]>> localTargets)
    {
        HostUpdateState state = host.Updates!;
        if (!activeCompileAfterInstallation || !host.IsLocal || !state.NtcInstalled || state.SoftwareCompilationCompleted) return;
        try
        {
            ct.ThrowIfCancellationRequested();
            Mark(host, h => { state.SoftwareCompilationRunning = true; h.Result = "Läuft"; h.Error = ""; h.Attempt = 0; h.Phase = "Softwarepakete nach NTC-Installation vorbereiten"; });
            IrisUploadTarget[] targets = await localTargets.Value;
            ct.ThrowIfCancellationRequested();
            if (targets.Length == 0)
                throw new InvalidDataException("Keine lokalen IRIS-Namespaces ohne %-Präfix gefunden. Check Database prüfen.");
            if (targets.Select(target => target.Port).Distinct().Count() != targets.Length)
                throw new InvalidDataException("Mehrdeutige IRIS-Instanzen mit demselben SuperServer-Port. Check Database prüfen.");
            Log(host.Host, "Softwarepakete nach NTC-Installation: " + targets.Sum(target => target.Namespaces.Length) + " lokale IRIS-Namespaces.");
            WorkerResult result = await RunSoftwareCompilationAsync(host, targets);
            if (!result.Success) throw new IOException(FriendlyError(result));
            Mark(host, h => state.CompleteSoftwareCompilation(result));
            Log(host.Host, "Softwarepakete nach NTC-Installation => OK " + result.Message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Mark(host, h => state.SoftwareCompilationError = "Softwarepakete nach NTC-Installation: " + DisplayText.Clean(ex.Message));
            Log(host.Host, "Softwarepakete nach NTC-Installation => FEHLER " + ex.Message);
        }
        finally
        {
            Mark(host, h =>
            {
                state.SoftwareCompilationRunning = false;
                if (state.DatabaseUploadRunning) { h.Result = "Läuft"; h.Error = ""; h.Phase = "zzzSoftware-Upload läuft"; }
            });
        }
    }

    private async Task CompileSoftwarePackagesAsync()
    {
        if (busy || selfUpdating || loadingPackages || preparingDatabaseUpload || restartingAsAdministrator) return;
        HostState[] selected = SelectedHosts();
        if (selected.Length == 0 || !ValidateCredentials()) return;
        try
        {
            ServicePackHostPlan[] plans;
            using (var preparation = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token))
            {
                loadingPackages = true; packageCancellation = preparation; UpdateButtons();
                try
                {
                    plans = await PrepareServicePackTargetsAsync(selected, preparation.Token);
                    preparation.Token.ThrowIfCancellationRequested();
                }
                finally { loadingPackages = false; packageCancellation = null; if (!Disposing && !IsDisposed) UpdateButtons(); }
            }
            int namespaces = plans.Sum(plan => plan.Targets.Sum(target => target.Namespaces.Length));
            if (namespaces == 0) throw new InvalidDataException("Keine IRIS-Namespaces ohne %-Präfix auf den ausgewählten Hosts gefunden. Check Database prüfen.");
            ServicePackHostPlan[]? selection;
            if (selectSoftwareCompilationTargets is { } select) selection = select(plans);
            else
            {
                using var dialog = DatabaseTargetsDialog.ForSoftwareCompilation(plans);
                selection = dialog.ShowDialog(this) == DialogResult.OK ? dialog.GetSelectedPlans() : null;
            }
            if (selection is null || selection.Length == 0) return;
            plans = selection;
            CaptureCredentials();
            var outcomes = new List<SoftwareCompilationHostOutcome>();
            await InBatchAsync(plans.Select(plan => plan.Host).ToArray(), async ct =>
            {
                foreach (ServicePackHostPlan plan in plans)
                {
                    bool cancelledBeforeStart = ct.IsCancellationRequested;
                    WorkerResult result;
                    if (cancelledBeforeStart) result = new() { Success = false, Message = "Abgebrochen; Host wurde nicht gestartet." };
                    else if (plan.Targets.Length == 0) result = new() { Success = true, Skipped = true, Message = "Keine IRIS-Namespaces ohne %-Präfix; übersprungen." };
                    else
                    {
                        result = await RunSoftwareCompilationAsync(plan.Host, plan.Targets);
                    }
                    outcomes.Add(new(plan.Host.IsLocal ? Environment.MachineName + " (lokal)" : plan.Host.Host, result));
                    Log(plan.Host.Host, "Softwarepakete kompilieren: " + result.Message + (result.Error.Length > 0 ? " | " + result.Error : ""));
                    Mark(plan.Host, host =>
                    {
                        host.Result = result.Success ? (result.Skipped ? "Übersprungen" : "Abgeschlossen") : cancelledBeforeStart ? "Abgebrochen" : "Fehler";
                        host.Phase = result.Message;
                        host.Error = result.Error;
                        if (result.Success && !result.Skipped) host.SetProgress(100);
                    });
                    CountCompleted();
                }
            });
            if (!IsDisposed)
            {
                if (showSoftwareCompilationResults is { } show) show(outcomes);
                else { using var dialog = new SoftwareCompilationResultsDialog(outcomes); dialog.ShowDialog(this); }
            }
        }
        catch (OperationCanceledException) { Log("SYSTEM", "Vorbereitung für Software-Kompilierung abgebrochen."); }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            if (showSoftwareCompilationWarning is { } show) show(ex.Message);
            else AppMessageBox.Show(this, ex.Message, "Softwarepakete kompilieren", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
