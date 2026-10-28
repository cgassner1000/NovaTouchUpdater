namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly ComboBox executionMode = new() { Name = "executionMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox softwarePath = new() { Name = "softwarePath", Dock = DockStyle.Fill, PlaceholderText = "Paketordner (inkl. Unterordner)" };
    private readonly Label packageStatus = new() { Name = "packageStatus", AutoSize = true, Dock = DockStyle.Fill, Text = "Pakete einlesen. Lokale Paketablage erst mit Updates starten." };
    private readonly Button browseSoftware = Button("Durchsuchen");
    private readonly Button readPackages = Button("Pakete einlesen");
    private readonly Button useFolderPackages = Button("Ordnerauswahl verwenden");
    private readonly Label selectedPackagesList = new()
    {
        Name = "selectedPackagesList", AutoSize = true, Dock = DockStyle.Fill,
        Text = "Keine Pakete ausgewählt.", Margin = Padding.Empty
    };
    private SoftwarePackage[] folderPackages = [];
    private bool individualPackageSelection;
    private readonly SoftwarePackageCatalog packageCatalog = new();
    private readonly Dictionary<UpdateComponents, MsiPackageRequest> msiPackages = [];
    private readonly Dictionary<UpdateComponents, (Button Button, Label Label)> msiControls = [];
    private CancellationTokenSource? packageCancellation;
    private bool loadingPackages;
    private ExpandableSection? individualFilesSection;

    private DialogResult ShowIndividualPackageDialog(Func<DialogResult> showDialog)
    {
        using var keepOpen = individualFilesSection?.SuspendDismissal();
        return showDialog();
    }
    private UpdateExecutionMode Mode => executionMode.SelectedIndex is >= 0 and <= 2 ? (UpdateExecutionMode)executionMode.SelectedIndex : UpdateExecutionMode.Remote;
    private bool LocalFallback => Mode == UpdateExecutionMode.Local || !hosts.Any(h => !h.IsLocal && h.IsSelected);
    private bool IncludeLocalHost => Mode != UpdateExecutionMode.Remote || LocalFallback;
    private bool localHostInTable;
    private bool hostRowsRebuildQueued;
    private IEnumerable<HostState> ExecutionHosts() => hosts.Where(h => h.IsLocal ? IncludeLocalHost : Mode != UpdateExecutionMode.Local);
    private HostState[] SelectedRemoteHosts() => SelectedHosts().Where(h => !h.IsLocal).ToArray();
    private bool UseLocalPackageSources => chkSkipLocalPackageCopy.Checked || string.IsNullOrWhiteSpace(txtRemotePath.Text);
    private string RemoteDestinationPath() => string.IsNullOrWhiteSpace(txtRemotePath.Text) ? DefaultRemoteInstallPath : txtRemotePath.Text.Trim();
    private bool IsUpdatePathValid(HostState[] selected) =>
        !selected.Any(h => !h.IsLocal || !UseLocalPackageSources) ||
        System.Text.RegularExpressions.Regex.IsMatch(RemoteDestinationPath(), @"^[A-Za-z]:\\");

    private void EnsureLocalHost()
    {
        if (!IncludeLocalHost) return;
        HostState? local = hosts.FirstOrDefault(h => h.IsLocal);
        if (local is null)
        {
            local = new HostState { Host = "LOCALHOST", Connection = "Lokal", Phase = "Lokaler Rechner – ohne WinRM" };
            hosts.Insert(0, local);
        }
        if (LocalFallback)
        {
            local.IsSelected = true;
            if (local.Row is not null) local.Row.Cells["include"].Value = true;
        }
    }

    private void QueueHostRowsRebuild()
    {
        // Checkbox commits must finish before changing the rows of the grid.
        if (!IsHandleCreated) { RebuildRows(); return; }
        if (hostRowsRebuildQueued) return;
        hostRowsRebuildQueued = true;
        BeginInvoke(() =>
        {
            hostRowsRebuildQueued = false;
            if (IsDisposed) return;
            RebuildRows(); UpdateSummary(); UpdateButtons();
        });
    }

    private Control CreateLocalUpdateCard()
    {
        browseSoftware.Name = "browseSoftware"; readPackages.Name = "readPackages";
        var selector = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selector.Controls.Add(softwarePath, 0, 0);
        selector.Controls.Add(FlowRow(browseSoftware, readPackages), 1, 0);
        var card = Section("Installations-Pakete", Stack(new Label
        {
            Text = "Softwareordner mit ZIP- und MSI-Paketen (inkl. Unterordner):", AutoSize = true, Dock = DockStyle.Fill
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
                if (ShowIndividualPackageDialog(() => dialog.ShowDialog(this)) != DialogResult.OK) return;
                loadingPackages = true;
                using var ct = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
                packageCancellation = ct;
                UpdateButtons();
                try
                {
                    var packages = await Task.Run(() => packageCatalog.ReadFileAsync(dialog.FileName, ct.Token), ct.Token);
                    ct.Token.ThrowIfCancellationRequested();
                    SoftwarePackage? package = packages.Where(p => p.Component == component).OrderByDescending(p => SoftwarePackageCatalog.NumericVersion(p.Version)).FirstOrDefault();
                    if (package is null) throw new InvalidDataException("Das Paket enthält keine MSI für " + HostUpdateState.Label(component));
                    SelectIndividualPackage(package);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ShowIndividualPackageDialog(() => MessageBox.Show(this, ex.Message, "Update-Paket", MessageBoxButtons.OK, MessageBoxIcon.Warning)); }
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

    private void ClearPackageSelection()
    {
        paymentZip = fiscalZip = serverSuiteZip = paymentVersion = fiscalVersion = serverSuiteVersion = "";
        paymentHash = fiscalHash = serverSuiteHash = "";
        lblPayment.Text = "Payment Target: –"; lblFiscal.Text = "Fiscal Target: –"; lblServerSuite.Text = "Server Suite Target: –";
        msiPackages.Clear();
        foreach (var (component, controls) in msiControls) controls.Label.Text = component.ToString().ToUpperInvariant() + " Target: –";
    }

    private void SelectIndividualPackage(SoftwarePackage package)
    {
        if ((package.Component & UpdateComponents.Msi) != 0 && package.Msi is null)
            throw new InvalidDataException("MSI-Paketinformationen fehlen.");
        if (!individualPackageSelection) ClearPackageSelection();
        individualPackageSelection = true;
        ApplyPackage(package);
        RefreshSelectedPackages();
        UpdateButtons();
    }

    private void UseFolderPackages()
    {
        ClearPackageSelection();
        individualPackageSelection = false;
        foreach (var package in folderPackages) ApplyPackage(package);
        RefreshSelectedPackages();
        UpdateButtons();
    }

    private void RefreshSelectedPackages()
    {
        var packages = new List<SoftwarePackage>();
        if (paymentZip.Length > 0) packages.Add(new(UpdateComponents.Payment, paymentZip, paymentVersion));
        if (fiscalZip.Length > 0) packages.Add(new(UpdateComponents.Fiscal, fiscalZip, fiscalVersion));
        if (serverSuiteZip.Length > 0) packages.Add(new(UpdateComponents.ServerSuite, serverSuiteZip, serverSuiteVersion));
        packages.AddRange(msiPackages.Select(pair => new SoftwarePackage(pair.Key, pair.Value.PayloadPath, pair.Value.ProductVersion)));
        selectedPackagesList.Text = packages.Count == 0 ? "Keine Pakete ausgewählt." :
            string.Join(Environment.NewLine, packages.OrderBy(package => package.Component).Select(package =>
                $"{HostUpdateState.Label(package.Component)} · {package.Version} · {Path.GetFileName(package.Path)}"));
        selectedPackagesList.AccessibleDescription = individualPackageSelection ? "Einzeln ausgewählte Pakete" : "Pakete aus dem Softwareordner";
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
            folderPackages = catalog.Packages;
            if (!individualPackageSelection) UseFolderPackages();
            packageStatus.Text = $"{catalog.Packages.Length} Komponenten gefunden · " +
                (individualPackageSelection ? "Einzelauswahl hat Vorrang" : "Lokale Paketablage mit Updates starten") +
                (catalog.Warnings.Length == 0 ? "." : $" · {catalog.Warnings.Length} Pakete nicht lesbar (Details im Protokoll).");
            foreach (string warning in catalog.Warnings) Log("SYSTEM", "PAKETE: " + warning);
        }
        catch (OperationCanceledException) { packageStatus.Text = "Paketsuche abgebrochen."; }
        catch (Exception ex) { packageStatus.Text = "Pakete konnten nicht gelesen werden: " + ex.Message; Log("SYSTEM", packageStatus.Text); }
        finally { packageCancellation = null; loadingPackages = false; if (!IsDisposed) UpdateButtons(); }
    }

    private void UpdateExecutionSelection()
    {
        if (busy || selfUpdating || loadingPackages) return;
        InvalidateConnectionChecks(); RebuildRows(); UpdateSummary(); UpdateButtons();
    }

    private static void ReadMsiInventory(HostState host, WorkerResult result)
    {
        host.PosVersion = string.IsNullOrWhiteSpace(result.PosVersion) ? "–" : DisplayText.Clean(result.PosVersion);
        host.NtcVersion = string.IsNullOrWhiteSpace(result.NtcVersion) ? "–" : DisplayText.Clean(result.NtcVersion);
        host.InstalledPrograms = result.InstalledPrograms.ToList();
        host.RebootRequired |= result.RebootRequired;
    }

    private string UpdateConfirmation(HostState[] selected, UpdateComponents needed, bool onlyFailed)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("Ausführung: " + (selected.All(h => h.IsLocal) ? "Lokal" : executionMode.Text));
        int remoteCount = selected.Count(h => !h.IsLocal);
        text.AppendLine("Ziele: " + (selected.Any(h => h.IsLocal) ? "LOKAL" + (remoteCount > 0 ? $" und {remoteCount} Remote-Host(s)" : "") : $"{remoteCount} Remote-Host(s)"));
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
            text.AppendLine("ZIPs: " + (chkForceUpdate.Checked ? "Force Up-Downgrade" : "Gleiche Version überspringen") + ". Payment/Fiscal: zusätzliche Client-Dateien bleiben erhalten.");
        if ((needed & UpdateComponents.ServerSuite) != 0) text.AppendLine("Server Suite: alle Instanzen. Inhalt von portable_python vor dem Kopieren löschen; vorhandene nt3_server_suite*.exe bleiben unverändert erhalten, auch in Unterordnern. EventLog bleibt während der Installation gestoppt. Deaktivierte Dienste bleiben deaktiviert.");
        if ((needed & UpdateComponents.Msi) != 0) text.AppendLine("MSIs nur LOKAL: gleiche oder neuere installierte Versionen immer überspringen. Stille Installation ohne automatischen Windows-Neustart.");
        if (selected.Any(h => h.IsLocal)) text.AppendLine((UseLocalPackageSources
            ? "LOKAL: Gewählte Paketquellen direkt verwenden; keine zusätzliche Kopie in den Zielpfad. ZIPs werden temporär entpackt."
            : "Lokaler Zielpfad: " + txtRemotePath.Text.Trim() + "\nLOKAL: alle gewählten Pakete bereitstellen.") +
            "\nNur installierte Komponenten aktualisieren. Direkter Zugriff ohne WinRM. Installationen benötigen Administratorrechte.");
        if (selected.Any(h => !h.IsLocal)) text.AppendLine("REMOTE: ausschließlich Payment, Fiscal und Server Suite. POS, NTC, SOL und NTV werden remote nicht installiert.\nRemote-Zielpfad: " + RemoteDestinationPath() + "\nPaketübertragung: " + (chkSkipZipTransfer.Checked ? "deaktiviert; vorhandene Pakete prüfen" : "aktiv"));
        return text.ToString();
    }
}
