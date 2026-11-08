namespace NovaTouchUpdater;

internal sealed record ServicePackHostPlan(HostState Host, IrisUploadTarget[] Targets);

public sealed partial class MainForm
{
    private Func<ServicePackHostPlan[], int, ServicePackHostPlan[]?>? selectServicePackTargets = null;
    private Action<string>? showServicePackWarning = null;

    internal static OpenFileDialog CreateServicePackFileDialog() => new()
    {
        Title = "XML-Servicepacks auswählen", Filter = "XML-Servicepacks (*.xml)|*.xml", Multiselect = true,
        CheckFileExists = true
    };

    private async Task ChooseServicePacksAsync()
    {
        if (betaFeatures?.Enabled != true || busy || selfUpdating || loadingPackages || preparingDatabaseUpload) return;
        using var picker = CreateServicePackFileDialog();
        if (picker.ShowDialog(this) == DialogResult.OK) await ImportServicePackFilesAsync(picker.FileNames);
    }

    private async Task<ServicePackHostPlan[]> PrepareServicePackTargetsAsync(HostState[] selected, CancellationToken ct)
    {
        var plans = new List<ServicePackHostPlan>();
        foreach (HostState host in selected)
        {
            ct.ThrowIfCancellationRequested();
            IrisInventoryResult inventory;
            if (host.IsLocal) inventory = await readIrisInventory(ct);
            else
            {
                WorkerResult result = await runner.RunAsync(new WorkerRequest { Action = "database-inventory", Host = host.Host,
                    Username = txtUser.Text.Trim(), Password = txtPassword.Text, UseSsl = chkSsl.Checked, HttpsPort = (int)nHttpsPort.Value }, _ => { }, ct);
                if (!result.Success) throw new InvalidDataException(host.DisplayName + ": " + FriendlyError(result));
                inventory = new(result.IrisInstances.Select(instance => new IrisInstanceInfo(instance.Name, instance.Directory,
                    instance.ConfigurationFile, instance.Version, instance.Namespaces, instance.Error, instance.SuperServerPort, instance.ConnectionError)).ToArray(), []);
            }
            IrisUploadTarget[] targets = IrisUploadPlan.Create(inventory, chkIrisTls.Checked);
            if (targets.Select(target => target.Port).Distinct().Count() != targets.Length)
                throw new InvalidDataException(host.DisplayName + ": Mehrdeutige IRIS-Instanzen mit demselben SuperServer-Port.");
            plans.Add(new(host, targets));
        }
        return plans.ToArray();
    }

    private async Task ImportServicePackFilesAsync(string[] paths)
    {
        if (betaFeatures?.Enabled != true || busy || selfUpdating || loadingPackages || preparingDatabaseUpload || restartingAsAdministrator) return;
        HostState[] selected = SelectedHosts();
        if (selected.Length == 0 || !ValidateCredentials()) return;
        ServicePackSelection? selection = null;
        try
        {
            ServicePackHostPlan[] plans;
            using (var preparation = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token))
            {
                loadingPackages = true; packageCancellation = preparation; UpdateButtons();
                try
                {
                    selection = await Task.Run(() => ServicePackSelection.CreateAsync(paths, preparation.Token), preparation.Token);
                    plans = await PrepareServicePackTargetsAsync(selected, preparation.Token);
                    preparation.Token.ThrowIfCancellationRequested();
                }
                finally { loadingPackages = false; packageCancellation = null; if (!IsDisposed) UpdateButtons(); }
            }
            int namespaces = plans.Sum(plan => plan.Targets.Sum(target => target.Namespaces.Length));
            if (namespaces == 0) throw new InvalidDataException("Keine IRIS-Namespaces ohne %-Präfix auf den ausgewählten Hosts gefunden. Check Database prüfen.");
            ServicePackHostPlan[]? chosen;
            if (selectServicePackTargets is { } select) chosen = select(plans, selection.Files.Length);
            else
            {
                using var dialog = DatabaseTargetsDialog.ForServicePacks(plans, selection.Files.Length);
                chosen = dialog.ShowDialog(this) == DialogResult.OK ? dialog.GetSelectedPlans() : null;
            }
            if (chosen is null || chosen.Length == 0) return;
            plans = chosen;
            CaptureCredentials();
            await InBatchAsync(plans.Select(plan => plan.Host).ToArray(), async ct =>
            {
                foreach (ServicePackHostPlan plan in plans)
                {
                    WorkerResult result;
                    if (ct.IsCancellationRequested) result = new() { Success = false, Message = "Abgebrochen; Host wurde nicht gestartet." };
                    else if (plan.Targets.Length == 0) result = new() { Success = true, Skipped = true, Message = "Keine IRIS-Namespaces ohne %-Präfix; übersprungen." };
                    else
                    {
                        var request = new WorkerRequest { Action = "import-servicepacks", Host = plan.Host.Host, LocalExecution = plan.Host.IsLocal,
                            Username = plan.Host.IsLocal ? "" : activeUser, Password = plan.Host.IsLocal ? "" : activePassword,
                            UseSsl = !plan.Host.IsLocal && activeSsl, HttpsPort = activeHttpsPort,
                            RemoteInstallPath = plan.Host.IsLocal ? "" : activeRemoteInstallPath,
                            ServicePackFiles = plan.Host.IsLocal ? selection.LocalFiles : selection.Files,
                            DatabaseTargets = plan.Targets, IrisUseTls = activeIrisUseTls };
                        // Like installed-component updates, an accepted host finishes.
                        // Cancellation prevents the next host from starting.
                        result = await runner.RunAsync(request, progress =>
                        {
                            Log(plan.Host.Host, progress.Phase + ": " + progress.Message);
                            Mark(plan.Host, host => { host.Phase = progress.Phase + ": " + DisplayText.Clean(progress.Message); });
                        }, CancellationToken.None);
                    }
                    Log(plan.Host.Host, "Servicepacks: " + result.Message + (result.Error.Length > 0 ? " | " + result.Error : ""));
                    CountCompleted();
                }
            });
        }
        catch (OperationCanceledException) { Log("SYSTEM", "Servicepack-Vorbereitung abgebrochen."); }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            if (showServicePackWarning is { } show) show(ex.Message);
            else MessageBox.Show(this, ex.Message, "Servicepacks einspielen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            try { selection?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("SYSTEM", "Temporäre Servicepack-Dateien konnten nicht entfernt werden: " + ex.Message); }
        }
    }

}
