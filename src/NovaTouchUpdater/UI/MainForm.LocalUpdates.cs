namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly ComboBox executionMode = new() { Name = "executionMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox softwarePath = new() { Name = "softwarePath", Dock = DockStyle.Fill, PlaceholderText = "Ordner mit allen Update-Paketen" };
    private readonly Label packageStatus = new() { Name = "packageStatus", AutoSize = true, Dock = DockStyle.Fill, Text = "ZIP- und MSI-Pakete automatisch aus dem Softwareordner laden." };
    private readonly Button browseSoftware = Button("Durchsuchen");
    private readonly Button readPackages = Button("Pakete einlesen");
    private readonly SoftwarePackageCatalog packageCatalog = new();
    private readonly Dictionary<UpdateComponents, MsiPackageRequest> msiPackages = [];
    private readonly Dictionary<UpdateComponents, (Button Button, Label Label)> msiControls = [];
    private CancellationTokenSource? packageCancellation;
    private bool loadingPackages;
    private UpdateExecutionMode Mode => executionMode.SelectedIndex is >= 0 and <= 2 ? (UpdateExecutionMode)executionMode.SelectedIndex : UpdateExecutionMode.Remote;
    private IEnumerable<HostState> ExecutionHosts() => hosts.Where(h => Mode == UpdateExecutionMode.LocalAndRemote || (Mode == UpdateExecutionMode.Local ? h.IsLocal : !h.IsLocal));
    private HostState[] SelectedRemoteHosts() => SelectedHosts().Where(h => !h.IsLocal).ToArray();

    private Control CreateLocalUpdateCard()
    {
        browseSoftware.Name = "browseSoftware"; readPackages.Name = "readPackages";
        var selector = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selector.Controls.Add(softwarePath, 0, 0);
        selector.Controls.Add(FlowRow(browseSoftware, readPackages), 1, 0);
        var card = Section("Lokales Update", Stack(new Label
        {
            Text = "Softwareordner mit ZIP- und MSI-Paketen:", AutoSize = true, Dock = DockStyle.Fill
        }, selector, packageStatus));
        card.Name = "localUpdateCard";
        browseSoftware.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Ordner mit der vollständigen NovaTouch-Software auswählen", UseDescriptionForTitle = true };
            if (Directory.Exists(softwarePath.Text)) dialog.SelectedPath = softwarePath.Text;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            softwarePath.Text = dialog.SelectedPath;
            await LoadSoftwarePackagesAsync();
        };
        readPackages.Click += async (_, _) => await LoadSoftwarePackagesAsync();
        softwarePath.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; await LoadSoftwarePackagesAsync(); } };
        return card;
    }

    private Control CreateMsiPackageRows()
    {
        var rows = new List<Control>();
        foreach (UpdateComponents component in HostUpdateState.Each(UpdateComponents.Msi))
        {
            string shortName = component.ToString().ToUpperInvariant();
            var button = Button(shortName + "-Paket wählen");
            button.Name = "choose" + shortName;
            var label = new Label { Text = shortName + " Target: –" };
            msiControls.Add(component, (button, label));
            button.Click += async (_, _) =>
            {
                using var dialog = new OpenFileDialog { Filter = "MSI oder ZIP (*.msi;*.zip)|*.msi;*.zip", Title = HostUpdateState.Label(component) + " auswählen" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                loadingPackages = true;
                using var ct = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
                packageCancellation = ct;
                UpdateButtons();
                try
                {
                    var packages = await Task.Run(() => packageCatalog.ReadFileAsync(dialog.FileName, ct.Token), ct.Token);
                    SoftwarePackage? package = packages.Where(p => p.Component == component).OrderByDescending(p => SoftwarePackageCatalog.NumericVersion(p.Version)).FirstOrDefault();
                    if (package is null) throw new InvalidDataException("Das Paket enthält keine MSI für " + HostUpdateState.Label(component));
                    ApplyPackage(package); UpdateButtons();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update-Paket", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { packageCancellation = null; loadingPackages = false; if (!IsDisposed) UpdateButtons(); }
            };
            rows.Add(PanelRow(button, label));
        }
        return Stack(rows.ToArray());
    }

    private Control CreatePackageSelectionGrid()
    {
        var services = Stack(PanelRow(btnPayment, lblPayment), PanelRow(btnFiscal, lblFiscal), PanelRow(btnServerSuite, lblServerSuite));
        Control msi = CreateMsiPackageRows();
        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(services, 0, 0); grid.Controls.Add(msi, 1, 0);
        bool stacked = false;
        grid.Layout += (_, _) =>
        {
            bool narrow = grid.ClientSize.Width < 720 * grid.DeviceDpi / 96F * grid.Font.SizeInPoints / 9.5F;
            if (narrow == stacked) return;
            stacked = narrow; grid.SuspendLayout();
            grid.ColumnCount = narrow ? 1 : 2; grid.RowCount = narrow ? 2 : 1;
            grid.ColumnStyles.Clear(); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, narrow ? 100 : 50));
            if (!narrow) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Clear(); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (narrow) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.SetCellPosition(msi, narrow ? new TableLayoutPanelCellPosition(0, 1) : new TableLayoutPanelCellPosition(1, 0));
            grid.ResumeLayout(true);
        };
        return grid;
    }

    private void ApplyPackage(SoftwarePackage package)
    {
        switch (package.Component)
        {
            case UpdateComponents.Payment: paymentZip = package.Path; paymentVersion = package.Version; lblPayment.Text = "Payment Target: " + package.Version; break;
            case UpdateComponents.Fiscal: fiscalZip = package.Path; fiscalVersion = package.Version; lblFiscal.Text = "Fiscal Target: " + package.Version; break;
            case UpdateComponents.ServerSuite: serverSuiteZip = package.Path; serverSuiteVersion = package.Version; lblServerSuite.Text = "Server Suite Target: " + package.Version; break;
            default:
                if (package.Msi is null) throw new InvalidDataException("MSI-Paketinformationen fehlen.");
                msiPackages[package.Component] = package.Msi;
                msiControls[package.Component].Label.Text = package.Component.ToString().ToUpperInvariant() + " Target: " + package.Version;
                break;
        }
    }

    private async Task LoadSoftwarePackagesAsync()
    {
        if (busy || selfUpdating || loadingPackages || string.IsNullOrWhiteSpace(softwarePath.Text)) return;
        loadingPackages = true;
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        packageCancellation = ct; UpdateButtons();
        try
        {
            packageStatus.Text = "Softwarepakete werden gelesen …";
            string folder = softwarePath.Text.Trim();
            SoftwareCatalogResult catalog = await Task.Run(() => packageCatalog.ReadAsync(folder, null, ct.Token), ct.Token);
            ct.Token.ThrowIfCancellationRequested();
            paymentZip = fiscalZip = serverSuiteZip = paymentVersion = fiscalVersion = serverSuiteVersion = "";
            lblPayment.Text = "Payment Target: –"; lblFiscal.Text = "Fiscal Target: –"; lblServerSuite.Text = "Server Suite Target: –";
            msiPackages.Clear();
            foreach (var (component, controls) in msiControls) controls.Label.Text = component.ToString().ToUpperInvariant() + " Target: –";
            foreach (SoftwarePackage package in catalog.Packages) ApplyPackage(package);
            packageStatus.Text = $"{catalog.Packages.Length} Komponenten gefunden" + (catalog.Warnings.Length == 0 ? "." : $" · {catalog.Warnings.Length} Pakete nicht lesbar (Details im Protokoll).");
            foreach (string warning in catalog.Warnings) Log("SYSTEM", "PAKETE: " + warning);
        }
        catch (OperationCanceledException) { packageStatus.Text = "Paketsuche abgebrochen."; }
        catch (Exception ex) { packageStatus.Text = "Pakete konnten nicht gelesen werden: " + ex.Message; Log("SYSTEM", packageStatus.Text); }
        finally { packageCancellation = null; loadingPackages = false; if (!IsDisposed) UpdateButtons(); }
    }

    private void UpdateExecutionSelection()
    {
        if (busy || selfUpdating || loadingPackages) return;
        if (Mode != UpdateExecutionMode.Remote && !hosts.Any(h => h.IsLocal))
            hosts.Insert(0, new HostState { Host = "LOCALHOST", Connection = "Lokal", Phase = "Lokaler Rechner – ohne WinRM" });
        InvalidateConnectionChecks(); RebuildRows(); UpdateSummary(); UpdateButtons();
    }

    private static void ReadMsiInventory(HostState host, WorkerResult result)
    {
        host.PosVersion = string.IsNullOrWhiteSpace(result.PosVersion) ? "–" : DisplayText.Clean(result.PosVersion);
        host.NtcVersion = string.IsNullOrWhiteSpace(result.NtcVersion) ? "–" : DisplayText.Clean(result.NtcVersion);
        host.InstalledPrograms = result.InstalledPrograms.ToList();
        host.RebootRequired |= result.RebootRequired;
    }

    private async Task HashMsiPackagesAsync(UpdateComponents needed, CancellationToken ct)
    {
        foreach (var (component, package) in msiPackages.Where(pair => (pair.Key & needed) != 0))
        {
            package.Sha256 = await WorkerRunner.Sha256Async(package.PayloadPath, ct);
            foreach (var file in package.CompanionFiles) file.Sha256 = await WorkerRunner.Sha256Async(file.LocalPath, ct);
            Log("SYSTEM", $"MSI {component}: {package.ProductName} {package.ProductVersion}");
        }
    }

    private string UpdateConfirmation(HostState[] selected, UpdateComponents needed, bool onlyFailed)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("Ausführung: " + executionMode.Text);
        int remoteCount = selected.Count(h => !h.IsLocal);
        text.AppendLine("Ziele: " + (selected.Any(h => h.IsLocal) ? "LOCALHOST" + (remoteCount > 0 ? $" und {remoteCount} Remote-Host(s)" : "") : $"{remoteCount} Remote-Host(s)"));
        text.AppendLine("\nGeplante Komponenten:");
        foreach (UpdateComponents component in HostUpdateState.Each(needed))
        {
            string version = component switch
            {
                UpdateComponents.Payment => paymentVersion, UpdateComponents.Fiscal => fiscalVersion,
                UpdateComponents.ServerSuite => serverSuiteVersion, _ => msiPackages[component].ProductVersion
            };
            text.AppendLine($"{HostUpdateState.Label(component)} {version}: {selected.Count(h => (ComponentsForUpdate(h, onlyFailed) & component) != 0)} Host(s)");
        }
        if (onlyFailed) text.AppendLine("\nBereits verifizierte Komponenten werden nicht wiederholt.");
        text.AppendLine("\nNur bereits installierte Komponenten werden aktualisiert.");
        if ((needed & UpdateComponents.ZipServices) != 0)
            text.AppendLine("ZIPs: " + (chkForceUpdate.Checked ? "Force Up-Downgrade" : "Gleiche Version überspringen") + ". Zielordner und zusätzliche Client-Dateien bleiben erhalten.");
        if ((needed & UpdateComponents.ServerSuite) != 0) text.AppendLine("Server Suite: alle Instanzen, einschließlich portable_python. Deaktivierte Dienste bleiben deaktiviert.");
        if ((needed & UpdateComponents.Msi) != 0) text.AppendLine("MSIs: gleiche oder neuere installierte Versionen immer überspringen. Stille Installation ohne automatischen Windows-Neustart.");
        if (selected.Any(h => h.IsLocal)) text.AppendLine("Lokaler Zielpfad: " + txtRemotePath.Text.Trim() + "\nLokal: direkter Zugriff ohne WinRM. Installationen benötigen Administratorrechte.");
        if (selected.Any(h => !h.IsLocal)) text.AppendLine("Remote-Zielpfad: " + txtRemotePath.Text.Trim() + "\nPaketübertragung: " + (chkSkipZipTransfer.Checked ? "deaktiviert; vorhandene Pakete prüfen" : "aktiv"));
        return text.ToString();
    }
}
