using System.Security.Cryptography;
using System.Text.Json;

namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer configSaveTimer = new() { Interval = 350 };
    private readonly HashSet<string> pendingConfigFields = [];
    private readonly Label configSaveStatus = new() { Name = "configSaveStatus", AutoSize = true, Visible = false, Margin = new Padding(0, 4, 0, 5) };
    private bool autoSaveEnabled;
    private bool applyingConfiguration;

    private void ConfigureAutoSave(bool enabled)
    {
        void Text(TextBox control, string field)
        {
            control.TextChanged += (_, _) => QueueConfigSave(field, defer: true);
            control.Leave += (_, _) => FlushConfigChanges();
        }
        Text(txtUser, nameof(UpdaterConfig.Username));
        Text(txtPassword, nameof(UpdaterConfig.EncryptedPassword));
        Text(txtRemotePath, nameof(UpdaterConfig.RemotePath));
        Text(softwarePath, nameof(UpdaterConfig.SoftwarePath));
        foreach (var (control, field) in new[]
        {
            (chkSsl, nameof(UpdaterConfig.UseSsl)), (chkIrisTls, nameof(UpdaterConfig.IrisUseTls)),
            (chkCompileAfterInstallation, nameof(UpdaterConfig.CompileAfterInstallation)), (chkUploadToZzzSoftware, nameof(UpdaterConfig.UploadToZzzSoftware)),
            (chkRestartPosAfterUpdate, nameof(UpdaterConfig.RestartPosAfterUpdate)), (chkScanOnStartup, nameof(UpdaterConfig.ScanOnStartup)),
            (chkSkipLocalPackageCopy, nameof(UpdaterConfig.SkipLocalPackageCopy)), (chkSkipRemotePackageCopy, nameof(UpdaterConfig.SkipRemotePackageCopy)),
            (chkSkipZipTransfer, nameof(UpdaterConfig.SkipZipTransfer)), (chkForceUpdate, nameof(UpdaterConfig.ForceUpdate))
        }) control.CheckedChanged += (_, _) => QueueConfigSave(field);
        foreach (var (control, field) in new[]
        {
            (nHttpsPort, nameof(UpdaterConfig.HttpsPort)), (nScan, nameof(UpdaterConfig.ScanParallelism)), (nUpdate, nameof(UpdaterConfig.UpdateParallelism))
        }) control.ValueChanged += (_, _) => QueueConfigSave(field);
        executionMode.SelectedIndexChanged += (_, _) => QueueConfigSave(nameof(UpdaterConfig.ExecutionMode));
        useBuild.SelectedIndexChanged += (_, _) => QueueConfigSave(nameof(UpdaterConfig.UseBuild));
        configSaveTimer.Tick += (_, _) => FlushConfigChanges();
        configSaveStatus.Cursor = Cursors.Hand;
        configSaveStatus.Click += (_, _) => FlushConfigChanges();
        Disposed += (_, _) => configSaveTimer.Dispose();
        configSaveStatus.ForeColor = UiTheme.Muted;
        autoSaveEnabled = enabled;
    }

    private void QueueConfigSave(string field, bool defer = false)
    {
        if (!autoSaveEnabled || applyingConfiguration || Disposing || IsDisposed) return;
        pendingConfigFields.Add(field);
        configSaveTimer.Stop();
        if (defer)
        {
            configSaveStatus.Text = "Änderungen werden gespeichert …";
            configSaveStatus.ForeColor = UiTheme.Muted;
            configSaveTimer.Start();
        }
        else FlushConfigChanges();
    }

    private bool FlushConfigChanges()
    {
        configSaveTimer.Stop();
        if (!autoSaveEnabled || applyingConfiguration || pendingConfigFields.Count == 0) return true;
        try
        {
            Directory.CreateDirectory(userDataPaths.RootDirectory);
            bool passwordChanged = pendingConfigFields.Contains(nameof(UpdaterConfig.EncryptedPassword));
            AppConfigStore.SaveChangedSettings(userDataPaths.ConfigFile, CaptureConfig(),
                pendingConfigFields.Where(field => field != nameof(UpdaterConfig.EncryptedPassword)),
                passwordChanged ? txtPassword.Text : null, startupConfigPath);
            pendingConfigFields.Clear();
            configSaveStatus.Text = "Änderungen automatisch gespeichert.";
            configSaveStatus.ForeColor = UiTheme.Muted;
            configSaveStatus.Visible = false;
            updateToolTip.SetToolTip(configSaveStatus, userDataPaths.ConfigFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidOperationException)
        {
            configSaveStatus.Text = "Konfiguration nicht gespeichert – zum erneuten Speichern hier klicken.";
            configSaveStatus.ForeColor = UiTheme.Danger;
            configSaveStatus.Visible = true;
            updateToolTip.SetToolTip(configSaveStatus, "Speichern fehlgeschlagen; Änderungen bleiben ausstehend.\n" + ex.Message);
            Log("SYSTEM", "Automatisches Speichern der Konfiguration fehlgeschlagen: " + ex.Message);
            return false;
        }
    }
}
