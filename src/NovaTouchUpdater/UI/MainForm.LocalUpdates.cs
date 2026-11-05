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
    private readonly TableLayoutPanel selectedPackagesTable = new()
    {
        Name = "selectedPackagesTable", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top, ColumnCount = 3, Margin = Padding.Empty, Visible = false
    };
    private SoftwarePackage[] folderPackages = [];
    private bool individualPackageSelection;
    private readonly SoftwarePackageCatalog packageCatalog = new();
    private readonly Dictionary<UpdateComponents, MsiPackageRequest> msiPackages = [];
    private readonly Dictionary<UpdateComponents, (Button Button, Label Label)> msiControls = [];
    private readonly Dictionary<UpdateComponents, Button> removePackageButtons = [];
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
        !selected.Any(h => h.IsLocal ? !UseLocalPackageSources : !chkSkipRemotePackageCopy.Checked) ||
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
            Text = "Softwareordner mit Setup-, ZIP- und MSI-Paketen (inkl. Unterordner):", AutoSize = true, Dock = DockStyle.Fill
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
            rows.Add(CreateIndividualPackageRow(component, button, label));
        }
        return Stack(rows.ToArray());
    }

    private Control CreatePackageSelectionGrid()
    {
        var services = Stack(CreateIndividualPackageRow(UpdateComponents.Payment, btnPayment, lblPayment),
            CreateIndividualPackageRow(UpdateComponents.Fiscal, btnFiscal, lblFiscal),
            CreateIndividualPackageRow(UpdateComponents.ServerSuite, btnServerSuite, lblServerSuite));
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

    private PackageRemoveButton CreateRemovePackageButton(UpdateComponents component)
    {
        var remove = new PackageRemoveButton
        {
            Name = "remove" + component + "Package", Text = "×", Tag = component,
            AccessibleName = HostUpdateState.Label(component) + " aus der Auswahl entfernen",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold), Cursor = Cursors.Hand,
            ForeColor = UiTheme.Danger, BackColor = UiTheme.DangerSoft,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(6, 4, 0, 4), Padding = new Padding(3, 0, 3, 0),
            Anchor = AnchorStyles.Right
        };
        remove.FlatAppearance.BorderSize = 0;
        updateToolTip.SetToolTip(remove, "Paket aus der Auswahl entfernen. Die Datei bleibt auf der Festplatte erhalten.");
        remove.Click += (_, _) => RemoveSelectedPackage(component);
        return remove;
    }

    private Control CreateIndividualPackageRow(UpdateComponents component, Button choose, Label version)
    {
        var remove = CreateRemovePackageButton(component);
        remove.Visible = false;
        removePackageButtons.Add(component, remove);
        var row = (TableLayoutPanel)PanelRow(choose, version);
        row.ColumnCount = 3;
        row.ColumnStyles[1].SizeType = SizeType.AutoSize;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.SetCellPosition(version, new TableLayoutPanelCellPosition(2, 0));
        row.Controls.Add(remove, 1, 0);
        return row;
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
                foreach (HostState host in hosts) if (host.Updates is { } state) state.ResetDatabaseUpload();
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
        foreach (HostState host in hosts) if (host.Updates is { } state) state.ResetDatabaseUpload();
        foreach (var (component, controls) in msiControls) controls.Label.Text = component.ToString().ToUpperInvariant() + " Target: –";
    }

    private void SelectIndividualPackage(SoftwarePackage package)
    {
        if ((package.Component & UpdateComponents.Msi) != 0 && package.Msi is null)
            throw new InvalidDataException("MSI-Paketinformationen fehlen.");
        individualPackageSelection = true;
        ApplyPackage(package);
        RefreshSelectedPackages();
        UpdateButtons();
    }

    private void RemoveSelectedPackage(UpdateComponents component)
    {
        if (busy || selfUpdating || loadingPackages || preparingDatabaseUpload || restartingAsAdministrator ||
            (SelectedUpdateComponents() & component) == UpdateComponents.None) return;
        switch (component)
        {
            case UpdateComponents.Payment:
                paymentZip = paymentVersion = paymentHash = "";
                lblPayment.Text = "Payment Target: –";
                break;
            case UpdateComponents.Fiscal:
                fiscalZip = fiscalVersion = fiscalHash = "";
                lblFiscal.Text = "Fiscal Target: –";
                break;
            case UpdateComponents.ServerSuite:
                serverSuiteZip = serverSuiteVersion = serverSuiteHash = "";
                lblServerSuite.Text = "Server Suite Target: –";
                break;
            default:
                if (!msiPackages.Remove(component)) return;
                msiControls[component].Label.Text = component.ToString().ToUpperInvariant() + " Target: –";
                foreach (HostState host in hosts) if (host.Updates is { } state) state.ResetDatabaseUpload();
                break;
        }
        individualPackageSelection = true;
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
        InvalidateConnectionChecks();
        var packages = new List<SoftwarePackage>();
        if (paymentZip.Length > 0) packages.Add(new(UpdateComponents.Payment, paymentZip, paymentVersion));
        if (fiscalZip.Length > 0) packages.Add(new(UpdateComponents.Fiscal, fiscalZip, fiscalVersion));
        if (serverSuiteZip.Length > 0) packages.Add(new(UpdateComponents.ServerSuite, serverSuiteZip, serverSuiteVersion));
        packages.AddRange(msiPackages.Select(pair => new SoftwarePackage(pair.Key, pair.Value.PayloadPath, pair.Value.ProductVersion)));
        selectedPackagesList.Text = packages.Count == 0 ? "Keine Pakete ausgewählt." :
            string.Join(Environment.NewLine, packages.OrderBy(package => package.Component).Select(package =>
                $"{HostUpdateState.Label(package.Component)} · {package.Version} · {Path.GetFileName(package.Path)}"));
        selectedPackagesList.AccessibleDescription = individualPackageSelection ? "Einzeln ausgewählte Pakete" : "Pakete aus dem Softwareordner";
        selectedPackagesTable.SuspendLayout();
        try
        {
            foreach (Control control in selectedPackagesTable.Controls.Cast<Control>().ToArray()) control.Dispose();
            selectedPackagesTable.ColumnStyles.Clear(); selectedPackagesTable.RowStyles.Clear();
            selectedPackagesTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            selectedPackagesTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            selectedPackagesTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectedPackagesTable.RowCount = 0;
            foreach (SoftwarePackage package in packages.OrderBy(package => package.Component))
            {
                string name = package.Component switch
                {
                    UpdateComponents.Payment => "Nt.Payment", UpdateComponents.Fiscal => "Nt.Fiskal",
                    _ => HostUpdateState.Label(package.Component)
                };
                int row = selectedPackagesTable.RowCount++;
                selectedPackagesTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                selectedPackagesTable.Controls.Add(new Label { Text=name + ":", AutoSize=true, Anchor=AnchorStyles.Left, Margin=new Padding(0, 2, 14, 2) }, 0, row);
                selectedPackagesTable.Controls.Add(new Label { Text=package.Version, AutoSize=true, Anchor=AnchorStyles.Left, Margin=new Padding(0, 2, 0, 2), AccessibleDescription=package.Path }, 1, row);
                var remove = CreateRemovePackageButton(package.Component);
                remove.Name = "removeSelected" + package.Component + "Package";
                remove.Anchor = AnchorStyles.Left;
                remove.Compact = true;
                remove.AutoSize = false;
                remove.Padding = Padding.Empty;
                remove.Size = Size.Round(new SizeF(22 * DeviceDpi / 96F, 12 * DeviceDpi / 96F));
                remove.Margin = new Padding(6, 2, 0, 2);
                selectedPackagesTable.Controls.Add(remove, 2, row);
            }
            selectedPackagesList.Visible = packages.Count == 0;
            selectedPackagesTable.Visible = packages.Count > 0;
        }
        finally { selectedPackagesTable.ResumeLayout(true); }
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
        if ((needed & UpdateComponents.ServerSuite) != 0) text.AppendLine("Server Suite: alle Instanzen. " +
            (ServerSuiteInstaller.IsSetup(serverSuiteZip)
                ? $"Das Inno-Setup führt je Instanz das reguläre Update mit explizitem Dienstnamen und Installationspfad durch, einschließlich Dienstinstallation und Dienststart. Maximal {nUpdate.Value} Instanz-Setups parallel je Host; gemeinsame Installationsordner nacheinander. Kein automatischer Windows-Neustart."
                : "ZIP-Verfahren: Dienste stoppen, verbliebene nt3_server_suite*-Prozesse beenden, Windows EventLog stoppen, Dateien einspielen, EventLog starten und anschließend die Dienste starten. Vorhandene nt3_server_suite*.exe bleiben unverändert erhalten, auch in Unterordnern. Konfiguration und Dienstkonten bleiben erhalten. Deaktivierte Suite-Dienste bleiben deaktiviert."));
        if ((needed & UpdateComponents.Msi) != 0) text.AppendLine("MSIs nur LOKAL: gleiche oder neuere installierte Versionen immer überspringen. Stille Installation ohne automatischen Windows-Neustart.");
        if (chkUploadToZzzSoftware.Checked && msiPackages.Count > 0)
        {
            text.AppendLine("Nach den Updates: MSI-Pakete in zzzSoftware aller IRIS-Namespaces ohne Präfix % hochladen. Gleiche oder neuere Datenbankversionen überspringen; nur ältere Pakete ersetzen. Uploadfehler bleiben für eine Wiederholung erhalten.");
            foreach (var package in msiPackages.Values) text.AppendLine("Datenbank: " + package.ProductName + " " + package.ProductVersion);
            if (remoteCount > 0) text.AppendLine(chkSkipLocalPackageCopy.Checked || chkSkipRemotePackageCopy.Checked ? "Remote-Datenbank: MSI-Daten über WinRM streamen, keine Paketkopie auf dem Server." :
                chkSkipZipTransfer.Checked ? "Remote-Datenbank: bereits vorhandene Paketdateien im Zielpfad verwenden." : "Remote-Datenbank: benötigte MSI-Pakete zuerst in den Zielpfad kopieren.");
        }
        if (!chkUploadToZzzSoftware.Checked) text.AppendLine("zzzSoftware-Upload ausgeschaltet.");
        else if (msiPackages.Count == 0) text.AppendLine("zzzSoftware: keine MSI-Pakete ausgewählt; kein Datenbank-Upload möglich.");
        if (chkRestartPosAfterUpdate.Checked && remoteCount > 0)
            text.AppendLine("Zum Schluss: Nur Remote-POS mit vor dem Update kleinerer POS-Version starten / neu starten, nachdem das ausgewählte POS-MSI neu in zzzSoftware gespeichert wurde und alle Uploads erfolgreich abgeschlossen sind. Ohne POS-Paket, bei gleicher/neuerer installierter POS-Version oder bereits vorhandenem gleichen/neuerem POS-MSI in zzzSoftware kein Neustart. LOKAL bleibt ausgeschlossen. Nicht gespeicherte POS-Eingaben können verloren gehen. Bereits gestartete Neustarts werden bei Abbruch vollständig abgeschlossen.");
        if (selected.Any(h => h.IsLocal)) text.AppendLine((UseLocalPackageSources
            ? "LOKAL: Gewählte Paketquellen direkt verwenden; keine zusätzliche Kopie in den Zielpfad. ZIPs werden temporär entpackt."
            : "Lokaler Zielpfad: " + txtRemotePath.Text.Trim() + "\nLOKAL: alle gewählten Pakete bereitstellen.") +
            "\nNur installierte Komponenten aktualisieren. Direkter Zugriff ohne WinRM. Installationen benötigen Administratorrechte.");
        if (selected.Any(h => !h.IsLocal)) text.AppendLine("REMOTE: ausschließlich Payment, Fiscal und Server Suite. POS, NTC, SOL und NTV werden remote nicht installiert.\n" +
            (chkSkipRemotePackageCopy.Checked ? "Pakete nur temporär für die Installation bereitstellen und danach entfernen. Keine zusätzliche Kopie oder Bereinigung im eingestellten Zielverzeichnis." :
            "Remote-Zielpfad: " + RemoteDestinationPath() + "\nPaketübertragung: " + (chkSkipZipTransfer.Checked ? "deaktiviert; vorhandene Pakete prüfen" : "aktiv")));
        return text.ToString();
    }
}
