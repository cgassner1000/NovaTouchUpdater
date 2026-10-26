using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Security.Cryptography;
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
    private bool fittingColumns;
    private bool selfUpdating;
    private bool checkingRelease;
    private AppRelease? availableRelease;
    private readonly HttpClient releaseHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource appLifetime = new();
    private readonly System.Windows.Forms.Timer releaseTimer = new() { Interval = 30 * 60 * 1000 };
    private readonly LinkLabel updateNotice = new() { Name = "updateNotice", Text = "Neue Version verfügbar", Visible = false, LinkColor = Color.FromArgb(22, 125, 65) };
    private readonly ToolTip updateToolTip = new();

    private readonly DataGridView table = new();
    private readonly ComboBox statusFilter = new();
    private readonly Label lblFile = new();
    private readonly Label lblSummary = new();
    private readonly Label lblPayment = new();
    private readonly Label lblFiscal = new();
    private readonly Label lblServerSuite = new();
    private readonly TextBox txtUser = new();
    private readonly TextBox txtPassword = new();
    private readonly TextBox txtRemotePath = new();
    private readonly NumericUpDown nScan = new();
    private readonly NumericUpDown nUpdate = new();
    private readonly NumericUpDown nHttpsPort = new();
    private readonly CheckBox chkSsl = new();
    private readonly CheckBox chkForceUpdate = new();
    private readonly CheckBox chkSkipZipTransfer = new();
    private readonly ProgressBar progress = new ModernProgressBar();
    private readonly Button btnImport = Button("Hostliste laden");
    private readonly Button btnAddHost = Button("Host hinzufügen");
    private readonly Button btnRemoveHost = Button("Entfernen");
    private readonly Button btnSaveConfig = Button("Config speichern");
    private readonly Button btnPayment = Button("Payment-ZIP wählen");
    private readonly Button btnFiscal = Button("Fiscal-ZIP wählen");
    private readonly Button btnServerSuite = Button("Server-Suite-ZIP wählen");
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
    private string serverSuiteZip = "";
    private string paymentVersion = "";
    private string fiscalVersion = "";
    private string serverSuiteVersion = "";
    private string paymentHash = "";
    private string fiscalHash = "";
    private string serverSuiteHash = "";
    private int completed;
    private int total;
    private string activeUser = "";
    private string activePassword = "";
    private bool activeSsl;
    private int activeHttpsPort = 5986;
    private bool activeForceUpdate;
    private string activeRemoteInstallPath = @"C:\install\Novacom";
    private bool activeSkipZipTransfer;
    private bool lastUpdateForce;

    public MainForm() : this(preview: false) { }

    // Off-screen design previews use synthetic rows and never load the user's
    // config, start monitoring, check releases or write an application log.
    internal MainForm(bool preview)
    {
        SuspendLayout();
        Text = $"NovaTouch Updater v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)}";
        Font = UiTheme.BodyFont();
        BackColor = UiTheme.Background;
        ClientSize = new Size(1440, 920);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaTouchUpdater", "Logs");
        if (!preview) Directory.CreateDirectory(logDir);
        logFile = preview ? "" : Path.Combine(logDir, "Update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        ConstructUi();
        WindowLayout.CompleteInitialization(this);
        if (!preview) LoadStartupConfig();
        // Erst nach dem ersten Layout die endgültigen Fill-Spalten aktivieren.
        connectionTimer.Tick += async (_, _) => await RefreshConnectionsAsync();
        releaseTimer.Tick += async (_, _) => await CheckAppReleaseAsync();
        updateNotice.LinkClicked += async (_, _) => await UpdateApplicationAsync();
        txtUser.TextChanged += (_, _) => InvalidateConnectionChecks();
        txtPassword.TextChanged += (_, _) => InvalidateConnectionChecks();
        DpiChanged += (_, _) => BeginInvoke((Action)ApplyColumnWeights);
        chkSsl.CheckedChanged += (_, _) => { InvalidateConnectionChecks(); UpdateButtons(); };
        nHttpsPort.ValueChanged += (_, _) => InvalidateConnectionChecks();
        Shown += async (_, _) =>
        {
            ApplyColumnWeights();
            if (preview) return;
            connectionTimer.Start();
            releaseTimer.Start();
            _ = CheckAppReleaseAsync();
            await RefreshConnectionsAsync();
        };
        UpdateButtons();
        FormClosing += (_, e) =>
        {
            if (busy || selfUpdating)
            {
                e.Cancel = true;
                MessageBox.Show(this, selfUpdating ? "Die neue Updater-Version wird vorbereitet. Bitte den Abschluss abwarten oder 'Abbrechen' klicken." : "Ein Update-/Prüfdurchlauf ist noch aktiv. Bitte zuerst 'Abbrechen' klicken und den Abschluss der aktiven Hosts abwarten.",
                    "Aktive Verarbeitung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            connectionClosing = true;
            appLifetime.Cancel();
            connectionTimer.Stop();
            releaseTimer.Stop();
            InvalidateConnectionChecks();
        };
        Disposed += (_, _) =>
        {
            connectionClosing = true;
            connectionTimer.Dispose();
            releaseTimer.Dispose();
            appLifetime.Cancel();
            appLifetime.Dispose();
            releaseHttp.Dispose();
            updateToolTip.Dispose();
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
            var loaded = AppConfigStore.Load(configPath);
            var config = loaded.Settings;

            txtRemotePath.Text = string.IsNullOrWhiteSpace(config.RemotePath) ? activeRemoteInstallPath : config.RemotePath;
            txtUser.Text = config.Username ?? "";
            chkSsl.Checked = config.UseSsl;
            nHttpsPort.Value = config.HttpsPort;

            txtPassword.Text = loaded.Password;

            string[] configuredHosts = config.Hosts ?? [];
            if (configuredHosts.Length > 0)
            {
                hosts.Clear();
                foreach (string host in configuredHosts) hosts.Add(new HostState { Host = host });
                lblFile.Text = $"config.json ({hosts.Count})";
                RebuildRows();
                UpdateSummary();
            }
            if (loaded.PasswordWarning is not null)
                MessageBox.Show(this, loaded.PasswordWarning, "Passwort neu eingeben", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"Die Einstellungen aus config.json konnten nicht vollständig geladen werden.\n{ex.Message}",
                "Konfiguration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveConfig()
    {
        if (busy || selfUpdating) return;
        string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        try
        {
            AppConfigStore.Save(configPath, new UpdaterConfig
            {
                // Save every loaded host, including rows hidden by a filter.
                Hosts = hosts.Select(h => h.Host).ToArray(), RemotePath = txtRemotePath.Text.Trim(),
                Username = txtUser.Text.Trim(), UseSsl = chkSsl.Checked, HttpsPort = (int)nHttpsPort.Value
            }, txtPassword.Text);
            Log("SYSTEM", $"Config gespeichert: {configPath} ({hosts.Count} Hosts)");
            MessageBox.Show(this, "Config gespeichert",
                "Config gespeichert", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            MessageBox.Show(this, "Die Config konnte nicht gespeichert werden. Eine vorhandene Datei bleibt erhalten.\n" + ex.Message,
                "Config speichern", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Button Button(string text) => new ModernButton
    {
        Text = text
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
            ColumnCount = 1, RowCount = 5, Padding = new Padding(22, 20, 22, 18), BackColor = BackColor
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new UpdaterHeader(updateNotice);
        main.Controls.Add(header, 0, 0);

        // Hostliste und die drei Update-Dateien immer untereinander anzeigen.
        var configuration = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 14)
        };
        configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(configuration, 0, 1);

        var hostList = FlowRow();
        foreach (Button btn in new[] { btnImport, btnAddHost, btnRemoveHost, btnSaveConfig })
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
        var hostCard = Section("Hosts", Stack(hostList, lblFile));
        hostCard.Name = "hostsCard";
        hostCard.Margin = new Padding(0, 0, 0, 12);
        configuration.Controls.Add(hostCard, 0, 0);
        configuration.SetColumnSpan(hostCard, 2);
        var filesContent = Stack(PanelRow(btnPayment, lblPayment),
            PanelRow(btnFiscal, lblFiscal), PanelRow(btnServerSuite, lblServerSuite), targetPath);
        var files = Section("Update-Dateien", filesContent);
        files.Name = "filesCard";
        files.Margin = new Padding(0, 0, 8, 0);
        configuration.Controls.Add(files, 0, 1);
        lblFile.Text = "Keine Hostliste";
        lblPayment.Text = "Payment Target: –";
        lblFiscal.Text = "Fiscal Target: –";
        lblServerSuite.Text = "Server Suite Target: –";

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
        nHttpsPort.Minimum = 1; nHttpsPort.Maximum = 65535; nHttpsPort.Value = 5986; nHttpsPort.Width = 75;
        var httpsOptions = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 1, Anchor = AnchorStyles.Left, Margin = Padding.Empty
        };
        for (int column = 0; column < 3; column++) httpsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        httpsOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        chkSsl.Anchor = nHttpsPort.Anchor = AnchorStyles.Left;
        httpsOptions.Controls.Add(chkSsl, 0, 0);
        httpsOptions.Controls.Add(new Label
        {
            Text = "HTTPS-Port:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 4, 3)
        }, 1, 0);
        httpsOptions.Controls.Add(nHttpsPort, 2, 0);
        optionBar.Controls.Add(httpsOptions);
        chkForceUpdate.Text = "Force Up-Downgrade";
        chkForceUpdate.AutoSize = true;
        chkForceUpdate.Anchor = AnchorStyles.Left;
        chkForceUpdate.Margin = new Padding(3);
        chkForceUpdate.Checked = false;
        chkSkipZipTransfer.Text = "Installationsdatei nicht übertragen" + Environment.NewLine + "Lokale Datei verwenden";
        chkSkipZipTransfer.AutoSize = true;
        chkSkipZipTransfer.TextAlign = ContentAlignment.MiddleLeft;
        chkSkipZipTransfer.Anchor = AnchorStyles.Left;
        var tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 350, ReshowDelay = 200 };
        tips.SetToolTip(nScan, "Maximal gleichzeitig geprüfte Hosts (WinRM und Versionsprüfung).");
        tips.SetToolTip(nUpdate, "Maximal gleichzeitig aktualisierte Hosts (Dateiübertragung und Installation).");
        tips.SetToolTip(nHttpsPort, "WinRM-Port für HTTPS, z. B. 443 (Standard: 5986). Gilt auch für Test WinRM und die Verbindungsüberwachung. HTTP verwendet Port 5985.");
        tips.SetToolTip(btnSaveConfig, "Aktuell geladene Hosts, Zielpfad, Benutzer, Passwort und HTTPS speichern. Das Passwort ist für alle Benutzer mit Lesezugriff auf diesem Rechner nutzbar. config.json neben der EXE wird beim Start geladen.");
        tips.SetToolTip(chkForceUpdate, "Ausgewählte Payment-/Fiscal-/Server-Suite-Komponenten auch bei identischer IST-/Zielversion erneut installieren.");
        tips.SetToolTip(txtRemotePath, "Absoluter Windows-Zielpfad auf den Clients.");
        tips.SetToolTip(chkSkipZipTransfer, "Lokale ZIPs bestimmen weiterhin die Zielversion. ZIPs auf den Clients werden bei Updates nicht übertragen.");
        tips.SetToolTip(btnRetry, "Hosts mit Prüf- oder Updatefehlern erneut prüfen/aktualisieren. Frühere Updatefehler bleiben auch nach einem erfolgreichen Neustart wiederholbar. Dienst- und Neustartbefehle werden hier nicht wiederholt.");
        var settingsContent = Stack(login, optionBar, chkSkipZipTransfer, chkForceUpdate);
        var settings = Section("Verbindung & Ausführung", settingsContent);
        settings.Name = "settingsCard";
        settings.Margin = new Padding(8, 0, 0, 0);
        configuration.Controls.Add(settings, 1, 1);
        bool stacked = false;
        configuration.Layout += (_, _) =>
        {
            // Bei großen Schriften oder schmalen Fenstern beide Bereiche untereinander anordnen.
            int optionsWidth = Math.Max(chkSkipZipTransfer.PreferredSize.Width, httpsOptions.PreferredSize.Width);
            int comfortableWidth = Math.Max(optionsWidth + settings.Padding.Horizontal,
                TextRenderer.MeasureText(settings.Controls[0].Text, settings.Controls[0].Font).Width * 2);
            bool needsStack = configuration.ClientSize.Width * .45F < comfortableWidth + settings.Padding.Left;
            if (needsStack == stacked) return;
            stacked = needsStack;
            configuration.SuspendLayout();
            configuration.ColumnCount = stacked ? 1 : 2;
            configuration.RowCount = stacked ? 3 : 2;
            configuration.ColumnStyles.Clear();
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, stacked ? 100 : 55));
            if (!stacked) configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            configuration.RowStyles.Clear();
            configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (stacked) configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            configuration.SetColumnSpan(hostCard, stacked ? 1 : 2);
            configuration.SetCellPosition(settings, stacked ? new TableLayoutPanelCellPosition(0, 2) : new TableLayoutPanelCellPosition(1, 1));
            files.Margin = stacked ? new Padding(0, 0, 0, 10) : new Padding(0, 0, 8, 0);
            settings.Margin = stacked ? Padding.Empty : new Padding(8, 0, 0, 0);
            configuration.ResumeLayout(true);
        };

        var bar = FlowRow();
        ((ModernButton)btnScan).Role = ButtonRole.Accent;
        ((ModernButton)btnUpdate).Role = ButtonRole.Primary;
        ((ModernButton)btnCancel).Role = ButtonRole.Danger;
        foreach (Button btn in new[] { btnWinRm, btnScan, btnUpdate, btnTransfer, btnSendCommand, btnRetry, btnExport, btnCancel })
        {
            btn.Margin = new Padding(0, 3, 7, 3);
            bar.Controls.Add(btn);
        }
        var filterBar = FlowRow();
        filterBar.Margin = new Padding(0, 9, 0, 12);
        filterBar.Controls.Add(new Label { Text = "Hostübersicht", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 18, 3) });
        filterBar.Controls.Add(new Label { Text = "Anzeige", ForeColor = UiTheme.Muted, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 8, 3) });
        statusFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        statusFilter.Width = 146;
        statusFilter.Margin = new Padding(0, 2, 0, 0);
        statusFilter.Items.AddRange(new object[] { "Alle Hosts", "Nur Fehler", "Erfolgreich", "Bereits aktuell", "Nicht erreichbar" });
        statusFilter.SelectedIndex = 0;
        statusFilter.SelectedIndexChanged += (_, _) => { if (!busy) RebuildRows(); };
        filterBar.Controls.Add(statusFilter);
        main.Controls.Add(Stack(bar, filterBar), 0, 2);

        table.Name = "hosts";
        table.Dock = DockStyle.Fill;
        table.MinimumSize = new Size(0, 160);
        table.AllowUserToAddRows = false;
        table.AllowUserToDeleteRows = false;
        table.ReadOnly = true;
        table.MultiSelect = false;
        table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        table.ScrollBars = ScrollBars.Vertical;
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
        table.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        table.Columns.Add("hostname", "Hostname");
        table.Columns.Add("connection", "Verbindung");
        table.Columns["connection"].CellTemplate = new StatusBadgeCell();
        table.Columns["connection"].ToolTipText = "Automatische WinRM-Prüfung ca. alle 10 Sekunden. Letzte Prüfung und Fehlerdetails stehen im Tooltip des Hosts.";
        table.Columns.Add("dotnet48", ".NET 4.8");
        table.Columns.Add("payment", "Payment Ist");
        table.Columns.Add("fiscal", "Fiscal Ist");
        table.Columns.Add("serversuite", "Server Suite Ist");
        table.Columns.Add("pos", "NovaTouch POS");
        table.Columns.Add("information", "Information");
        table.Columns["information"].CellTemplate = new InformationProgressCell();
        table.Columns.Add("result", "Status");
        table.Columns["result"].CellTemplate = new StatusBadgeCell();
        // Prozentwerte werden nach dem Anzeigen des Fensters in ApplyColumnWeights gesetzt.
        // Dadurch verändern die späteren MinimumWidth-Einstellungen die FillWeight-Werte nicht.

        // Mindestbreiten verhindern bei kleinen Fenstern unlesbare Spalten.
        // Bei sehr schmalen Fenstern haben Mindestbreiten Vorrang vor Prozentwerten.
        table.Columns["hostname"].MinimumWidth = 160;
        table.Columns["connection"].MinimumWidth = 112;
        table.Columns["connection"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        table.Columns["dotnet48"].MinimumWidth = 52;
        table.Columns["payment"].MinimumWidth = 110;
        table.Columns["fiscal"].MinimumWidth = 110;
        table.Columns["serversuite"].MinimumWidth = 125;
        table.Columns["serversuite"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        table.Columns["pos"].MinimumWidth = 110;
        table.Columns["information"].MinimumWidth = 200;
        table.Columns["result"].MinimumWidth = 100;
        table.Columns["hostname"].DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);

        table.Columns["dotnet48"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["dotnet48"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["dotnet48"].ToolTipText = ".NET Framework 4.8 oder neuer: ✓ installiert, ✗ nicht installiert, – nicht geprüft";
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
        table.ClientSizeChanged += (_, _) => { if (table.IsHandleCreated) ApplyColumnWeights(); };
        table.VisibleChanged += (_, _) => { if (table.Visible && table.IsHandleCreated) ApplyColumnWeights(); };
        table.CellPainting += (_, e) =>
        {
            if (e.RowIndex != -1 || e.ColumnIndex < 0) return;
            e.Paint(e.ClipBounds, e.PaintParts);
            if (e.ColumnIndex < table.Columns.Count - 1 && (e.PaintParts & DataGridViewPaintParts.Border) != 0)
            {
                var state = e.Graphics!.Save();
                e.Graphics.SetClip(Rectangle.Intersect(e.CellBounds, e.ClipBounds));
                using var separator = new Pen(Color.FromArgb(202, 213, 229));
                e.Graphics.DrawLine(separator, e.CellBounds.Right - 1, e.CellBounds.Top, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
                e.Graphics.Restore(state);
            }
            e.Handled = true;
        };
        main.Controls.Add(table, 0, 3);

        lblSummary.Text = "0 Hosts";
        lblSummary.AutoSize = true;
        lblSummary.Dock = DockStyle.Fill;
        lblSummary.ForeColor = UiTheme.Muted;
        lblSummary.Margin = new Padding(0, 12, 0, 10);
        progress.Dock = DockStyle.Fill;
        progress.Height = 6;
        progress.Margin = Padding.Empty;
        main.Controls.Add(Stack(lblSummary, progress), 0, 4);

        btnImport.Click += (_, _) => LoadHosts();
        btnSaveConfig.Click += (_, _) => SaveConfig();
        btnAddHost.Click += (_, _) => AddHost();
        btnRemoveHost.Click += (_, _) => RemoveSelectedHost();
        btnPayment.Click += (_, _) => ChooseZip("payment");
        btnFiscal.Click += (_, _) => ChooseZip("fiscal");
        btnServerSuite.Click += (_, _) => ChooseServerSuite();
        btnWinRm.Click += (_, _) =>
        {
            if (busy || hosts.Count == 0 || !ValidateCredentials()) return;
            using var dialog = new WinRmTestForm(hosts.Select(h => h.Host).ToArray(),
                txtUser.Text.Trim(), txtPassword.Text, (int)nScan.Value, chkSsl.Checked, (int)nHttpsPort.Value);
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
                + Math.Max(chkSkipZipTransfer.PreferredSize.Width + chkSkipZipTransfer.Margin.Horizontal,
                    httpsOptions.PreferredSize.Width + httpsOptions.Margin.Horizontal) + settings.Padding.Horizontal;
            if (main.MinimumSize.Width != width) main.MinimumSize = new Size(width, 0);
        };
        Controls.Add(WindowLayout.CreateScrollArea(main, table));
    }

    private SurfaceCard Section(string title, TableLayoutPanel content)
    {
        var card = new SurfaceCard
        {
            Dock = DockStyle.Top, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = content.RowCount + 1, Width = 640
        };
        card.SuspendLayout();
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < card.RowCount; row++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(new Label
        {
            Text = title, AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        var children = content.Controls.Cast<Control>().Select(child => (child, row: content.GetRow(child))).ToArray();
        foreach (var (child, row) in children) card.Controls.Add(child, 0, row + 1);
        content.Dispose();
        card.AutoSize = true;
        card.ResumeLayout(true);
        return card;
    }

    private void ApplyColumnWeights()
    {
        if (fittingColumns || table.Columns.Count == 0 || table.ClientSize.Width <= 0) return;
        fittingColumns = true;
        table.SuspendLayout();
        try
        {
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            // Minimum widths are a shared budget, not independent limits that
            // can push Status outside the viewport. Narrow windows wrap text.
            int[] preferredMinimums = [140, 104, 50, 90, 90, 104, 104, 220, 94];
            float scale = table.DeviceDpi / 96F * table.Font.SizeInPoints / 9.5F;
            int available = Math.Max(1, table.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            float fit = Math.Min(1F, available / (preferredMinimums.Sum() * scale));
            for (int index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.NotSet;
                column.MinimumWidth = Math.Max(2, (int)(preferredMinimums[index] * scale * fit));
            }
            int horizontalPadding = Math.Max(1, (int)(5 * scale * fit));
            table.ColumnHeadersDefaultCellStyle.Padding = new Padding(horizontalPadding, (int)(11 * scale), horizontalPadding, (int)(11 * scale));
            table.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            table.DefaultCellStyle.Padding = new Padding(horizontalPadding, (int)(9 * scale), horizontalPadding, (int)(9 * scale));

            table.Columns["hostname"].FillWeight = 11;
            table.Columns["connection"].FillWeight = 9;
            table.Columns["dotnet48"].FillWeight = 3;
            table.Columns["payment"].FillWeight = 6;
            table.Columns["fiscal"].FillWeight = 6;
            table.Columns["serversuite"].FillWeight = 8;
            table.Columns["pos"].FillWeight = 9;
            table.Columns["information"].FillWeight = 40;
            table.Columns["result"].FillWeight = 8;

            // Einmaliges Aktivieren nach der kompletten Gewichtung.
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table.HorizontalScrollingOffset = 0;
        }
        finally
        {
            table.ResumeLayout(true);
            fittingColumns = false;
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
        btn.Margin = new Padding(0, 4, 0, 4);
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
            Text = "Computer hinzufügen oder bearbeiten", Width = 720, Height = 420, MinimumSize = new Size(560, 350),
            FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, Font = UiTheme.BodyFont()
        };
        dialog.SuspendLayout();
        var label = new Label
        {
            Text = "Computernamen (ein Computer pro Zeile). Über „Durchsuchen“ können mehrere Computer ausgewählt werden.\n" +
                "Die Suche unterstützt frei wählbare Namensmuster und eigene LDAP-Filter über „Erweiterte Suche“.",
            AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 3, 8)
        };
        var hostNames = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical, WordWrap = false
        };
        hostNames.Lines = hosts.Select(h => h.Host).ToArray();
        var buttons = FlowRow();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Padding = new Padding(8);
        var add = new ModernButton { Text = "OK", Role = ButtonRole.Primary };
        var cancel = new ModernButton { Text = "Abbrechen", DialogResult = DialogResult.Cancel };
        var browse = new ModernButton { Text = "Durchsuchen" };
        browse.Click += (_, _) =>
        {
            try
            {
                using var search = new LdapComputerSearchDialog();
                if (search.ShowDialog(dialog) != DialogResult.OK) return;
                string[] selected = search.SelectedHosts;
                if (selected.Length > 0)
                    hostNames.AppendText((hostNames.TextLength == 0 || hostNames.Text.EndsWith(Environment.NewLine) ? "" : Environment.NewLine)
                        + string.Join(Environment.NewLine, selected));
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog,
                    "Die Domänensuche konnte nicht geöffnet oder die Auswahl nicht übernommen werden. " +
                    "Bitte die Verbindung zur Domäne und die Berechtigungen der aktuellen Windows-Anmeldung prüfen. " +
                    "Computernamen können weiterhin manuell eingegeben werden.\n\n" + ex.Message,
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

    private void ChooseServerSuite()
    {
        using OpenFileDialog dialog = new() { Filter = "ZIP (*.zip)|*.zip", Title = "Server-Suite-Update auswählen" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!UpdatePackageNames.TryServerSuiteVersion(dialog.FileName, out string version))
        {
            MessageBox.Show(this, "Ungültiger ZIP-Dateiname. Erwartet: nt.server.suite-2026.4.9792.zip",
                "Server-Suite-Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        serverSuiteZip = dialog.FileName;
        serverSuiteVersion = version;
        lblServerSuite.Text = "Server Suite Target: " + version;
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

            var ready = await PrepareUploadDirectoriesAsync(targets, false, (host, error) =>
            {
                failures.Add(host.Host + ": " + error);
                Mark(host, h => { h.Phase = "Uploadvorbereitung fehlgeschlagen"; h.Error = error; });
                CountCompleted();
            }, ct);
            if (ready is null) return;
            await Parallel.ForEachAsync(ready,
                new ParallelOptions { MaxDegreeOfParallelism = (int)nUpdate.Value, CancellationToken = ct },
                async (host, token) =>
                {
                    try
                    {
                        Mark(host, h => h.Phase = "Dateien werden übertragen");
                        WorkerResult result = await runner.RunAsync(new WorkerRequest
                        {
                            Action = "transfer", Host = host.Host, Username = activeUser, Password = activePassword,
                            UseSsl = activeSsl, HttpsPort = activeHttpsPort, RemoteInstallPath = activeRemoteInstallPath, TransferFiles = transferFiles
                        }, ev =>
                        {
                            Log(host.Host, ev.Phase + ": " + ev.Message);
                            Mark(host, h => h.ApplyProgress(ev));
                        }, token);
                        if (result.Success)
                        {
                            Interlocked.Increment(ref succeeded);
                            Mark(host, h => { h.Phase = "Dateiübertragung abgeschlossen"; h.SetProgress(100); });
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
                            Username = activeUser, Password = activePassword, UseSsl = activeSsl, HttpsPort = activeHttpsPort,
                            RestartPayment = restartPayment, RestartFiscal = restartFiscal,
                            StartServerSuite = startServerSuite
                        }, ev =>
                        {
                            Log(host.Host, ev.Phase + ": " + ev.Message);
                            Mark(host, h => h.ApplyProgress(ev));
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
        activeHttpsPort = (int)nHttpsPort.Value;
        activeForceUpdate = chkForceUpdate.Checked;
        activeRemoteInstallPath = txtRemotePath.Text.Trim();
        activeSkipZipTransfer = chkSkipZipTransfer.Checked;
    }

    private WorkerRequest Request(string action, HostState host) => new()
    {
        Action = action, Host = host.Host,
        Username = activeUser, Password = activePassword, UseSsl = activeSsl, HttpsPort = activeHttpsPort, ForceUpdate = activeForceUpdate,
        RemoteInstallPath = activeRemoteInstallPath, SkipZipTransfer = activeSkipZipTransfer,
        PaymentZip = paymentZip, PaymentVersion = paymentVersion, PaymentSha256 = paymentHash,
        FiscalZip = fiscalZip, FiscalVersion = fiscalVersion, FiscalSha256 = fiscalHash,
        ServerSuiteZip = serverSuiteZip, ServerSuiteVersion = serverSuiteVersion, ServerSuiteSha256 = serverSuiteHash
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
                    if (result.Success) { h.PaymentBefore = h.Payment; h.FiscalBefore = h.Fiscal; h.ServerSuiteBefore = h.ServerSuite; }
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
        if (string.IsNullOrWhiteSpace(paymentZip) && string.IsNullOrWhiteSpace(fiscalZip) && string.IsNullOrWhiteSpace(serverSuiteZip))
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
            $"Update für {selected.Length} Hosts starten?\n\nModus: {(chkForceUpdate.Checked ? "FORCE UPDATE (Versiongleichheit ignorieren)" : "Normal (gleiche Version überspringen)")}\nPayment Target: {(paymentVersion.Length == 0 ? "–" : paymentVersion)}\nFiscal Target: {(fiscalVersion.Length == 0 ? "–" : fiscalVersion)}\nServer Suite Target: {(serverSuiteVersion.Length == 0 ? "–" : serverSuiteVersion)}\nZielpfad: {txtRemotePath.Text.Trim()}\nZIP-Übertragung: {(chkSkipZipTransfer.Checked ? "deaktiviert (ZIPs auf Hosts werden geprüft)" : "aktiv")}\n\nNur Komponenten mit ausgewählter ZIP werden aktualisiert. Bei der Server Suite werden alle Instanzen angehalten und Dateien in apps\\nt3-server-suite sowie portable_python überschrieben oder neu angelegt. Zielordner und zusätzliche Client-Dateien bleiben erhalten. ZIP-Quellen: apps\\nt3-server-suite und installer\\python\\portable_python. Deaktivierte Dienste bleiben deaktiviert.",
            "Update bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        CaptureCredentials();
        lastUpdateForce = activeForceUpdate;
        await InBatchAsync(async ct =>
        {
            paymentHash = await WorkerRunner.Sha256Async(paymentZip, ct);
            fiscalHash = await WorkerRunner.Sha256Async(fiscalZip, ct);
            serverSuiteHash = await WorkerRunner.Sha256Async(serverSuiteZip, ct);
            Log("SYSTEM", $"Update gestartet. Modus: {(activeForceUpdate ? "FORCE" : "Normal")}; Hosts: {selected.Length}; Payment: {paymentVersion}; Fiscal: {fiscalVersion}; Server Suite: {serverSuiteVersion}; Zielpfad: {activeRemoteInstallPath}; ZIP-Übertragung: {(activeSkipZipTransfer ? "deaktiviert" : "aktiv")}");
            ResetProgress(selected.Length);
            var ready = selected;
            if (!activeSkipZipTransfer)
            {
                ready = await PrepareUploadDirectoriesAsync(selected, true, (host, error) =>
                {
                    Mark(host, h => { h.Phase = "Uploadvorbereitung fehlgeschlagen"; h.Error = error; h.Result = "Fehler"; h.UpdateFailed = true; });
                    CountCompleted();
                }, ct);
                if (ready is null) return;
            }
            await ParallelPhaseAsync(ready, (int)nUpdate.Value, async (host, token) =>
            {
                bool succeeded = false;
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
                        Mark(host, h => { h.Scanned = true; h.PaymentBefore = h.Payment; h.FiscalBefore = h.Fiscal; h.ServerSuiteBefore = h.ServerSuite; });
                    }
                    var result = await DoWorkerAsync(host, "update", token);
                    Mark(host, h =>
                    {
                        h.Error = result.Success ? "" : FriendlyError(result);
                        h.Phase = result.Success ? "Verifiziert" : "Update fehlgeschlagen";
                        if (result.Success)
                        {
                            h.Result = result.Skipped ? "Übersprungen" : result.Changed ? "Erfolgreich" : "Bereits aktuell / übersprungen";
                            if (result.Skipped) h.Phase = result.Message;
                            h.Services = result.Services.ToList(); ReadVersions(h);
                            h.UpdateFailed = false;
                            if (result.Changed) h.SetProgress(100);
                        }
                    });
                    if (result.Success) { succeeded = true; break; }
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 3), token);
                }
                Mark(host, h =>
                {
                    if (!succeeded)
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
        if (txtUser.Focused || txtPassword.Focused || nHttpsPort.ContainsFocus || (!string.IsNullOrWhiteSpace(txtUser.Text) && txtPassword.TextLength == 0)) return;

        var targets = hosts.ToDictionary(h => h.Host, h => (Host: h, Revision: h.ConnectionState.Revision), StringComparer.OrdinalIgnoreCase);
        var received = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        int generation = connectionGeneration;
        int parallelism = Math.Clamp((int)nScan.Value, 1, 40);
        bool useSsl = chkSsl.Checked;
        int httpsPort = (int)nHttpsPort.Value;
        string transport = useSsl ? $"WinRM HTTPS/{httpsPort}" : "WinRM HTTP/5985";
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
                Username = txtUser.Text.Trim(), Password = txtPassword.Text, UseSsl = useSsl, HttpsPort = httpsPort
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

    private async Task<HostState[]?> PrepareUploadDirectoriesAsync(HostState[] targets, bool forUpdate,
        Action<HostState, string> failed, CancellationToken ct)
    {
        var checks = new ConcurrentDictionary<HostState, WorkerResult>();
        void Progress(HostState host, WorkerEvent ev)
        {
            Log(host.Host, ev.Phase + ": " + ev.Message);
            Mark(host, h => h.ApplyProgress(ev));
        }
        await Parallel.ForEachAsync(targets,
            new ParallelOptions { MaxDegreeOfParallelism=(int)nUpdate.Value, CancellationToken=ct }, async (host, token) =>
        {
            try
            {
                var request=Request("check-upload-directory",host);
                request.CheckUpdateUpload=forUpdate;
                WorkerResult result=new();
                for (int attempt=1; attempt <= (forUpdate ? 3 : 1); attempt++)
                {
                    result=await runner.RunAsync(request, ev => Progress(host,ev), token);
                    if (result.Success) break;
                    if (forUpdate && attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt*3),token);
                }
                if (!result.Success) throw new InvalidOperationException(FriendlyError(result));
                if (result.UploadRequired && result.UploadDirectory is null) throw new InvalidOperationException("Keine gültige Uploadordner-Prüfung erhalten.");
                checks[host]=result;
                if (result.UploadDirectory is { } directory)
                    Log(host.Host,$"Uploadziel geprüft: {directory.Path}; {directory.EntryCount} Einträge" +
                        (directory.CanClean ? "" : "; Bereinigung gesperrt: " + directory.ProtectionReason));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log(host.Host,"UPLOADZIEL => FEHLER " + ex.Message); failed(host,ex.Message); }
        });
        var nonempty=targets.Where(h => checks.TryGetValue(h,out var check) && check.UploadRequired && check.UploadDirectory is { EntryCount: > 0 })
            .Select(h => (Host:h.Host, Directory:checks[h].UploadDirectory!)).ToArray();
        if (nonempty.Length > 0)
        {
            ct.ThrowIfCancellationRequested();
            using var dialog=new UploadDirectoryDialog(nonempty);
            DialogResult choice=dialog.ShowDialog(this);
            if (choice is not DialogResult.Yes and not DialogResult.No)
            {
                foreach (var host in checks.Keys) Mark(host,h => h.Phase="Upload abgebrochen");
                Log("SYSTEM","Upload vor der Bereinigung abgebrochen.");
                return null;
            }
            if (choice == DialogResult.Yes)
            {
                var cleanTargets=targets.Where(h => checks.TryGetValue(h,out var check) && check.UploadDirectory is { EntryCount: > 0, CanClean:true }).ToArray();
                await Parallel.ForEachAsync(cleanTargets,
                    new ParallelOptions { MaxDegreeOfParallelism=(int)nUpdate.Value, CancellationToken=ct }, async (host,token) =>
                {
                    try
                    {
                        var request=Request("clean-upload-directory",host);
                        request.ExpectedDirectorySnapshot=checks[host].UploadDirectory!.Snapshot;
                        var result=await runner.RunAsync(request, ev => Progress(host,ev), token);
                        if (!result.Success) throw new InvalidOperationException(FriendlyError(result));
                        Log(host.Host,"UPLOADZIEL BEREINIGT: " + checks[host].UploadDirectory!.Path);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        checks.TryRemove(host,out _);
                        Log(host.Host,"UPLOADZIEL => FEHLER " + ex.Message);
                        failed(host,ex.Message);
                    }
                });
            }
            else foreach (var host in checks.Keys) Log(host.Host,"Upload ohne Bereinigung bestätigt.");
        }
        ct.ThrowIfCancellationRequested();
        return targets.Where(checks.ContainsKey).ToArray();
    }

    private async Task<WorkerResult> DoWorkerAsync(HostState host, string action, CancellationToken ct)
    {
        Log(host.Host, action.ToUpperInvariant() + " gestartet");
        // A running update is not interrupted midway through stop/copy/start.
        // Cancellation prevents new hosts and new attempts, but active updates finish.
        WorkerResult result = await runner.RunAsync(Request(action, host), ev =>
        {
            Log(host.Host, ev.Phase + ": " + ev.Message);
            Mark(host, h => h.ApplyProgress(ev));
        }, action == "update" ? CancellationToken.None : ct);
        if (action == "scan")
        {
            Log(host.Host, DisplayText.DotNet48Description(result.DotNet48Installed));
            Log(host.Host, "NovaTouch POS: " + (string.IsNullOrWhiteSpace(result.PosVersion) ? "Nicht geprüft" : result.PosVersion));
        }
        if (result.Services.Length > 0 && action != "probe")
            foreach (var svc in result.Services)
                Log(host.Host, svc.Kind == "serversuite"
                    ? $"{svc.Name}: version.txt={svc.ProductVersion}, Status={svc.State}, Suite={svc.InstallDir}, Dienst-EXE={svc.ExePath}"
                    : $"{svc.Name}: Product={svc.ProductVersion}, File={svc.FileVersion}, Status={svc.State}");
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
        else { h.Payment = "–"; h.Fiscal = "–"; h.ServerSuite = "–"; }
    }

    private void ReadVersions(HostState h)
    {
        h.Payment = DisplayText.ServiceVersions(h.Services.Where(s => s.Kind == "payment"));
        h.Fiscal = DisplayText.ServiceVersions(h.Services.Where(s => s.Kind == "fiscal"));
        h.ServerSuite = DisplayText.ServiceVersions(h.Services.Where(s => s.Kind == "serversuite"));
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
        string info;
        int? rowProgress;
        int attempt;
        lock (h)
        {
            connection = h.Connection;
            connectionToolTip = h.ConnectionState.ToolTip;
            info = DisplayText.Information(h.Phase, h.Error);
            rowProgress = h.ProgressPercent;
            attempt = h.Attempt;
        }
        var cells = h.Row.Cells;
        cells["hostname"].Value = h.Host;
        cells["connection"].Value = connection;
        cells["connection"].ToolTipText = connectionToolTip;
        cells["dotnet48"].Value = DisplayText.DotNet48(h.DotNet48Installed);
        cells["dotnet48"].ToolTipText = DisplayText.DotNet48Description(h.DotNet48Installed);
        cells["payment"].Value = h.Payment;
        cells["fiscal"].Value = h.Fiscal;
        cells["serversuite"].Value = h.ServerSuite;
        cells["serversuite"].ToolTipText = DisplayText.ServiceInstances(h.Services.Where(s => s.Kind == "serversuite"), h.ServerSuite);
        cells["pos"].Value = h.PosVersion;
        // Bei mehreren abweichenden Versionsständen bleibt der volle Inhalt per Tooltip lesbar.
        cells["payment"].ToolTipText = h.Payment;
        cells["fiscal"].ToolTipText = h.Fiscal;
        cells["pos"].ToolTipText = h.PosVersion;
        // Aktuellen Schritt und bisherige Fehler-/Informationstexte gemeinsam anzeigen.
        if (rowProgress is int percent) info = $"{percent} % · {info}";
        info = DisplayText.WithAttempt(info, attempt);
        cells["information"].Tag = rowProgress;
        cells["information"].Value = info;
        cells["information"].ToolTipText = info;
        cells["result"].Value = h.Result == "Bereits aktuell / übersprungen" ? "Aktuell" : h.Result;
        cells["result"].ToolTipText = h.Result;
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

    private async Task CheckAppReleaseAsync()
    {
        if (checkingRelease || selfUpdating || connectionClosing) return;
        checkingRelease = true;
        try
        {
            var release = await new AppReleaseClient(releaseHttp).CheckAsync(AppReleaseClient.CurrentVersion, appLifetime.Token);
            if (connectionClosing || IsDisposed || selfUpdating) return;
            availableRelease = release;
            updateNotice.Visible = release is not null;
            if (release is not null)
                updateToolTip.SetToolTip(updateNotice, $"Version {AppReleaseClient.DisplayVersion(release.Version)} verfügbar. Zum Aktualisieren anklicken.");
            UpdateButtons();
        }
        catch (OperationCanceledException) { /* closing or request timeout; no startup dialog */ }
        catch (Exception ex) { if (!connectionClosing) Log("SYSTEM", "GitHub-Versionsprüfung nicht möglich: " + ex.Message); }
        finally { checkingRelease = false; }
    }

    private async Task UpdateApplicationAsync()
    {
        if (busy || selfUpdating || availableRelease is not { } release) return;
        if (MessageBox.Show(this,
            $"NovaTouch Updater {AppReleaseClient.DisplayVersion(release.Version)} ist verfügbar.\n" +
            $"Aktuelle Version: {AppReleaseClient.DisplayVersion(AppReleaseClient.CurrentVersion)}\n\n" +
            "Neue Version von GitHub herunterladen und jetzt verwenden?\n" +
            "Der Updater wird geschlossen und die neue Version im selben Ordner als NovaTouchUpdater.exe geöffnet. Die bisherige EXE wird ersetzt.\n\n" +
            "Die gespeicherte config.json bleibt erhalten. Nicht gespeicherte Änderungen gehen beim Neustart verloren.",
            "Neue Version verfügbar", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        selfUpdating = true;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        UpdateButtons();
        try
        {
            using var update = new AppSelfUpdate();
            var downloadProgress = new Progress<int>(percent =>
            {
                if (!IsDisposed && selfUpdating) updateNotice.Text = $"Download: {percent} %";
            });
            await new AppReleaseClient(releaseHttp).DownloadAsync(release, update.DownloadPath, downloadProgress, cancellation.Token);
            updateNotice.Text = "Neustart wird vorbereitet …";
            await update.StartHelperAsync(release, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Log("SYSTEM", $"Updater-Version {AppReleaseClient.DisplayVersion(release.Version)} geprüft; EXE-Austausch nach Programmende.");
            selfUpdating = false;
            Close();
            if (IsDisposed) update.HandOff();
            else throw new InvalidOperationException("Das Fenster konnte nicht geschlossen werden. Die bisherige EXE bleibt erhalten.");
        }
        catch (OperationCanceledException) { Log("SYSTEM", "Aktualisierung des Updaters abgebrochen; bisherige EXE unverändert."); }
        catch (Exception ex)
        {
            Log("SYSTEM", "Aktualisierung des Updaters fehlgeschlagen: " + ex.Message);
            if (!IsDisposed) MessageBox.Show(this, ex.Message, "Aktualisierung nicht möglich", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            selfUpdating = false;
            if (!IsDisposed) { updateNotice.Text = "Neue Version verfügbar"; UpdateButtons(); }
        }
    }

    private void UpdateButtons()
    {
        bool idle = !busy && !selfUpdating;
        updateNotice.Enabled = idle;
        btnImport.Enabled = idle; btnAddHost.Enabled = idle; btnSaveConfig.Enabled = idle;
        btnRemoveHost.Enabled = idle && table.CurrentRow?.Tag is HostState;
        btnPayment.Enabled = idle; btnFiscal.Enabled = idle; btnServerSuite.Enabled = idle;
        btnWinRm.Enabled = idle && hosts.Count > 0;
        btnScan.Enabled = idle && hosts.Count > 0;
        btnUpdate.Enabled = idle && hosts.Any(h => h.Scanned);
        btnTransfer.Enabled = idle && hosts.Count > 0;
        btnSendCommand.Enabled = idle && hosts.Count > 0;
        btnRetry.Enabled = idle && hosts.Any(h => h.CanRetryUpdate);
        btnExport.Enabled = idle && hosts.Count > 0;
        btnCancel.Enabled = busy || selfUpdating;
        nScan.Enabled = idle; nUpdate.Enabled = idle; chkSsl.Enabled = idle; chkForceUpdate.Enabled = idle;
        nHttpsPort.Enabled = idle && chkSsl.Checked;
        txtRemotePath.Enabled = idle; chkSkipZipTransfer.Enabled = idle;
        txtUser.Enabled = idle; txtPassword.Enabled = idle; statusFilter.Enabled = idle;
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
        using var window = new Form { Text = "Protokoll – " + host.Host, Width = 920, Height = 520, Padding = new Padding(18), StartPosition = FormStartPosition.CenterParent, Font = UiTheme.BodyFont() };
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
            string report = HtmlReport.Create(hosts, paymentVersion, fiscalVersion, DateTime.Now, lastUpdateForce, serverSuiteVersion);
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
