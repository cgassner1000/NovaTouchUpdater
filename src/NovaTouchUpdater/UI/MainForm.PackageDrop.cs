namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private Task packageDropTask = Task.CompletedTask;
    private Action<string>? showPackageDropWarning = null;
    private readonly List<ShellFileDropTarget> shellPackageDrops = [];

    private bool CanDropPackages => !Disposing && !IsDisposed && !connectionClosing && !busy && !selfUpdating && !loadingPackages &&
        !preparingDatabaseUpload && !restartingAsAdministrator;

    private static string[] DroppedPackageFiles(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop, false) is string[] paths
            ? paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];

    private void EnablePackageDrop(Control surface)
    {
        bool elevated = AppElevation.IsProcessElevated();
        surface.AllowDrop = !elevated;
        if (elevated)
            shellPackageDrops.Add(new ShellFileDropTarget(surface, files => AcceptPackageDrop(surface, files),
                message => { packageStatus.Text = message; Log("SYSTEM", message); }));
        surface.DragEnter += (_, e) => e.Effect = CanDropPackages && (e.AllowedEffect & DragDropEffects.Copy) != 0 &&
            DroppedPackageFiles(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        surface.DragDrop += (_, e) =>
        {
            e.Effect = DragDropEffects.None;
            if (!CanDropPackages || (e.AllowedEffect & DragDropEffects.Copy) == 0) return;
            string[] files = DroppedPackageFiles(e.Data);
            if (files.Length == 0) return;
            e.Effect = DragDropEffects.Copy;
            AcceptPackageDrop(surface, files);
        };
        foreach (Control child in surface.Controls) EnablePackageDrop(child);
    }

    private void AcceptPackageDrop(Control surface, string[] paths)
    {
        if (!CanDropPackages) return;
        string[] files = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        packageDropTask = LoadPackageFilesAsync(files);
    }

    private async Task LoadPackageFilesAsync(string[] paths)
    {
        if (!CanDropPackages) return;
        loadingPackages = true;
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        packageCancellation = ct;
        UpdateButtons();
        try
        {
            SoftwareDropResult result = await Task.Run(() => packageCatalog.ReadFilesAsync(paths, ct.Token), ct.Token);
            ct.Token.ThrowIfCancellationRequested();
            foreach (string warning in result.Warnings) Log("SYSTEM", "PAKETE: " + warning);
            SoftwarePackage[] packages = result.Packages;
            if (packages.Length == 0 && result.AdditionalFiles.Length == 0)
            {
                if (result.Rejections.Length == 0)
                    throw new InvalidDataException("Keine lesbaren Dateien zum Hinzufügen gefunden. Ordner werden nicht übernommen.");
                packageStatus.Text = "Keine Dateien hinzugefügt. Für nt.payment und nt.fiscal werden ZIP-Pakete benötigt.";
                ShowPackageWarning(string.Join(Environment.NewLine, result.Rejections));
                return;
            }
            foreach (SoftwarePackage package in packages) SelectIndividualPackage(package);
            AddAdditionalFiles(result.AdditionalFiles);
            packageStatus.Text = $"{packages.Length} Paket(e), {result.AdditionalFiles.Length} zusätzliche Datei(en) hinzugefügt." +
                (result.Warnings.Length == 0 ? "" : $" {result.Warnings.Length} Hinweis(e) (Details im Protokoll).");
            if (result.Rejections.Length > 0) ShowPackageWarning(string.Join(Environment.NewLine, result.Rejections));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Disposing || IsDisposed) return;
            ShowPackageWarning(ex.Message);
        }
        finally { packageCancellation = null; loadingPackages = false; if (!Disposing && !IsDisposed) UpdateButtons(); }
    }

    private void ShowPackageWarning(string message)
    {
        if (showPackageDropWarning is { } show) show(message);
        else ShowPackageDialog(() => AppMessageBox.Show(this, message, "Update-Pakete", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }
}
