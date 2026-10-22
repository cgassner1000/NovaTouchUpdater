using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

public sealed class MainForm : Form
{
    private readonly List<HostState> hosts = [];
    private readonly object logLock = new();
    private readonly WorkerRunner runner = new();
    private readonly WorkerRunner connectionRunner = new("NovaTouchUpdater.ConnectionWorker.ps1");
    private readonly System.Windows.Forms.Timer connectionTimer = new() { Interval = 10000 };
    private CancellationTokenSource? connectionCancellation;
    private bool connectionCheckRunning;
    private bool connectionScanPaused;
    private bool connectionClosing;
    private int connectionGeneration;
    private readonly string logFile;
    private CancellationTokenSource? cancellation;
    private bool busy;

    private readonly DataGridView table = new();
    private readonly ComboBox statusFilter = new();
    private readonly Label lblFile = new();
    private readonly Label lblSummary = new();
    private readonly Label lblPayment = new();
    private readonly Label lblFiscal = new();
    private readonly TextBox txtUser = new();
    private readonly TextBox txtPassword = new();
    private readonly TextBox txtRemotePath = new();
    private readonly NumericUpDown nScan = new();
    private readonly NumericUpDown nUpdate = new();
    private readonly CheckBox chkSsl = new();
    private readonly CheckBox chkForceUpdate = new();
    private readonly CheckBox chkSkipZipTransfer = new();
    private readonly ProgressBar progress = new();
    private readonly Button btnImport = Button("Hostliste laden");
    private readonly Button btnAddHost = Button("Host hinzufügen");
    private readonly Button btnRemoveHost = Button("Entfernen");
    private readonly Button btnPayment = Button("Payment-ZIP wählen");
    private readonly Button btnFiscal = Button("Fiscal-ZIP wählen");
    private readonly Button btnWinRm = Button("Test WinRM");
    private readonly Button btnScan = Button("1. Prüfen + Versionen");
    private readonly Button btnUpdate = Button("2. Updates starten");
    private readonly Button btnTransfer = Button("Upload");
    private readonly Button btnSendCommand = Button("Befehl senden");
    private readonly Button btnRetry = Button("Fehler erneut versuchen");
    private readonly Button btnExport = Button("HTML-Bericht erstellen");
    private readonly Button btnCancel = Button("Abbrechen");

    private string paymentZip = "";
    private string fiscalZip = "";
    private string paymentVersion = "";
    private string fiscalVersion = "";
    private string paymentHash = "";
    private string fiscalHash = "";
    private int completed;
    private int total;
    private string activeUser = "";
    private string activePassword = "";
    private bool activeSsl;
    private bool activeForceUpdate;
    private string activeRemoteInstallPath = @"C:\install\Novacom";
    private bool activeSkipZipTransfer;
    private bool lastUpdateForce;

    public MainForm()
    {
        SuspendLayout();
        Text = $"NovaTouch Updater v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)}";
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        ClientSize = new Size(1230, 790);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaTouchUpdater", "Logs");
        Directory.CreateDirectory(logDir);
        logFile = Path.Combine(logDir, "Update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        ConstructUi();
        WindowLayout.CompleteInitialization(this);
        LoadStartupConfig();
        // Erst nach dem ersten Layout die endgültigen Fill-Spalten aktivieren.
        connectionTimer.Tick += async (_, _) => await RefreshConnectionsAsync();
        txtUser.TextChanged += (_, _) => InvalidateConnectionChecks();
        txtPassword.TextChanged += (_, _) => InvalidateConnectionChecks();
        chkSsl.CheckedChanged += (_, _) => InvalidateConnectionChecks();
        Shown += async (_, _) =>
        {
            ApplyColumnWeights();
            connectionTimer.Start();
            await RefreshConnectionsAsync();
        };
        UpdateButtons();
        FormClosing += (_, e) =>
        {
            if (busy)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Ein Update-/Prüfdurchlauf ist noch aktiv. Bitte zuerst 'Abbrechen' klicken und den Abschluss der aktiven Hosts abwarten.",
                    "Aktive Verarbeitung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            connectionClosing = true;
            connectionTimer.Stop();
            InvalidateConnectionChecks();
        };
        Disposed += (_, _) =>
        {
            connectionClosing = true;
            connectionTimer.Dispose();
            connectionCancellation?.Cancel();
            connectionRunner.Dispose();
            runner.Dispose();
        };
    }

    private void LoadStartupConfig()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(configPath)) return;

