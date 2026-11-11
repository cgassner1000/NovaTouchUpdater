namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Func<ServicePackHostPlan[], ServicePackHostPlan[]?>? selectSoftwareCompilationTargets = null;
    private Action<string>? showSoftwareCompilationWarning = null;
    private Action<IReadOnlyList<SoftwareCompilationHostOutcome>>? showSoftwareCompilationResults = null;

    private async Task CompileSoftwarePackagesAsync()
    {
        if (betaFeatures?.Enabled != true || busy || selfUpdating || loadingPackages || preparingDatabaseUpload || restartingAsAdministrator) return;
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
                        var request = new WorkerRequest { Action = "compile-software-packages", Host = plan.Host.Host, LocalExecution = plan.Host.IsLocal,
                            Username = plan.Host.IsLocal ? "" : activeUser, Password = plan.Host.IsLocal ? "" : activePassword,
                            UseSsl = !plan.Host.IsLocal && activeSsl, HttpsPort = activeHttpsPort,
                            DatabaseTargets = plan.Targets, IrisUseTls = activeIrisUseTls };
                        // An accepted host finishes; cancellation skips subsequent hosts.
                        result = await runner.RunAsync(request, progress =>
                        {
                            Log(plan.Host.Host, progress.Phase + ": " + progress.Message);
                            if (progress.Phase == "Softwarepaket-Zeiten") return;
                            Mark(plan.Host, host =>
                            {
                                host.Error = "";
                                host.Phase = progress.Phase == "Softwarepakete vorbereiten" ? progress.Phase : DisplayText.Clean(progress.Message);
                                host.SetProgress(progress.ProgressPercent);
                            });
                        }, CancellationToken.None);
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
            else MessageBox.Show(this, ex.Message, "Softwarepakete kompilieren", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
