using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal sealed class HostState
{
    public string Host { get; init; } = "";
    public string Connection { get; set; } = "Nicht geprüft";
    public string Payment { get; set; } = "–";
    public string Fiscal { get; set; } = "–";
    public string PaymentBefore { get; set; } = "–";
    public string FiscalBefore { get; set; } = "–";
    public string Phase { get; set; } = "Bereit";
    public string Result { get; set; } = "Offen";
    public string Error { get; set; } = "";
    public int Attempt { get; set; }
    public bool Scanned { get; set; }
    public bool UpdateFailed { get; set; }
    public List<ServiceInfo> Services { get; set; } = [];
    public DataGridViewRow? Row { get; set; }
    public List<string> Log { get; } = [];
}

public sealed class MainForm : Form
{
    private readonly List<HostState> hosts = [];
    private readonly object logLock = new();
    private readonly WorkerRunner runner = new();
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
    private readonly NumericUpDown nScan = new();
    private readonly NumericUpDown nUpdate = new();
    private readonly CheckBox chkSsl = new();
    private readonly CheckBox chkForceUpdate = new();
    private readonly ProgressBar progress = new();
    private readonly Button btnImport = Button("Hostliste laden");
    private readonly Button btnPayment = Button("Payment-ZIP wählen");
    private readonly Button btnFiscal = Button("Fiscal-ZIP wählen");
    private readonly Button btnScan = Button("1. Prüfen + Versionen");
    private readonly Button btnUpdate = Button("2. Updates starten");
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
    private bool lastUpdateForce;