        try
        {
            var config = JsonSerializer.Deserialize<UpdaterConfig>(File.ReadAllText(configPath));
            if (config is null) return;

            txtRemotePath.Text = string.IsNullOrWhiteSpace(config.RemotePath) ? activeRemoteInstallPath : config.RemotePath;
            txtUser.Text = config.Username ?? "";
            chkSsl.Checked = config.UseSsl;

            txtPassword.Text = config.Password ?? "";

            string[] configuredHosts = (config.Hosts ?? [])
                .Select(host => host.Trim())
                .Where(host => host.Length > 0 && !host.StartsWith('#') && !host.StartsWith(';') && !host.Any(char.IsWhiteSpace))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (configuredHosts.Length > 0)
            {
                hosts.Clear();
                foreach (string host in configuredHosts) hosts.Add(new HostState { Host = host });
                lblFile.Text = $"config.json ({hosts.Count})";
                RebuildRows();
                UpdateSummary();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"Die Einstellungen aus config.json konnten nicht vollständig geladen werden.\n{ex.Message}",
                "Konfiguration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed class UpdaterConfig
    {
        public string[]? Hosts { get; set; }
        public string? RemotePath { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool UseSsl { get; set; }
    }

    private static Button Button(string text) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8, 4, 8, 4), FlatStyle = FlatStyle.System
    };

    private static FlowLayoutPanel FlowRow(params Control[] controls)
    {
        var row = new WrappingFlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = Padding.Empty
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private static TableLayoutPanel Stack(params Control[] controls)
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = controls.Length, Margin = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < controls.Length; i++)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(controls[i], 0, i);
        }
        return stack;
    }

    private static Control LabeledInput(string text, Control input)
    {
        var label = new Label
        {
            Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 4, 3)
        };
        var row = FlowRow(label, input);
        row.WrapContents = false;
        row.Anchor = AnchorStyles.Left;
        row.Dock = DockStyle.None;
        row.Margin = new Padding(3, 0, 8, 0);
        return row;
    }

    private void ConstructUi()
    {
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1, RowCount = 5, Padding = new Padding(16), BackColor = BackColor
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = FlowRow();
        header.Margin = new Padding(3, 0, 3, 12);
        header.Controls.Add(new Label
        {
            Text = "NovaTouch 2.5 Updater", Font = new Font(Font.FontFamily, 19F, FontStyle.Bold),
            AutoSize = true, Anchor = AnchorStyles.Left
        });
        header.Controls.Add(new Label
        {
            Text = "nt.payment / nt.fiscal    ·    parallele Remote-Updates",
            AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(95, 110, 130)
        });
        main.Controls.Add(header, 0, 0);

        // Hostliste, Payment-ZIP und Fiscal-ZIP immer untereinander anzeigen.
        var configuration = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Padding = new Padding(2)
        };
        configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(configuration, 0, 1);

        var hostList = FlowRow();
        foreach (Button btn in new[] { btnImport, btnAddHost, btnRemoveHost })
        {
            btn.Margin = new Padding(0, 0, 5, 0);
            hostList.Controls.Add(btn);
        }
        lblFile.AutoSize = true;
        lblFile.Dock = DockStyle.Fill;
        lblFile.TextAlign = ContentAlignment.MiddleLeft;
        lblFile.ForeColor = Color.FromArgb(85, 98, 115);
        var targetPath = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 2, 0, 0)
        };
        targetPath.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetPath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetPath.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        targetPath.Controls.Add(new Label
        {
            Text = "Zielpfad auf den Clients:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        txtRemotePath.Text = activeRemoteInstallPath;
        txtRemotePath.Dock = DockStyle.Fill;
        txtRemotePath.TextAlign = HorizontalAlignment.Left;
        txtRemotePath.Margin = new Padding(3, 2, 8, 2);
        targetPath.Controls.Add(txtRemotePath, 1, 0);
        var files = Stack(Stack(hostList, lblFile), PanelRow(btnPayment, lblPayment),
            PanelRow(btnFiscal, lblFiscal), targetPath);
        configuration.Controls.Add(files, 0, 0);
        lblFile.Text = "Keine Hostliste";
        lblPayment.Text = "Payment Target: –";
        lblFiscal.Text = "Fiscal Target: –";

        // Login-Daten rechts in zwei eigenen Zeilen, damit nichts abgeschnitten wird.
        var login = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 2
        };
        login.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        login.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        login.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        login.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        login.Controls.Add(new Label { Text = "Benutzer:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        login.Controls.Add(new Label { Text = "Passwort:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        txtUser.Dock = DockStyle.Fill;
        txtUser.Margin = new Padding(3, 5, 3, 5);
        txtPassword.Dock = DockStyle.Fill;
        txtPassword.Margin = new Padding(3, 5, 3, 5);
        txtPassword.UseSystemPasswordChar = true;
        login.Controls.Add(txtUser, 1, 0);
        login.Controls.Add(txtPassword, 1, 1);
        var optionBar = FlowRow();
        nScan.Minimum = 1; nScan.Maximum = 150; nScan.Value = 40; nScan.Width = 55;
        optionBar.Controls.Add(LabeledInput("Prüfen parallel:", nScan));
        nUpdate.Minimum = 1; nUpdate.Maximum = 100; nUpdate.Value = 8; nUpdate.Width = 55;
        optionBar.Controls.Add(LabeledInput("Update parallel:", nUpdate));
        chkSsl.Text = "HTTPS"; chkSsl.AutoSize = true; chkSsl.Margin = new Padding(5, 3, 8, 3);
        optionBar.Controls.Add(chkSsl);
        chkForceUpdate.Text = "Force Up-Downgrade";
        chkForceUpdate.AutoSize = true;
        chkForceUpdate.Margin = new Padding(5, 3, 8, 3);
        chkForceUpdate.Checked = false;
        optionBar.Controls.Add(chkForceUpdate);
        chkSkipZipTransfer.Text = "Installationsdatei nicht übertragen" + Environment.NewLine + "Lokale Datei verwenden";
        chkSkipZipTransfer.AutoSize = true;
        chkSkipZipTransfer.TextAlign = ContentAlignment.MiddleLeft;
        chkSkipZipTransfer.Anchor = AnchorStyles.Left;
        var tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 350, ReshowDelay = 200 };
        tips.SetToolTip(nScan, "Maximal gleichzeitig geprüfte Hosts (WinRM und Versionsprüfung).");
        tips.SetToolTip(nUpdate, "Maximal gleichzeitig aktualisierte Hosts (Dateiübertragung und Installation).");
        tips.SetToolTip(chkForceUpdate, "Ausgewählte Payment-/Fiscal-Dienste auch bei identischer IST-/Zielversion erneut installieren.");
        tips.SetToolTip(txtRemotePath, "Absoluter Windows-Zielpfad auf den Clients.");
        tips.SetToolTip(chkSkipZipTransfer, "Lokale ZIPs bestimmen weiterhin die Zielversion. ZIPs auf den Clients werden bei Updates nicht übertragen.");
        tips.SetToolTip(btnRetry, "Hosts mit Prüf- oder Updatefehlern erneut prüfen/aktualisieren. Frühere Updatefehler bleiben auch nach einem erfolgreichen Neustart wiederholbar. Dienst- und Neustartbefehle werden hier nicht wiederholt.");
        var settings = Stack(login, optionBar, chkSkipZipTransfer);
        configuration.Controls.Add(settings, 1, 0);
        bool stacked = false;
        configuration.Layout += (_, _) =>
        {
            // Bei großen Schriften oder schmalen Fenstern beide Bereiche untereinander anordnen.
            bool needsStack = configuration.ClientSize.Width * .45F < chkSkipZipTransfer.PreferredSize.Width + settings.Margin.Horizontal + 16;
            if (needsStack == stacked) return;
            stacked = needsStack;
            configuration.SuspendLayout();
            configuration.ColumnCount = stacked ? 1 : 2;
            configuration.RowCount = stacked ? 2 : 1;
            configuration.ColumnStyles.Clear();
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, stacked ? 100 : 55));
            if (!stacked) configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            configuration.RowStyles.Clear();
            configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (stacked) configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            configuration.SetCellPosition(settings, stacked ? new TableLayoutPanelCellPosition(0, 1) : new TableLayoutPanelCellPosition(1, 0));
            configuration.ResumeLayout(true);
        };

        var bar = FlowRow();
        foreach (Button btn in new[] { btnWinRm, btnScan, btnUpdate, btnTransfer, btnSendCommand, btnRetry, btnExport, btnCancel })
        {
            btn.Margin = new Padding(0, 3, 9, 3);
            bar.Controls.Add(btn);
        }
        var filterBar = FlowRow();
        filterBar.Controls.Add(new Label { Text = "Anzeige:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 5, 3) });
        statusFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        statusFilter.Width = 146;
        statusFilter.Margin = new Padding(0, 2, 0, 0);
        statusFilter.Items.AddRange(new object[] { "Alle Hosts", "Nur Fehler", "Erfolgreich", "Bereits aktuell", "Nicht erreichbar" });
        statusFilter.SelectedIndex = 0;
        statusFilter.SelectedIndexChanged += (_, _) => { if (!busy) RebuildRows(); };
        filterBar.Controls.Add(statusFilter);
        main.Controls.Add(Stack(bar, filterBar), 0, 2);

        table.Dock = DockStyle.Fill;
        table.MinimumSize = new Size(0, 160);
        table.AllowUserToAddRows = false;
        table.AllowUserToDeleteRows = false;
        table.ReadOnly = true;
        table.MultiSelect = false;
        table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        // Fill erst nach dem fertigen Layout in ApplyColumnWeights aktivieren.
        table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        table.RowHeadersVisible = false;
        table.BackgroundColor = Color.White;
        table.BorderStyle = BorderStyle.FixedSingle;
        table.EnableHeadersVisualStyles = false;
        table.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 239, 248);
        table.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        table.DefaultCellStyle.Padding = new Padding(0, 4, 0, 4);
        table.Columns.Add("hostname", "Hostname");
        table.Columns.Add("connection", "Verbindung");
        table.Columns["connection"].ToolTipText = "Automatische WinRM-Prüfung ca. alle 10 Sekunden. Letzte Prüfung und Fehlerdetails stehen im Tooltip des Hosts.";
        table.Columns.Add("dotnet48", ".NET 4.8");
        table.Columns.Add("payment", "Payment Ist");
        table.Columns.Add("fiscal", "Fiscal Ist");
        table.Columns.Add("pos", "NovaTouch POS");
        table.Columns.Add("information", "Information");
        table.Columns.Add("attempt", "Versuch");
        table.Columns.Add("result", "Ergebnis");
        // Prozentwerte werden nach dem Anzeigen des Fensters in ApplyColumnWeights gesetzt.
        // Dadurch verändern die späteren MinimumWidth-Einstellungen die FillWeight-Werte nicht.

        // Mindestbreiten verhindern bei kleinen Fenstern unlesbare Spalten.
        // Bei sehr schmalen Fenstern haben Mindestbreiten Vorrang vor Prozentwerten.
        table.Columns["hostname"].MinimumWidth = 80;
        table.Columns["connection"].MinimumWidth = 75;
        table.Columns["dotnet48"].MinimumWidth = 52;
        table.Columns["payment"].MinimumWidth = 110;
        table.Columns["fiscal"].MinimumWidth = 110;
        table.Columns["pos"].MinimumWidth = 110;
        table.Columns["information"].MinimumWidth = 200;
        table.Columns["attempt"].MinimumWidth = 62;
        table.Columns["result"].MinimumWidth = 75;

        table.Columns["dotnet48"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["dotnet48"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["dotnet48"].ToolTipText = ".NET Framework 4.8 oder neuer: ✓ installiert, ✗ nicht installiert, – nicht geprüft";
        table.Columns["attempt"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["attempt"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["result"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["result"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["result"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        table.ShowCellToolTips = true;
        table.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (table.Rows[e.RowIndex].Tag is HostState host) ShowHostLog(host);
        };
        table.SelectionChanged += (_, _) => UpdateButtons();
        main.Controls.Add(table, 0, 3);

        lblSummary.Text = "0 Hosts";
        lblSummary.AutoSize = true;
        lblSummary.Dock = DockStyle.Fill;
        lblSummary.Font = new Font(Font, FontStyle.Bold);
        progress.Dock = DockStyle.Fill;
        progress.Height = 22;
        main.Controls.Add(Stack(lblSummary, progress), 0, 4);

        btnImport.Click += (_, _) => LoadHosts();
        btnAddHost.Click += (_, _) => AddHost();
        btnRemoveHost.Click += (_, _) => RemoveSelectedHost();
        btnPayment.Click += (_, _) => ChooseZip("payment");
        btnFiscal.Click += (_, _) => ChooseZip("fiscal");
        btnWinRm.Click += (_, _) =>
        {
            if (busy || hosts.Count == 0 || !ValidateCredentials()) return;
            using var dialog = new WinRmTestForm(hosts.Select(h => h.Host).ToArray(),
                txtUser.Text.Trim(), txtPassword.Text, (int)nScan.Value, chkSsl.Checked);
            dialog.ShowDialog(this);
        };
        btnScan.Click += async (_, _) => await RunScansAsync();
        btnUpdate.Click += async (_, _) => await RunUpdatesAsync(false);
        btnTransfer.Click += async (_, _) => await TransferFilesAsync();
        btnSendCommand.Click += async (_, _) => await SendCommandAsync();
        btnRetry.Click += async (_, _) => await RunUpdatesAsync(true);
        btnExport.Click += (_, _) => ExportHtml();
        btnCancel.Click += (_, _) => cancellation?.Cancel();

        main.Layout += (_, _) =>
        {
            // Even the longest option must remain readable if a vertical scrollbar
            // reduces the available width at very large text sizes.
            int width = main.Padding.Horizontal + configuration.Margin.Horizontal + configuration.Padding.Horizontal
                + chkSkipZipTransfer.PreferredSize.Width + chkSkipZipTransfer.Margin.Horizontal;
            if (main.MinimumSize.Width != width) main.MinimumSize = new Size(width, 0);
        };
        Controls.Add(WindowLayout.CreateScrollArea(main, table));
    }

    private void ApplyColumnWeights()
    {
        // Hier kannst du die prozentualen Breiten selbst ändern (Summe = 100).
        // Zuerst Fill deaktivieren, damit das Setzen einzelner Werte nicht
        // bereits die anderen Spalten proportional neu berechnet.
        table.SuspendLayout();
        try
        {
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            foreach (DataGridViewColumn column in table.Columns)
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.NotSet;

            table.Columns["hostname"].FillWeight = 11;
            table.Columns["connection"].FillWeight = 9;
            table.Columns["dotnet48"].FillWeight = 3;
            table.Columns["payment"].FillWeight = 6;
            table.Columns["fiscal"].FillWeight = 6;
            table.Columns["pos"].FillWeight = 9;
            table.Columns["information"].FillWeight = 43;
            table.Columns["attempt"].FillWeight = 5;
            table.Columns["result"].FillWeight = 8;

            // Einmaliges Aktivieren nach der kompletten Gewichtung.
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        }
        finally
        {
            table.ResumeLayout(true);
        }
    }

    private static Control PanelRow(Button btn, Label lbl)
    {
        var pnl = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        pnl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pnl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        btn.Margin = new Padding(0, 3, 0, 3);
        lbl.AutoEllipsis = true; lbl.AutoSize = false; lbl.Size = Size.Empty;
        lbl.Dock = DockStyle.Fill;
        lbl.Margin = new Padding(8, 0, 0, 0);
        lbl.TextAlign = ContentAlignment.MiddleLeft;
        lbl.ForeColor = Color.FromArgb(85, 98, 115);
        pnl.Controls.Add(btn, 0, 0); pnl.Controls.Add(lbl, 1, 0);
        return pnl;
    }

    private void LoadHosts()
    {
        using OpenFileDialog dialog = new() { Filter = "Hostlisten (*.txt)|*.txt|Alle Dateien (*.*)|*.*", Title = "Hostnamen importieren" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var values = File.ReadAllLines(dialog.FileName)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#') && !x.StartsWith(';'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length == 0) { MessageBox.Show(this, "Die Datei enthält keine Hostnamen."); return; }
        InvalidateConnectionChecks();
        hosts.Clear();
        foreach (string value in values) hosts.Add(new HostState { Host = value });
        RebuildRows();
        lblFile.Text = Path.GetFileName(dialog.FileName) + $" ({hosts.Count})";
        Log("SYSTEM", $"Hostliste eingelesen: {dialog.FileName}, {hosts.Count} eindeutige Hosts");
        UpdateSummary(); UpdateButtons();
    }

    private void AddHost()
    {
        using var dialog = new Form
        {
            Text = "Computer hinzufügen oder bearbeiten", Width = 560, Height = 350,
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, Font = new Font("Segoe UI", 9F)
        };
        dialog.SuspendLayout();
        var label = new Label { Text = "Computernamen (ein Computer pro Zeile):", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        var hostNames = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical, WordWrap = false
        };
        hostNames.Lines = hosts.Select(h => h.Host).ToArray();
        var buttons = FlowRow();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Padding = new Padding(8);
        var add = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        var browse = new Button { Text = "Durchsuchen...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            try
            {
                string[] selected = ActiveDirectoryComputerPicker.SelectComputers(dialog.Handle);
                if (selected.Length > 0)
                    hostNames.AppendText((hostNames.TextLength == 0 || hostNames.Text.EndsWith(Environment.NewLine) ? "" : Environment.NewLine)
                        + string.Join(Environment.NewLine, selected));
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog,
                    "Der Active-Directory-Browser ist nicht verfügbar. Du kannst Computernamen unten manuell eingeben.\n\n" + ex.Message,
                    "Computer durchsuchen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        add.Click += (_, _) =>
        {
            string[] values = hostNames.Lines.Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith('#') && !x.StartsWith(';'))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (values.Length == 0)
            {
                MessageBox.Show(dialog, "Bitte mindestens einen Computernamen eingeben oder auswählen.",
                    "Computer hinzufügen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string[] invalid = values.Where(x => x.Any(char.IsWhiteSpace)).ToArray();
            if (invalid.Length > 0)
            {
                MessageBox.Show(dialog, "Jede Zeile muss genau einen Computernamen oder eine IP-Adresse enthalten:\n" +
                    string.Join(Environment.NewLine, invalid), "Ungültige Eingabe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        buttons.Controls.Add(browse);
        buttons.Controls.Add(add);
        buttons.Controls.Add(cancel);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(hostNames, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = add;
        dialog.CancelButton = cancel;
        WindowLayout.CompleteInitialization(dialog);
        dialog.Shown += (_, _) => hostNames.Focus();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string[] entries = hostNames.Lines.Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#') && !x.StartsWith(';'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = hosts.ToDictionary(h => h.Host, StringComparer.OrdinalIgnoreCase);
        int removed = hosts.Count(h => !entries.Contains(h.Host, StringComparer.OrdinalIgnoreCase));
        int added = entries.Count(value => !existing.ContainsKey(value));
        InvalidateConnectionChecks();
        hosts.Clear();
        foreach (string value in entries)
            hosts.Add(existing.TryGetValue(value, out HostState? host) ? host : new HostState { Host = value });
        lblFile.Text = $"{hosts.Count} Hosts (manuell ergänzt)";
        RebuildRows();
        UpdateSummary();
        UpdateButtons();
        Log("SYSTEM", $"Hostliste manuell bearbeitet: {added} hinzugefügt, {removed} entfernt");
    }

    private void RemoveSelectedHost()
    {
        if (busy || table.CurrentRow?.Tag is not HostState host) return;
        InvalidateConnectionChecks();
        hosts.Remove(host);
        lblFile.Text = $"{hosts.Count} Hosts";
        RebuildRows();
        UpdateSummary();
        UpdateButtons();
    }

    private void RebuildRows()
    {
        table.Rows.Clear();
        foreach (HostState host in hosts) host.Row = null;
        foreach (HostState host in hosts)
        {
            bool include = statusFilter.SelectedIndex switch
            {
                1 => host.Result == "Fehler",
                2 => host.Result == "Erfolgreich" || host.CommandSucceeded,
                3 => host.Result == "Bereits aktuell / übersprungen",
                4 => host.Connection == "Nicht erreichbar",
                _ => true
            };
            if (!include) continue;
            int idx = table.Rows.Add();
            host.Row = table.Rows[idx];
            host.Row.Tag = host;
            PaintRow(host);
        }
    }

    private static readonly Regex ZipFormat = new(@"^nt\.(payment|fiscal)_(\d+(?:\.\d+){1,3}).*\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private void ChooseZip(string kind)
    {
        using OpenFileDialog dialog = new() { Filter = "ZIP (*.zip)|*.zip", Title = "Update-ZIP auswählen" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Match m = ZipFormat.Match(Path.GetFileName(dialog.FileName));
        if (!m.Success || !m.Groups[1].Value.Equals(kind, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"Ungültiger ZIP-Dateiname. Erwartet: nt.{kind}_VERSION*.zip", "ZIP-Datei", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (kind == "payment") { paymentZip = dialog.FileName; paymentVersion = m.Groups[2].Value; lblPayment.Text = "Payment Target: " + paymentVersion; }
        else { fiscalZip = dialog.FileName; fiscalVersion = m.Groups[2].Value; lblFiscal.Text = "Fiscal Target: " + fiscalVersion; }
        UpdateButtons();
    }

    private async Task TransferFilesAsync()
    {
        if (busy || hosts.Count == 0 || !ValidateCredentials()) return;
        string remotePath = txtRemotePath.Text.Trim();
        if (!Regex.IsMatch(remotePath, @"^[A-Za-z]:\\"))
        {
            MessageBox.Show(this, "Bitte einen absoluten Windows-Zielpfad auf den Clients eingeben, z. B. C:\\install\\Novacom.",
                "Zielpfad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using OpenFileDialog dialog = new()
        {
            Filter = "Alle Dateien (*.*)|*.*",
            Multiselect = true,
            Title = "Dateien für den Upload auswählen"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var files = dialog.FileNames.ToList();

        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens eine Datei für den Upload auswählen.",
                "Dateiübertragung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string[] missing = files.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            MessageBox.Show(this, "Diese Dateien wurden nicht gefunden:\n" + string.Join(Environment.NewLine, missing),
                "Dateiübertragung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string[] duplicateNames = files.Select(path => Path.GetFileName(path))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateNames.Length > 0)
        {
            MessageBox.Show(this, "Mehrere ausgewählte Dateien haben denselben Dateinamen:\n" +
                string.Join(Environment.NewLine, duplicateNames), "Dateinamen doppelt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this,
            $"{files.Count} Datei(en) an alle {hosts.Count} Hosts nach '{remotePath}' übertragen?\n\nEs wird nichts installiert. Dateien mit gleichem Namen und abweichendem Inhalt werden ersetzt. Diese Aktion überträgt Dateien auch dann, wenn die Update-Option 'Installationsdateien nicht übertragen' aktiviert ist.",
            "Dateiübertragung bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        CaptureCredentials();
        HostState[] targets = hosts.ToArray();
        var failures = new ConcurrentBag<string>();
        int succeeded = 0;
        bool completedTransfer = false;
        await InBatchAsync(async ct =>
        {
            var transferFiles = new TransferFileRequest[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                transferFiles[i] = new TransferFileRequest
                {
                    LocalPath = files[i],
                    Sha256 = await WorkerRunner.Sha256Async(files[i], ct)
                };
            }

            await Parallel.ForEachAsync(targets,
                new ParallelOptions { MaxDegreeOfParallelism = (int)nUpdate.Value, CancellationToken = ct },
                async (host, token) =>
                {
                    try
                    {
                        Mark(host, h => h.Phase = "Dateien werden übertragen");
                        WorkerResult result = await runner.RunAsync(new WorkerRequest
                        {
                            Action = "transfer", Host = host.Host, Username = activeUser, Password = activePassword,
                            UseSsl = activeSsl, RemoteInstallPath = activeRemoteInstallPath, TransferFiles = transferFiles
                        }, ev =>
                        {
                            Log(host.Host, ev.Phase + ": " + ev.Message);
                            Mark(host, h => h.Phase = ev.Phase + " – " + ev.Message);
                        }, token);
                        if (result.Success)
                        {
                            Interlocked.Increment(ref succeeded);
                            Mark(host, h => h.Phase = "Dateiübertragung abgeschlossen");
                            Log(host.Host, "TRANSFER => OK " + result.Message);
                        }
                        else
                        {
                            string error = FriendlyError(result);
                            failures.Add(host.Host + ": " + error);
                            Mark(host, h => { h.Phase = "Dateiübertragung fehlgeschlagen"; h.Error = error; });
                            Log(host.Host, "TRANSFER => FEHLER " + error);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failures.Add(host.Host + ": " + ex.Message);
                        Mark(host, h => { h.Phase = "Dateiübertragung fehlgeschlagen"; h.Error = ex.Message; });
                        Log(host.Host, "TRANSFER => FEHLER " + ex);
                    }
                    finally { CountCompleted(); }
                });
            completedTransfer = true;
        });

        if (!completedTransfer) return;
        string report = $"Dateiübertragung: {succeeded} von {targets.Length} Hosts erfolgreich.";
        if (!failures.IsEmpty)
            report += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures);
        else if (succeeded < targets.Length)
            report += Environment.NewLine + "Der Vorgang wurde abgebrochen, bevor alle Hosts bearbeitet wurden.";
        MessageBox.Show(this, report, "Dateiübertragung", MessageBoxButtons.OK,
            failures.IsEmpty && succeeded == targets.Length ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task SendCommandAsync()
    {
        if (busy || hosts.Count == 0 || !ValidateCredentials()) return;

        using var dialog = new CommandDialog(hosts.Count);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        bool restartTerminals = dialog.RestartTerminals;
        bool restartPos = dialog.RestartPos;
        bool restartPayment = dialog.RestartPayment;
        bool restartFiscal = dialog.RestartFiscal;
        bool startServerSuite = dialog.StartServerSuite;
        if (!restartTerminals && !restartPos && !restartPayment && !restartFiscal && !startServerSuite)
        {
            MessageBox.Show(this, "Bitte eine Aktion auswählen.", "Befehl senden", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        HostState[] targets = hosts.ToArray();
        if (restartTerminals && MessageBox.Show(this,
            $"Windows auf allen {targets.Length} eingetragenen Hosts neu starten?\n\n" +
            "Der Neustart wird je Host mit 30 Sekunden Vorlauf angefordert. Geöffnete Programme werden geschlossen; nicht gespeicherte Daten können verloren gehen.\n\n" +
            "Abbrechen im Updater stoppt weitere Aufträge. Bereits übermittelte Neustartaufträge bleiben bestehen.",
            "Terminal neu starten", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (restartPos && MessageBox.Show(this,
            $"KassaPreh auf allen {targets.Length} eingetragenen Hosts starten / neu starten?\n\n" +
            "Läuft KassaPreh bereits, wird sie sofort per taskkill beendet und im selben Benutzerkonto neu gestartet. Nicht gespeicherte Eingaben können verloren gehen. Läuft sie nicht, wird sie als angemeldeter Benutzer aus dem Standardverzeichnis gestartet. Windows wird dabei nicht neu gestartet.\n\n" +
            "Pfad: C:\\Program Files (x86)\\novatouch\\bin\\KassaPreh.exe\n" +
            "Fehlt die Datei oder lässt sich keine eindeutige Benutzersitzung ermitteln, wird der Host übersprungen. Die temporäre Windows-Aufgabe wird nach der Startprüfung wieder gelöscht.\n\n" +
            "Abbrechen stoppt weitere Aufträge. Bereits gestartete POS-Neustarts werden einschließlich Bereinigung abgeschlossen.",
            "POS neustart", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        CaptureCredentials();
        int parallelism = (int)nUpdate.Value;
        string action = restartTerminals ? "restart-terminal" : restartPos ? "restart-pos" : "restart-services";
        string logAction = action.ToUpperInvariant();
        string title = restartTerminals ? "Terminal neu starten" : restartPos ? "POS neustart" : "Dienste neu starten";
        var failures = new ConcurrentBag<string>();
        var skipped = new ConcurrentBag<string>();
        int succeeded = 0;
        bool completedRestart = false;
        await InBatchAsync(async ct =>
        {
            await Parallel.ForEachAsync(targets,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                async (host, token) =>
                {
                    bool commandStarted = false;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        Mark(host, h =>
                        {
                            if (h.Result == "Fehler") Log(h.Host, "Vorheriges Ergebnis: Fehler – " + DisplayText.Information(h.Phase, h.Error));
                            h.BeginCommand(restartTerminals, restartPos);
                        });
                        commandStarted = true;
                        using var commandRunner = new WorkerRunner("NovaTouchUpdater.RemoteCommand.ps1");
                        // Finish an accepted restart and its cleanup. Cancellation
                        // only stops scheduling more terminal/POS restart requests.
                        WorkerResult result = await commandRunner.RunAsync(new WorkerRequest
                        {
                            Action = action, Host = host.Host,
                            Username = activeUser, Password = activePassword, UseSsl = activeSsl,
                            RestartPayment = restartPayment, RestartFiscal = restartFiscal,
                            StartServerSuite = startServerSuite
                        }, ev =>
                        {
                            Log(host.Host, ev.Phase + ": " + ev.Message);
                            Mark(host, h => h.Phase = ev.Phase + " – " + ev.Message);
                        }, restartTerminals || restartPos ? CancellationToken.None : token);
                        if (result.Success && result.Skipped)
                        {
                            skipped.Add(host.Host + ": " + result.Message);
                            Mark(host, h => h.SkipCommand(result.Message));
                            Log(host.Host, logAction + " => ÜBERSPRUNGEN " + result.Message);
                        }
                        else if (result.Success)
                        {
                            Interlocked.Increment(ref succeeded);
                            Mark(host, h =>
                            {
                                h.CompleteCommand(restartTerminals, DateTimeOffset.Now, restartPos);
                                if (restartPos) h.Phase = result.Message;
                            });
                            Log(host.Host, logAction + " => OK " + result.Message);
                        }
                        else
                        {
                            string error = FriendlyError(result);
                            failures.Add(host.Host + ": " + error);
                            Mark(host, h => h.FailCommand(restartTerminals, error, restartPos));
                            Log(host.Host, logAction + " => FEHLER " + error);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (commandStarted && !restartTerminals && !restartPos) Mark(host, h => h.CancelCommand());
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(host.Host + ": " + ex.Message);
                        Mark(host, h => h.FailCommand(restartTerminals, ex.Message, restartPos));
                        Log(host.Host, logAction + " => FEHLER " + ex);
                    }
                    finally { CountCompleted(); }
                });
            completedRestart = true;
        });

        if (!completedRestart && succeeded == 0 && failures.IsEmpty && skipped.IsEmpty) return;
        string report = restartTerminals
            ? $"Windows-Neustart für {succeeded} von {targets.Length} Hosts angefordert.\nDie Spalte 'Verbindung' prüft die WinRM-Erreichbarkeit automatisch ca. alle 10 Sekunden."
            : restartPos ? $"POS-Neustart: {succeeded} von {targets.Length} Hosts erfolgreich; {skipped.Count} übersprungen."
            : $"Neustart: {succeeded} von {targets.Length} Hosts erfolgreich.";
        if (!skipped.IsEmpty) report += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, skipped);
        if (!failures.IsEmpty) report += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures);
        if (succeeded + skipped.Count + failures.Count < targets.Length) report += Environment.NewLine + "Der Vorgang wurde abgebrochen, bevor alle Hosts bearbeitet wurden.";
        if (restartTerminals && !completedRestart) report += Environment.NewLine + "Bereits bestätigte Neustartaufträge bleiben bestehen.";
        MessageBox.Show(this, report, title, MessageBoxButtons.OK,
            failures.IsEmpty && succeeded + skipped.Count == targets.Length ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void CaptureCredentials()
    {
        // Capture UI state on the UI thread BEFORE Parallel.ForEachAsync.
        activeUser = txtUser.Text.Trim();
        activePassword = txtPassword.Text;
        activeSsl = chkSsl.Checked;
        activeForceUpdate = chkForceUpdate.Checked;
        activeRemoteInstallPath = txtRemotePath.Text.Trim();
        activeSkipZipTransfer = chkSkipZipTransfer.Checked;
    }

    private WorkerRequest Request(string action, HostState host) => new()
    {
        Action = action, Host = host.Host,
        Username = activeUser, Password = activePassword, UseSsl = activeSsl, ForceUpdate = activeForceUpdate,
        RemoteInstallPath = activeRemoteInstallPath, SkipZipTransfer = activeSkipZipTransfer,
        PaymentZip = paymentZip, PaymentVersion = paymentVersion, PaymentSha256 = paymentHash,
        FiscalZip = fiscalZip, FiscalVersion = fiscalVersion, FiscalSha256 = fiscalHash
    };

    private bool ValidateCredentials()
    {
        if (!string.IsNullOrWhiteSpace(txtUser.Text) && txtPassword.TextLength == 0)
        {
            MessageBox.Show(this, "Bitte das Passwort für den angegebenen Benutzer eingeben. Bei leerem Benutzernamen wird die aktuelle Windows-Anmeldung verwendet.");
            return false;
        }
        return true;
    }

    private async Task RunScansAsync()
    {
        if (busy || hosts.Count == 0 || !ValidateCredentials()) return;
        connectionScanPaused = true;
        InvalidateConnectionChecks();
        try { await RunScanPhaseAsync(); }
        finally
        {
            connectionScanPaused = false;
            _ = RefreshConnectionsAsync();
        }
    }

    private async Task RunScanPhaseAsync()
    {
        CaptureCredentials();
        await InBatchAsync(async ct =>
        {
            await ParallelPhaseAsync(hosts, (int)nScan.Value, async (host, token) =>
            {
                Mark(host, h =>
                {
                    h.Connection = "Prüfung läuft"; h.Phase = "Verbindung"; h.Result = "Offen"; h.Error = ""; h.Scanned = false;
                    h.DotNet48Installed = null; h.PosVersion = "–";
                });
                var result = await DoWorkerAsync(host, "probe", token);
                Mark(host, h =>
                {
                    h.Connection = result.Success ? "Verbunden" : "Nicht erreichbar";
                    h.Phase = result.Success ? "Verbunden" : "Verbindungsfehler";
                    h.Error = result.Success ? "" : FriendlyError(result);
                    h.Result = result.Success ? "Offen" : "Fehler";
                });
                CountCompleted();
            }, ct);
            if (ct.IsCancellationRequested) return;
            ResetProgress(hosts.Count(h => h.Connection == "Verbunden"));
            await ParallelPhaseAsync(hosts.Where(h => h.Connection == "Verbunden").ToArray(), (int)nScan.Value, async (host, token) =>
            {
                Mark(host, h => h.Phase = "Versionen werden gelesen");
                var result = await DoWorkerAsync(host, "scan", token);
                Mark(host, h =>
                {
                    h.Scanned = result.Success;
                    ReadScanInventory(h, result);
                    if (result.Success) { h.PaymentBefore = h.Payment; h.FiscalBefore = h.Fiscal; }
                    h.Phase = result.Success ? "Scan abgeschlossen" : "Scan fehlgeschlagen";
                    h.Result = result.Success ? "Bereit" : "Fehler";
                    h.Error = result.Success ? "" : FriendlyError(result);
                });
                CountCompleted();
            }, ct);
        });
    }

    private async Task RunUpdatesAsync(bool onlyFailed)
    {
        if (busy || hosts.Count == 0 || !ValidateCredentials()) return;
        if (string.IsNullOrWhiteSpace(paymentZip) && string.IsNullOrWhiteSpace(fiscalZip))
        {
            MessageBox.Show(this, "Mindestens eine Update-ZIP auswählen."); return;
        }
        if (!Regex.IsMatch(txtRemotePath.Text.Trim(), @"^[A-Za-z]:\\"))
        {
            MessageBox.Show(this, "Bitte einen absoluten Windows-Zielpfad auf den Clients eingeben, z. B. C:\\install\\Novacom.",
                "Zielpfad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var selected = onlyFailed ? hosts.Where(h => h.CanRetryUpdate).ToArray() : hosts.Where(h => h.Scanned).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, onlyFailed ? "Keine fehlerhaften Hosts vorhanden." : "Zuerst die Hosts prüfen und Versionen auslesen."); return;
        }
        if (MessageBox.Show(this,
            $"Update für {selected.Length} Hosts starten?\n\nModus: {(chkForceUpdate.Checked ? "FORCE UPDATE (Versiongleichheit ignorieren)" : "Normal (gleiche Version überspringen)")}\nPayment Target: {(paymentVersion.Length == 0 ? "–" : paymentVersion)}\nFiscal Target: {(fiscalVersion.Length == 0 ? "–" : fiscalVersion)}\nZielpfad: {txtRemotePath.Text.Trim()}\nZIP-Übertragung: {(chkSkipZipTransfer.Checked ? "deaktiviert (ZIPs auf Hosts werden geprüft)" : "aktiv")}\n\nNur Dienste mit ausgewählter ZIP werden während des Updates angehalten.",
            "Update bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        CaptureCredentials();
        lastUpdateForce = activeForceUpdate;
        await InBatchAsync(async ct =>
        {
            paymentHash = await WorkerRunner.Sha256Async(paymentZip, ct);
            fiscalHash = await WorkerRunner.Sha256Async(fiscalZip, ct);
            Log("SYSTEM", $"Update gestartet. Modus: {(activeForceUpdate ? "FORCE" : "Normal")}; Hosts: {selected.Length}; Payment: {paymentVersion}; Fiscal: {fiscalVersion}; Zielpfad: {activeRemoteInstallPath}; ZIP-Übertragung: {(activeSkipZipTransfer ? "deaktiviert" : "aktiv")}");
            await ParallelPhaseAsync(selected, (int)nUpdate.Value, async (host, token) =>
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    Mark(host, h => { h.Attempt = attempt; h.Phase = "Update"; h.Error = ""; h.Result = "Läuft"; });
                    if (!host.Scanned)
                    {
                        Mark(host, h => { h.DotNet48Installed = null; h.PosVersion = "–"; });
                        var probe = await DoWorkerAsync(host, "probe", token);
                        if (!probe.Success)
                        {
                            Mark(host, h => { h.Connection = "Nicht erreichbar"; h.Error = FriendlyError(probe); });
                            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 3), token);
                            continue;
                        }
                        Mark(host, h => h.Connection = "Verbunden");
                        var scan = await DoWorkerAsync(host, "scan", token);
                        Mark(host, h => ReadScanInventory(h, scan));
                        if (!scan.Success)
                        {
                            Mark(host, h => h.Error = FriendlyError(scan));
                            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 3), token);
                            continue;
                        }
                        Mark(host, h => { h.Scanned = true; h.PaymentBefore = h.Payment; h.FiscalBefore = h.Fiscal; });
                    }
                    var result = await DoWorkerAsync(host, "update", token);
                    Mark(host, h =>
                    {
                        h.Error = result.Success ? "" : FriendlyError(result);
                        h.Phase = result.Success ? "Verifiziert" : "Update fehlgeschlagen";
                        if (result.Success)
                        {
                            h.Result = result.Changed ? "Erfolgreich" : "Bereits aktuell / übersprungen";
                            h.Services = result.Services.ToList(); ReadVersions(h);
                            h.UpdateFailed = false;
                        }
                    });
                    if (result.Success) break;
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 3), token);
                }
                Mark(host, h =>
                {
                    if (h.Result is not ("Erfolgreich" or "Bereits aktuell / übersprungen"))
                    {
                        h.Result = "Fehler";
                        h.UpdateFailed = true;
                        h.Phase = "Maximal 3 Versuche";
                    }
                });
                CountCompleted();
            }, ct);
            Log("SYSTEM", "Update-Durchlauf abgeschlossen.");
        });
    }

    private void InvalidateConnectionChecks()
    {
        connectionGeneration++;
        connectionCancellation?.Cancel();
    }

    private async Task RefreshConnectionsAsync()
    {
        if (connectionClosing || IsDisposed || connectionScanPaused || connectionCheckRunning || hosts.Count == 0) return;
        // Do not repeatedly authenticate while credentials are being edited.
        if (txtUser.Focused || txtPassword.Focused || (!string.IsNullOrWhiteSpace(txtUser.Text) && txtPassword.TextLength == 0)) return;

        var targets = hosts.ToDictionary(h => h.Host, h => (Host: h, Revision: h.ConnectionState.Revision), StringComparer.OrdinalIgnoreCase);
        var received = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        int generation = connectionGeneration;
        int parallelism = Math.Clamp((int)nScan.Value, 1, 40);
        bool useSsl = chkSsl.Checked;
        string transport = useSsl ? "WinRM HTTPS/5986" : "WinRM HTTP/5985";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, Math.Ceiling(targets.Count / (double)parallelism) * 6 + 5)));
        connectionCancellation = timeout;
        connectionCheckRunning = true;

        void MissingResults(string error)
        {
            foreach (var target in targets.Values)
                if (!received.ContainsKey(target.Host.Host))
                    ApplyConnectionResult(target.Host, target.Revision, generation, null, error, DateTimeOffset.Now);
        }

        try
        {
            WorkerResult result = await connectionRunner.RunAsync(new WorkerRequest
            {
                Action = "monitor-connections", Hosts = targets.Keys.ToArray(), Parallelism = parallelism,
                Username = txtUser.Text.Trim(), Password = txtPassword.Text, UseSsl = useSsl
            }, ev =>
            {
                if (string.IsNullOrEmpty(ev.Host) || !targets.TryGetValue(ev.Host, out var target)) return;
                received.TryAdd(ev.Host, 0);
                DateTimeOffset checkedAt = DateTimeOffset.Now;
                Ui(() => ApplyConnectionResult(target.Host, target.Revision, generation, ev.Connected,
                    transport + ": " + DisplayText.Clean(ev.Message), checkedAt));
            }, timeout.Token);
            if (!result.Success) MissingResults("Automatische Verbindungsprüfung fehlgeschlagen: " + FriendlyError(result));
            else MissingResults("Keine Rückmeldung der automatischen Verbindungsprüfung.");
        }
        catch (OperationCanceledException) { MissingResults("Zeitlimit der automatischen Verbindungsprüfung überschritten."); }
        catch (Exception ex) { MissingResults("Automatische Verbindungsprüfung fehlgeschlagen: " + DisplayText.Clean(ex.Message)); }
        finally
        {
            connectionCancellation = null;
            connectionCheckRunning = false;
        }
    }

    private void ApplyConnectionResult(HostState host, int revision, int generation, bool? connected, string detail, DateTimeOffset checkedAt)
    {
        if (connectionClosing || IsDisposed || connectionScanPaused || generation != connectionGeneration || !hosts.Contains(host)) return;
        string previous;
        string previousResult;
        lock (host)
        {
            previous = host.Connection;
            previousResult = host.Result;
            if (!host.ApplyConnectionCheck(revision, connected, detail, checkedAt)) return;
        }
        if (previous != host.Connection)
        {
            Log(host.Host, "VERBINDUNG => " + host.Connection);
        }
        if (previousResult != host.Result) Log(host.Host, "NEUSTART => " + host.Result);
        if ((previous != host.Connection && statusFilter.SelectedIndex == 4) ||
            (previousResult != host.Result && statusFilter.SelectedIndex is 1 or 2)) RebuildRows();
        PaintRow(host);
        UpdateSummary();
    }

    private async Task<WorkerResult> DoWorkerAsync(HostState host, string action, CancellationToken ct)
    {
        Log(host.Host, action.ToUpperInvariant() + " gestartet");
        // A running update is not interrupted midway through stop/copy/start.
        // Cancellation prevents new hosts and new attempts, but active updates finish.
        WorkerResult result = await runner.RunAsync(Request(action, host), ev =>
        {
            Log(host.Host, ev.Phase + ": " + ev.Message);
            Mark(host, h => h.Phase = ev.Phase + " – " + ev.Message);
        }, action == "update" ? CancellationToken.None : ct);
        if (action == "scan")
        {
            Log(host.Host, DisplayText.DotNet48Description(result.DotNet48Installed));
            Log(host.Host, "NovaTouch POS: " + (string.IsNullOrWhiteSpace(result.PosVersion) ? "Nicht geprüft" : result.PosVersion));
        }
        if (result.Services.Length > 0 && action != "probe")
            foreach (var svc in result.Services)
                Log(host.Host, $"{svc.Name}: Product={svc.ProductVersion}, File={svc.FileVersion}, Status={svc.State}");
        Log(host.Host, action + " => " + (result.Success ? "OK" : "FEHLER") + " " + (result.Success ? result.Message : FriendlyError(result)));
        return result;
    }

    private static string FriendlyError(WorkerResult r) => DisplayText.Clean(
        !string.IsNullOrWhiteSpace(r.Error) ? r.Error : (!string.IsNullOrWhiteSpace(r.Message) ? r.Message : "Unbekannter Fehler"));

    private async Task InBatchAsync(Func<CancellationToken, Task> task)
    {
        busy = true; cancellation = new CancellationTokenSource();
        ResetProgress(hosts.Count);
        UpdateButtons();
        try { await task(cancellation.Token); }
        catch (OperationCanceledException) { Log("SYSTEM", "Verarbeitung abgebrochen"); }
        catch (Exception ex)
        {
            Log("SYSTEM", "FEHLER: " + ex);
            MessageBox.Show(this, ex.Message, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null; busy = false;
            activePassword = "";
            RebuildRows();
            UpdateSummary(); UpdateButtons();
        }
    }

    private async Task ParallelPhaseAsync(IEnumerable<HostState> selected, int parallelism, Func<HostState, CancellationToken, Task> action, CancellationToken ct)
    {
        await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (host, token) =>
        {
            try { await action(host, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Mark(host, h => { h.Error = ex.Message; h.Result = "Fehler"; h.Phase = "Fehler"; });
                Log(host.Host, "Nicht abgefangener Hostfehler: " + ex);
                CountCompleted();
            }
        });
    }

    private void ReadScanInventory(HostState h, WorkerResult result)
    {
        h.DotNet48Installed = result.DotNet48Installed;
        h.PosVersion = string.IsNullOrWhiteSpace(result.PosVersion) ? "–" : DisplayText.Clean(result.PosVersion);
        h.Services = result.Services.ToList();
        if (result.Success) ReadVersions(h);
        else { h.Payment = "–"; h.Fiscal = "–"; }
    }

    private void ReadVersions(HostState h)
    {
        h.Payment = DisplayText.ServiceVersions(h.Services.Where(s => s.Kind == "payment"));
        h.Fiscal = DisplayText.ServiceVersions(h.Services.Where(s => s.Kind == "fiscal"));
    }

    private void Mark(HostState h, Action<HostState> update)
    {
        lock (h) update(h);
        Ui(() => { PaintRow(h); UpdateSummary(); });
    }

    private void PaintRow(HostState h)
    {
        if (h.Row is null) return;
        string connection;
        string connectionToolTip;
        lock (h)
        {
            connection = h.Connection;
            connectionToolTip = h.ConnectionState.ToolTip;
        }
        var cells = h.Row.Cells;
        cells["hostname"].Value = h.Host;
        cells["connection"].Value = connection;
        cells["connection"].ToolTipText = connectionToolTip;
        cells["connection"].Style.ForeColor = connection switch
        {
            "Verbunden" => Color.FromArgb(22, 115, 75),
            "Nicht erreichbar" => Color.FromArgb(170, 45, 54),
            "Neustart angefordert" => Color.FromArgb(133, 90, 28),
            _ => Color.FromArgb(95, 110, 130)
        };
        cells["dotnet48"].Value = DisplayText.DotNet48(h.DotNet48Installed);
        cells["dotnet48"].ToolTipText = DisplayText.DotNet48Description(h.DotNet48Installed);
        cells["payment"].Value = h.Payment;
        cells["fiscal"].Value = h.Fiscal;
        cells["pos"].Value = h.PosVersion;
        // Bei mehreren abweichenden Versionsständen bleibt der volle Inhalt per Tooltip lesbar.
        cells["payment"].ToolTipText = h.Payment;
        cells["fiscal"].ToolTipText = h.Fiscal;
        cells["pos"].ToolTipText = h.PosVersion;
        // Aktuellen Schritt und bisherige Fehler-/Informationstexte gemeinsam anzeigen.
        string info = DisplayText.Information(h.Phase, h.Error);
        cells["information"].Value = info;
        cells["information"].ToolTipText = info;
        cells["attempt"].Value = h.Attempt == 0 ? "–" : h.Attempt + "/3";
        cells["result"].Value = h.Result == "Bereits aktuell / übersprungen" ? "Aktuell" : h.Result;
        cells["result"].ToolTipText = h.Result;
        Color color = h.Result switch
        {
            "Erfolgreich" => Color.FromArgb(223, 245, 230),
            "Wieder online" or "Dienste OK" or "POS OK" => Color.FromArgb(223, 245, 230),
            "Bereits aktuell / übersprungen" => Color.FromArgb(230, 242, 253),
            "Fehler" => Color.FromArgb(255, 229, 230),
            "Läuft" or "Neustart angefordert" => Color.FromArgb(255, 246, 213),
            _ => Color.White
        };
        h.Row.DefaultCellStyle.BackColor = color;
    }

    private void UpdateSummary()
    {
        lblSummary.Text = $"Hosts: {hosts.Count}    |    Verbunden: {hosts.Count(h => h.Connection == "Verbunden")}    |    " +
            $"Aktualisiert: {hosts.Count(h => h.Result == "Erfolgreich")}    |    " +
            $"Bereits aktuell: {hosts.Count(h => h.Result == "Bereits aktuell / übersprungen")}    |    " +
            $"Befehle OK: {hosts.Count(h => h.CommandSucceeded)}    |    " +
            $"Fehler: {hosts.Count(h => h.Result == "Fehler")}";
    }

    private void ResetProgress(int count)
    {
        completed = 0; total = Math.Max(count, 1);
        progress.Minimum = 0; progress.Maximum = total; progress.Value = 0;
    }
    private void CountCompleted()
    {
        int now = Interlocked.Increment(ref completed);
        Ui(() => { progress.Value = Math.Min(now, progress.Maximum); });
    }
    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { /* form is closing */ }
    }
    private void UpdateButtons()
    {
        btnImport.Enabled = !busy; btnAddHost.Enabled = !busy;
        btnRemoveHost.Enabled = !busy && table.CurrentRow?.Tag is HostState;
        btnPayment.Enabled = !busy; btnFiscal.Enabled = !busy;
        btnWinRm.Enabled = !busy && hosts.Count > 0;
        btnScan.Enabled = !busy && hosts.Count > 0;
        btnUpdate.Enabled = !busy && hosts.Any(h => h.Scanned);
        btnTransfer.Enabled = !busy && hosts.Count > 0;
        btnSendCommand.Enabled = !busy && hosts.Count > 0;
        btnRetry.Enabled = !busy && hosts.Any(h => h.CanRetryUpdate);
        btnExport.Enabled = !busy && hosts.Count > 0;
        btnCancel.Enabled = busy;
        nScan.Enabled = !busy; nUpdate.Enabled = !busy; chkSsl.Enabled = !busy; chkForceUpdate.Enabled = !busy;
        txtRemotePath.Enabled = !busy; chkSkipZipTransfer.Enabled = !busy;
        txtUser.Enabled = !busy; txtPassword.Enabled = !busy; statusFilter.Enabled = !busy;
    }

    private void Log(string host, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{host}] {DisplayText.Clean(msg)}";
        lock (logLock)
        {
            try { File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* logging problems must not break an update */ }
            var h = hosts.FirstOrDefault(x => x.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (h is not null) h.Log.Add(line);
        }
    }

    private void ShowHostLog(HostState host)
    {
        using var window = new Form { Text = "Protokoll – " + host.Host, Width = 920, Height = 520, StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9F) };
        window.SuspendLayout();
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F), WordWrap = false };
        lock (logLock) text.Text = string.Join(Environment.NewLine, host.Log);
        window.Controls.Add(text);
        WindowLayout.CompleteInitialization(window);
        window.ShowDialog(this);
    }

    private void ExportHtml()
    {
        try
        {
            // Bericht nur für die Browser-Vorschau temporär ablegen.
            // Dauerhaft speichern lässt er sich über „HTML speichern“ im Bericht
            // oder über Strg+S im Browser. Kein Speicherdialog vor der Vorschau.
            string previewDir = Path.Combine(Path.GetTempPath(), "NovaTouchUpdater", "Reports");
            Directory.CreateDirectory(previewDir);
            string filename = "NovaTouchUpdate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N")[..8] + ".html";
            string previewFile = Path.Combine(previewDir, filename);
            string report = HtmlReport.Create(hosts, paymentVersion, fiscalVersion, DateTime.Now, lastUpdateForce);
            File.WriteAllText(previewFile, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Process.Start(new ProcessStartInfo(previewFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "HTML-Bericht konnte nicht erstellt oder geöffnet werden:\n" + ex.Message,
                "Berichtsfehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
