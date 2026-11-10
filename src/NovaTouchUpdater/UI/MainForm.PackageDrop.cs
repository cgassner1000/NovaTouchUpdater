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
            ? paths.Where(SoftwarePackageCatalog.IsPackageFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];

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
        string[] files = paths.Where(SoftwarePackageCatalog.IsPackageFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        UpdateComponents components = UpdateComponents.All;
        for (Control? parent = surface; parent is not null; parent = parent.Parent)
            if (parent.Tag is UpdateComponents component) { components = component; break; }
        packageDropTask = LoadDroppedPackagesAsync(files, components);
    }

    private async Task LoadDroppedPackagesAsync(string[] paths, UpdateComponents components)
    {
        if (!CanDropPackages) return;
        loadingPackages = true;
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        packageCancellation = ct;
        UpdateButtons();
        try
        {
            SoftwareCatalogResult result = await Task.Run(() => packageCatalog.ReadFilesAsync(paths, ct.Token), ct.Token);
            ct.Token.ThrowIfCancellationRequested();
            foreach (string warning in result.Warnings) Log("SYSTEM", "PAKETE: " + warning);
            SoftwarePackage[] packages = result.Packages.Where(package => (package.Component & components) != 0).ToArray();
            if (packages.Length == 0)
                throw new InvalidDataException(components == UpdateComponents.All ? "Keine unterstützten Update-Pakete in den abgelegten Dateien gefunden." :
                    "Die abgelegten Dateien enthalten kein Update-Paket für " + HostUpdateState.Label(components) + ".");
            foreach (SoftwarePackage package in packages) SelectIndividualPackage(package);
            packageStatus.Text = $"{packages.Length} Komponente(n) per DragDrop hinzugefügt." +
                (result.Warnings.Length == 0 ? "" : $" {result.Warnings.Length} Datei(en) nicht lesbar (Details im Protokoll).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Disposing || IsDisposed) return;
            if (showPackageDropWarning is { } show) show(ex.Message);
            else ShowIndividualPackageDialog(() => MessageBox.Show(this, ex.Message, "Update-Pakete", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        finally { packageCancellation = null; loadingPackages = false; if (!Disposing && !IsDisposed) UpdateButtons(); }
    }
}