    public MainForm()
    {
        Text = $"NovaTouch Updater v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)}";
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(247, 249, 252);
        ClientSize = new Size(1230, 790);
        MinimumSize = new Size(980, 630);
        StartPosition = FormStartPosition.CenterScreen;
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaTouchUpdater", "Logs");
        Directory.CreateDirectory(logDir);
        logFile = Path.Combine(logDir, "Update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        ConstructUi();
        // Erst nach dem ersten Layout die endgültigen Fill-Spalten aktivieren.
        Shown += (_, _) => ApplyColumnWeights();
        UpdateButtons();
        FormClosing += (_, e) =>
        {
            if (busy)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Ein Update-/Prüfdurchlauf ist noch aktiv. Bitte zuerst 'Abbrechen' klicken und den Abschluss der aktiven Hosts abwarten.",
                    "Aktive Verarbeitung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        FormClosed += (_, _) => runner.Dispose();
    }

    private static Button Button(string text) => new()
    {
        Text = text, AutoSize = true, Height = 34,
        Padding = new Padding(8, 4, 8, 4), FlatStyle = FlatStyle.System
    };

    private void ConstructUi()
    {
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16),
            BackColor = BackColor
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        Controls.Add(main);

        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label
        {
            Text = "NovaTouch 2.5 Updater", Font = new Font(Font.FontFamily, 19F, FontStyle.Bold),
            Dock = DockStyle.Left, AutoSize = true
        });
        header.Controls.Add(new Label
        {
            Text = "nt.payment / nt.fiscal    ·    parallele Remote-Updates",
            AutoSize = true, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(95, 110, 130)
        });
        main.Controls.Add(header, 0, 0);

        // Hostliste, Payment-ZIP und Fiscal-ZIP immer untereinander anzeigen.
        var configuration = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(2)
        };
        configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        for (int i = 0; i < 3; i++)
            configuration.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 3F));
        main.Controls.Add(configuration, 0, 1);

        configuration.Controls.Add(PanelRow(btnImport, lblFile), 0, 0);
        configuration.Controls.Add(PanelRow(btnPayment, lblPayment), 0, 1);
        configuration.Controls.Add(PanelRow(btnFiscal, lblFiscal), 0, 2);
        lblFile.Text = "Keine Hostliste";
        lblPayment.Text = "Payment Target: –";
        lblFiscal.Text = "Fiscal Target: –";

        // Login-Daten rechts in zwei eigenen Zeilen, damit nichts abgeschnitten wird.
        var login = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        login.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        login.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        login.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        login.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        login.Controls.Add(new Label { Text = "Benutzer:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        login.Controls.Add(new Label { Text = "Passwort:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        txtUser.Dock = DockStyle.Fill;
        txtUser.Margin = new Padding(3, 5, 3, 5);
        txtPassword.Dock = DockStyle.Fill;
        txtPassword.Margin = new Padding(3, 5, 3, 5);
        txtPassword.UseSystemPasswordChar = true;
        login.Controls.Add(txtUser, 1, 0);
        login.Controls.Add(txtPassword, 1, 1);
        configuration.Controls.Add(login, 1, 0);
        configuration.SetRowSpan(login, 2);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = true };
        options.Controls.Add(new Label { Text = "Prüfen parallel:", AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        nScan.Minimum = 1; nScan.Maximum = 150; nScan.Value = 40; nScan.Width = 55;
        options.Controls.Add(nScan);
        options.Controls.Add(new Label { Text = "Update parallel:", AutoSize = true, Margin = new Padding(6, 10, 0, 0) });
        nUpdate.Minimum = 1; nUpdate.Maximum = 100; nUpdate.Value = 8; nUpdate.Width = 55;
        options.Controls.Add(nUpdate);
        chkSsl.Text = "HTTPS"; chkSsl.AutoSize = true; chkSsl.Margin = new Padding(5, 8, 0, 0);
        options.Controls.Add(chkSsl);
        chkForceUpdate.Text = "Force Update";
        chkForceUpdate.AutoSize = true;
        chkForceUpdate.Margin = new Padding(5, 8, 0, 0);
        chkForceUpdate.Checked = false;
        options.Controls.Add(chkForceUpdate);
        var tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 350, ReshowDelay = 200 };
        tips.SetToolTip(nScan, "Maximal gleichzeitig geprüfte Hosts (WinRM und Versionsprüfung).");
        tips.SetToolTip(nUpdate, "Maximal gleichzeitig aktualisierte Hosts (Dateiübertragung und Installation).");
        tips.SetToolTip(chkForceUpdate, "Ausgewählte Payment-/Fiscal-Dienste auch bei identischer IST-/Zielversion erneut installieren.");
        tips.SetToolTip(btnRetry, "Nur Hosts mit Ergebnis 'Fehler' erneut prüfen/aktualisieren; erfolgreiche Hosts bleiben unberührt.");
        configuration.Controls.Add(options, 1, 2);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        foreach (Button btn in new[] { btnScan, btnUpdate, btnRetry, btnExport, btnCancel })
        {
            btn.Margin = new Padding(0, 5, 9, 0);
            bar.Controls.Add(btn);
        }
        bar.Controls.Add(new Label { Text = "Anzeige:", AutoSize = true, Margin = new Padding(13, 14, 5, 0) });
        statusFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        statusFilter.Width = 146;
        statusFilter.Margin = new Padding(0, 7, 0, 0);
        statusFilter.Items.AddRange(new object[] { "Alle Hosts", "Nur Fehler", "Erfolgreich", "Bereits aktuell", "Nicht erreichbar" });
        statusFilter.SelectedIndex = 0;
        statusFilter.SelectedIndexChanged += (_, _) => { if (!busy) RebuildRows(); };
        bar.Controls.Add(statusFilter);
        main.Controls.Add(bar, 0, 2);

        table.Dock = DockStyle.Fill;
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
        table.ColumnHeadersHeight = 36;
        table.RowTemplate.Height = 29;
        table.Columns.Add("hostname", "Hostname");
        table.Columns.Add("connection", "Verbindung");
        table.Columns.Add("payment", "Payment Ist");
        table.Columns.Add("fiscal", "Fiscal Ist");
        table.Columns.Add("information", "Information");
        table.Columns.Add("attempt", "Versuch");
        table.Columns.Add("result", "Ergebnis");
        // Prozentwerte werden nach dem Anzeigen des Fensters in ApplyColumnWeights gesetzt.
        // Dadurch verändern die späteren MinimumWidth-Einstellungen die FillWeight-Werte nicht.

        // Mindestbreiten verhindern bei kleinen Fenstern unlesbare Spalten.
        // Bei sehr schmalen Fenstern haben Mindestbreiten Vorrang vor Prozentwerten.
        table.Columns["hostname"].MinimumWidth = 80;
        table.Columns["connection"].MinimumWidth = 75;
        table.Columns["payment"].MinimumWidth = 110;
        table.Columns["fiscal"].MinimumWidth = 110;
        table.Columns["information"].MinimumWidth = 200;
        table.Columns["attempt"].MinimumWidth = 62;
        table.Columns["result"].MinimumWidth = 75;

        table.Columns["attempt"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["attempt"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["result"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.Columns["result"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        table.ShowCellToolTips = true;
        table.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (table.Rows[e.RowIndex].Tag is HostState host) ShowHostLog(host);
        };
        main.Controls.Add(table, 0, 3);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        lblSummary.Text = "0 Hosts";
        lblSummary.Dock = DockStyle.Fill;
        lblSummary.Font = new Font(Font, FontStyle.Bold);
        footer.Controls.Add(lblSummary, 0, 0);
        progress.Dock = DockStyle.Fill;
        footer.Controls.Add(progress, 0, 1);
        main.Controls.Add(footer, 0, 4);

        btnImport.Click += (_, _) => LoadHosts();
        btnPayment.Click += (_, _) => ChooseZip("payment");
        btnFiscal.Click += (_, _) => ChooseZip("fiscal");
        btnScan.Click += async (_, _) => await RunScansAsync();
        btnUpdate.Click += async (_, _) => await RunUpdatesAsync(false);
        btnRetry.Click += async (_, _) => await RunUpdatesAsync(true);
        btnExport.Click += (_, _) => ExportHtml();
        btnCancel.Click += (_, _) => cancellation?.Cancel();
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
            table.Columns["payment"].FillWeight = 6;
            table.Columns["fiscal"].FillWeight = 6;
            table.Columns["information"].FillWeight = 55;
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
        var pnl = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false };
        lbl.AutoEllipsis = true; lbl.AutoSize = false; lbl.Width = 280; lbl.Height = 34;
        lbl.TextAlign = ContentAlignment.MiddleLeft;
        lbl.ForeColor = Color.FromArgb(85, 98, 115);
        pnl.Controls.Add(btn); pnl.Controls.Add(lbl);
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
        hosts.Clear();
        foreach (string value in values) hosts.Add(new HostState { Host = value });
        RebuildRows();
        lblFile.Text = Path.GetFileName(dialog.FileName) + $" ({hosts.Count})";
        Log("SYSTEM", $"Hostliste eingelesen: {dialog.FileName}, {hosts.Count} eindeutige Hosts");
        UpdateSummary(); UpdateButtons();
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
                2 => host.Result == "Erfolgreich",
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

    private void CaptureCredentials()
    {
        // Capture UI state on the UI thread BEFORE Parallel.ForEachAsync.
        activeUser = txtUser.Text.Trim();
        activePassword = txtPassword.Text;
        activeSsl = chkSsl.Checked;
        activeForceUpdate = chkForceUpdate.Checked;
    }

    private WorkerRequest Request(string action, HostState host) => new()
    {
        Action = action, Host = host.Host,
        Username = activeUser, Password = activePassword, UseSsl = activeSsl, ForceUpdate = activeForceUpdate,
        RemoteInstallPath = @"C:\install\Novacom",
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
        CaptureCredentials();
        await InBatchAsync(async ct =>
        {
            await ParallelPhaseAsync(hosts, (int)nScan.Value, async (host, token) =>
            {
                Mark(host, h => { h.Connection = "Prüfung läuft"; h.Phase = "Verbindung"; h.Result = "Offen"; h.Error = ""; h.Scanned = false; });
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
                    h.Services = result.Services.ToList();
                    ReadVersions(h);
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
        var selected = onlyFailed ? hosts.Where(h => h.Result == "Fehler").ToArray() : hosts.Where(h => h.Scanned).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, onlyFailed ? "Keine fehlerhaften Hosts vorhanden." : "Zuerst die Hosts prüfen und Versionen auslesen."); return;
        }
        if (MessageBox.Show(this,
            $"Update für {selected.Length} Hosts starten?\n\nModus: {(chkForceUpdate.Checked ? "FORCE UPDATE (Versiongleichheit ignorieren)" : "Normal (gleiche Version überspringen)")}\nPayment Target: {(paymentVersion.Length == 0 ? "–" : paymentVersion)}\nFiscal Target: {(fiscalVersion.Length == 0 ? "–" : fiscalVersion)}\n\nNur Dienste mit ausgewählter ZIP werden während des Updates angehalten.",
            "Update bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        CaptureCredentials();
        lastUpdateForce = activeForceUpdate;
        await InBatchAsync(async ct =>
        {
            paymentHash = await WorkerRunner.Sha256Async(paymentZip, ct);
            fiscalHash = await WorkerRunner.Sha256Async(fiscalZip, ct);
            Log("SYSTEM", $"Update gestartet. Modus: {(activeForceUpdate ? "FORCE" : "Normal")}; Hosts: {selected.Length}; Payment: {paymentVersion}; Fiscal: {fiscalVersion}");
            await ParallelPhaseAsync(selected, (int)nUpdate.Value, async (host, token) =>
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    Mark(host, h => { h.Attempt = attempt; h.Phase = "Update"; h.Error = ""; h.Result = "Läuft"; });
                    if (!host.Scanned)
                    {
                        var probe = await DoWorkerAsync(host, "probe", token);
                        if (!probe.Success)
                        {
                            Mark(host, h => { h.Connection = "Nicht erreichbar"; h.Error = FriendlyError(probe); });
                            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 3), token);
                            continue;
                        }
                        Mark(host, h => h.Connection = "Verbunden");
                        var scan = await DoWorkerAsync(host, "scan", token);
                        if (!scan.Success)
                        {
                            Mark(host, h => h.Error = FriendlyError(scan));
                            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 3), token);
                            continue;
                        }
                        Mark(host, h => { h.Scanned = true; h.Services = scan.Services.ToList(); ReadVersions(h); h.PaymentBefore = h.Payment; h.FiscalBefore = h.Fiscal; });
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

    private void ReadVersions(HostState h)
    {
        static string Display(IEnumerable<ServiceInfo> svc)
        {
            string[] versions = svc.Select(s => DisplayText.Version(
                    string.IsNullOrWhiteSpace(s.ProductVersion) ? s.FileVersion : s.ProductVersion))
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray();
            return versions.Length > 0 ? string.Join(", ", versions) : "–";
        }
        h.Payment = Display(h.Services.Where(s => s.Kind == "payment"));
        h.Fiscal = Display(h.Services.Where(s => s.Kind == "fiscal"));
    }

    private void Mark(HostState h, Action<HostState> update)
    {
        lock (h) update(h);
        Ui(() => { PaintRow(h); UpdateSummary(); });
    }

    private void PaintRow(HostState h)
    {
        if (h.Row is null) return;
        var cells = h.Row.Cells;
        cells[0].Value = h.Host;
        cells[1].Value = h.Connection;
        cells[2].Value = h.Payment;
        cells[3].Value = h.Fiscal;
        // Bei mehreren abweichenden Versionsständen bleibt der volle Inhalt per Tooltip lesbar.
        cells[2].ToolTipText = h.Payment;
        cells[3].ToolTipText = h.Fiscal;
        // Aktuellen Schritt und bisherige Fehler-/Informationstexte gemeinsam anzeigen.
        string info = DisplayText.Information(h.Phase, h.Error);
        cells[4].Value = info;
        cells[4].ToolTipText = info;
        cells[5].Value = h.Attempt == 0 ? "–" : h.Attempt + "/3";
        cells[6].Value = h.Result == "Bereits aktuell / übersprungen" ? "Aktuell" : h.Result;
        cells[6].ToolTipText = h.Result;
        Color color = h.Result switch
        {
            "Erfolgreich" => Color.FromArgb(223, 245, 230),
            "Bereits aktuell / übersprungen" => Color.FromArgb(230, 242, 253),
            "Fehler" => Color.FromArgb(255, 229, 230),
            "Läuft" => Color.FromArgb(255, 246, 213),
            _ => Color.White
        };
        h.Row.DefaultCellStyle.BackColor = color;
    }

    private void UpdateSummary()
    {
        lblSummary.Text = $"Hosts: {hosts.Count}    |    Verbunden: {hosts.Count(h => h.Connection == "Verbunden")}    |    " +
            $"Aktualisiert: {hosts.Count(h => h.Result == "Erfolgreich")}    |    " +
            $"Bereits aktuell: {hosts.Count(h => h.Result == "Bereits aktuell / übersprungen")}    |    " +
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
        btnImport.Enabled = !busy; btnPayment.Enabled = !busy; btnFiscal.Enabled = !busy;
        btnScan.Enabled = !busy && hosts.Count > 0;
        btnUpdate.Enabled = !busy && hosts.Any(h => h.Scanned);
        btnRetry.Enabled = !busy && hosts.Any(h => h.Result == "Fehler");
        btnExport.Enabled = !busy && hosts.Count > 0;
        btnCancel.Enabled = busy;
        nScan.Enabled = !busy; nUpdate.Enabled = !busy; chkSsl.Enabled = !busy; chkForceUpdate.Enabled = !busy;
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
        using var window = new Form { Text = "Protokoll – " + host.Host, Width = 920, Height = 520, StartPosition = FormStartPosition.CenterParent };
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F), WordWrap = false };
        lock (logLock) text.Text = string.Join(Environment.NewLine, host.Log);
        window.Controls.Add(text); window.ShowDialog(this);
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
