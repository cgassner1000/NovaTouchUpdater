using System.Net;
using System.Security;

namespace NovaTouchUpdater;

// A separate dialog keeps diagnostics independent from scan/update/retry state.
internal sealed class WinRmTestForm : Form
{
    private readonly DataGridView grid = new();
    private readonly TextBox details = new();
    private readonly Label summary = new();
    private readonly Label localStatus = new() { Name = "localWinRmStatus", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button cancel = new AppButton { Text = "Abbrechen" };
    private readonly Button retry = new AppButton { Text = "Erneut prüfen", Name = "retryWinRm", Enabled = false };
    private readonly Button chooseCertificate = new AppButton { Text = "PEM-Zertifikate wählen", Name = "chooseWinRmCertificate" };
    private readonly Button defaultCertificate = new AppButton { Text = "Standard verwenden", Name = "defaultWinRmCertificate", Enabled = false };
    private readonly Button trustCertificate = new AppButton { Text = "CA vertrauen", Name = "trustWinRmCertificate", Enabled = false };
    private readonly Label certificateStatus = new() { Name = "winRmCertificateStatus", AutoSize = true, Anchor = AnchorStyles.Left };
    private CancellationTokenSource cancellation = new();
    private readonly SecureString diagnosticPassword = new();
    private readonly bool useHttps;
    private readonly int httpsPort;
    private readonly Action<string, string>? certificatePathsChanged;
    private WinRmCertificateAuthority? authority;
    private bool usingDefaultCertificate;
    private string certificateLoadWarning = "";
    private string certificateTrustWarning = "";
    private readonly Dictionary<string, WinRmClientCertificate> clientCertificates = new(StringComparer.OrdinalIgnoreCase);
    private Action<string> copyText = Clipboard.SetText;
    private Action<string>? showConfigurationError = null;
    private Func<IWorkerRunner> createRunner = () => new WorkerRunner("NovaTouchUpdater.WinRmWorker.ps1");
    private Func<WinRmCertificateAuthority, bool> isAuthorityTrusted = authority => authority.IsTrusted();
    private Action<WinRmCertificateAuthority> trustAuthority = authority => authority.Trust();
    private bool generatingCode;
    private bool running;
    private int completed;
    private readonly string protocol;
    private readonly int parallelism;
    private readonly WorkerRequest preparation;
    private readonly WorkerRequest[] diagnostics;
    private readonly DataGridViewRow[] rows;
    private string preparationDetails = "";
    private bool preparationFailed;

    public WinRmTestForm(string[] hosts, string username, string password, int parallelism, bool useSsl, int httpsPort = 5986,
        string[]? trustedHosts = null, string? certificatePath = null, string? privateKeyPath = null,
        Action<string, string>? certificatePathsChanged = null)
    {
        SuspendLayout();
        useHttps = useSsl;
        this.httpsPort = httpsPort;
        this.certificatePathsChanged = certificatePathsChanged;
        foreach (char character in password) diagnosticPassword.AppendChar(character);
        diagnosticPassword.MakeReadOnly();
        protocol = useSsl ? $"HTTPS / {httpsPort}" : "HTTP / 5985";
        this.parallelism = Math.Clamp(parallelism, 1, 150);
        preparation = new WorkerRequest { Action = "winrm-prepare", Hosts = (trustedHosts ?? hosts).ToArray() };
        diagnostics = hosts.Select(host => new WorkerRequest
        {
            Action = "winrm-test", Host = host, Username = username, UseSsl = useSsl, HttpsPort = httpsPort
        }).ToArray();
        Text = "WinRM Konfiguration";
        Size = new Size(1200, 760);
        MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = UiTheme.BodyFont();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summary.Dock = DockStyle.Fill;
        summary.AutoSize = true;
        summary.Text = hosts.Length == 0 ? "Lokales WinRM prüfen und bei Bedarf starten; TrustedHosts vorbereiten."
            : $"{hosts.Length} Hosts; maximal {this.parallelism} Hosts parallel. Remote-Protokoll: {protocol}.\nZuerst lokales WinRM prüfen und bei Bedarf starten, TrustedHosts ergänzen, danach die ausgewählten Remote-Hosts testen.";
        layout.Controls.Add(summary, 0, 0);
        var certificateTools = new WrappingFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Visible = useSsl, Margin = new Padding(0, 6, 0, 2) };
        certificateTools.Controls.AddRange([chooseCertificate, defaultCertificate, trustCertificate, certificateStatus]);
        layout.Controls.Add(certificateTools, 0, 1);
        layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5),
            Text = "Bei Fehler: Einzeiler über das Kopiersymbol kopieren und auf dem Client in einer PowerShell als Administrator ausführen. Danach erneut prüfen." }, 0, 2);
        localStatus.Text = "Lokales WinRM vorbereiten und bei Bedarf starten …";
        localStatus.Margin = new Padding(3, 8, 3, 10);
        layout.Controls.Add(localStatus, 0, 3);
        grid.Name = "winRmResults";
        grid.Dock = DockStyle.Fill;
        grid.MinimumSize = new Size(0, 120);
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        foreach (var (name, title) in new[] { ("host", "Host"), ("protocol", "Protokoll / Port"), ("tcp", "TCP"), ("result", "Ergebnis") })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns["result"].CellTemplate = new StatusBadgeCell();
        grid.Columns.Add(new DataGridViewColumn(new WinRmSetupButtonCell())
        { Name = "clientSetup", HeaderText = "Einrichten", SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 90, MinimumWidth = 70 });
        layout.Controls.Add(grid, 0, 4);
        details.Name = "winRmDetails";
        details.Dock = DockStyle.Fill;
        details.MinimumSize = new Size(0, 90);
        details.Multiline = true;
        details.ReadOnly = true;
        details.ScrollBars = ScrollBars.Both;
        details.WordWrap = false;
        layout.Controls.Add(details, 0, 5);
        cancel.Anchor = AnchorStyles.Right;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.AddRange([cancel, retry]);
        layout.Controls.Add(footer, 0, 6);
        Controls.Add(WindowLayout.CreateScrollArea(layout, grid, details));
        WindowLayout.CompleteInitialization(this);
        rows = hosts.Select(host =>
            grid.Rows[grid.Rows.Add(host, protocol, "Wartet", "Wartet", WinRmSetupState.None)]).ToArray();
        grid.SelectionChanged += (_, _) => ShowDetails();
        grid.CellContentClick += async (_, e) => { if (e.ColumnIndex == grid.Columns["clientSetup"].Index && e.RowIndex >= 0) await CopyClientSetupAsync(e.RowIndex); };
        grid.CellMouseEnter += (_, e) => grid.Cursor = e.ColumnIndex == grid.Columns["clientSetup"].Index && e.RowIndex >= 0 && rows[e.RowIndex].Cells["result"].Value is "Fehler" ? Cursors.Hand : Cursors.Default;
        grid.CellMouseLeave += (_, _) => grid.Cursor = Cursors.Default;
        retry.Click += async (_, _) => await RunDiagnosticsAsync();
        chooseCertificate.Click += (_, _) => ChooseCertificateFiles();
        defaultCertificate.Click += (_, _) => LoadDefaultCertificate(savePreference: true);
        trustCertificate.Click += (_, _) =>
        {
            if (authority is null) return;
            try { trustAuthority(authority); certificateTrustWarning = ""; UpdateCertificateStatus(); }
            catch (Exception ex) { ConfigurationError("Die gemeinsame CA konnte nicht als vertrauenswürdig eingetragen werden. Den Updater als Administrator starten.\n" + ex.Message); }
        };
        cancel.Click += (_, _) => { if (running) { cancellation.Cancel(); cancel.Enabled = false; } else Close(); };
        FormClosing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            cancellation.Cancel();
            cancel.Enabled = false;
        };
        Disposed += (_, _) => { cancellation.Dispose(); diagnosticPassword.Dispose(); ClearClientCertificates(); authority?.Dispose(); };
        if (useSsl)
        {
            if (!string.IsNullOrWhiteSpace(certificatePath) && !string.IsNullOrWhiteSpace(privateKeyPath))
            {
                try { LoadCertificateFiles(certificatePath, privateKeyPath, savePaths: false); }
                catch (Exception ex) { certificateLoadWarning = "Eigene PEM-Dateien nicht verfügbar: " + ex.Message; }
            }
            if (authority is null) LoadDefaultCertificate();
        }
        Shown += async (_, _) => await RunDiagnosticsAsync();
    }

    internal async Task RunDiagnosticsAsync(IWorkerRunner? worker = null)
    {
        if (running || generatingCode) return;
        cancellation.Dispose(); cancellation = new();
        running = true; completed = 0; preparationFailed = false; preparationDetails = "";
        retry.Enabled = false; cancel.Enabled = true; cancel.Text = "Abbrechen";
        localStatus.Text = "Lokales WinRM vorbereiten und bei Bedarf starten …";
        localStatus.ForeColor = UiTheme.Muted;
        summary.Text = $"WinRM prüfen · {protocol} · {diagnostics.Length} Host(s)";
        foreach (var row in rows)
        {
            row.Cells["tcp"].Value = row.Cells["result"].Value = "Wartet";
            row.Cells["clientSetup"].Value = WinRmSetupState.None; row.Tag = null;
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = "";
        }
        ShowDetails();
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            string certificateDetails = PrepareCertificateTrust();
            preparationDetails = certificateDetails;
            ShowDetails();
            using var runner = worker ?? createRunner();
            WorkerResult ready = await RunWithTimeoutAsync(runner, preparation, cancellation.Token);
            preparationDetails = certificateDetails + $"Lokale WinRM-Vorbereitung\r\n{ready.Message}\r\n{ready.Error}";
            localStatus.Text = ready.Success ? ready.Message : "Lokale WinRM-Vorbereitung fehlgeschlagen – siehe Details.";
            localStatus.ForeColor = ready.Success ? UiTheme.Success : UiTheme.Danger;
            ShowDetails();
            if (!ready.Success) { preparationFailed = true; return; }
            await Parallel.ForEachAsync(Enumerable.Range(0, diagnostics.Length),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellation.Token },
                async (index, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var row = rows[index];
                    Post(() => row.Cells["result"].Value = "Läuft");
                    diagnostics[index].Password = new NetworkCredential("", diagnosticPassword).Password;
                    WorkerResult result = await RunWithTimeoutAsync(runner, diagnostics[index], token);
                    Post(() => DisplayResult(row, result));
                });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (preparationDetails.Length == 0) localStatus.Text = "Lokale Vorbereitung abgebrochen.";
        }
        catch (Exception ex)
        {
            preparationFailed = true;
            preparationDetails += "\r\n" + ex;
            localStatus.Text = "WinRM-Test fehlgeschlagen – siehe Details.";
            localStatus.ForeColor = UiTheme.Danger;
        }
        finally
        {
            foreach (var request in diagnostics) request.Password = "";
            running = false;
            foreach (var row in rows)
                if (row.Cells["result"].Value is "Wartet" or "Läuft")
                {
                    row.Cells["result"].Value = cancellation.IsCancellationRequested ? "Abgebrochen"
                        : preparationFailed ? "Nicht gestartet" : "Nicht abgeschlossen";
                    row.Tag = $"{row.Cells[0].Value} – {row.Cells[1].Value}\r\n{row.Cells["result"].Value}. Es liegt kein vollständiges Testergebnis vor.";
                    row.Cells["tcp"].Value = "Nicht bestätigt";
                }
            summary.Text = (diagnostics.Length == 0 ? "Lokale WinRM-Vorbereitung beendet" : $"{completed} von {diagnostics.Length} Hosttests abgeschlossen") +
                (cancellation.IsCancellationRequested ? " – abgebrochen." : ".") +
                (preparationFailed ? "\nWinRM-Vorbereitung fehlgeschlagen – siehe Details."
                    : diagnostics.Length == 0 ? "\nKeine Remote-Hosts zum Testen ausgewählt."
                    : $"\nRemote-Protokoll: {protocol}. Zeile auswählen für vollständige Details.");
            cancel.Enabled = true;
            cancel.Text = "Schließen";
            retry.Enabled = true;
            ShowDetails();
        }
    }

    private static async Task<WorkerResult> RunWithTimeoutAsync(IWorkerRunner runner, WorkerRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { return await runner.RunAsync(request, _ => { }, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new WorkerResult { Error = "Gesamtzeitlimit von 60 Sekunden überschritten. Der lokale Testprozess wurde beendet; noch laufende Schritte sind nicht bestätigt." };
        }
    }

    private void Post(Action action) => Invoke(action);

    private void DisplayResult(DataGridViewRow row, WorkerResult result)
    {
        row.Cells[2].Value = result.Tcp;
        row.Cells["result"].Value = result.Success ? "OK" : "Fehler";
        row.Cells["clientSetup"].Value = result.Success ? WinRmSetupState.None : WinRmSetupState.Ready;
        row.Tag = $"{row.Cells[0].Value} – {row.Cells[1].Value}\r\nTCP: {result.Tcp}\r\nNew-PSSession: {result.Session}\r\nInvoke-Command: {result.Command}\r\n\r\n{result.Message}\r\n{result.Error}";
        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = result.Error;
        row.Cells["clientSetup"].ToolTipText = result.Success ? "" : "Einzeiler für diesen Client kopieren; dort als Administrator ausführen.";
        completed++;
        summary.Text = $"{completed} Hosttests abgeschlossen. Zeile auswählen für vollständige Details.";
        ShowDetails();
    }

    private void ShowDetails() => details.Text = preparationDetails + "\r\n\r\n" +
        (grid.CurrentRow?.Tag as string ?? "Für diese Zeile liegt noch kein Ergebnis vor.");

    private void ChooseCertificateFiles()
    {
        using var certificate = new OpenFileDialog { Title = "Gemeinsames CA-Zertifikat auswählen", Filter = "PEM-Zertifikat (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|Alle Dateien (*.*)|*.*" };
        if (certificate.ShowDialog(this) != DialogResult.OK) return;
        using var key = new OpenFileDialog { Title = "Privaten CA-Schlüssel auswählen", Filter = "PEM-Schlüssel (*.pem;*.key)|*.pem;*.key|Alle Dateien (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(certificate.FileName) };
        if (key.ShowDialog(this) != DialogResult.OK) return;
        try { LoadCertificateFiles(certificate.FileName, key.FileName); }
        catch (Exception ex) { ConfigurationError("Die PEM-Dateien konnten nicht geladen werden.\n" + ex.Message); }
    }

    private void LoadCertificateFiles(string certificatePath, string privateKeyPath, bool savePaths = true)
    {
        var loaded = WinRmCertificateAuthority.Load(certificatePath, privateKeyPath);
        SetCertificateAuthority(loaded, useDefault: false);
        certificateLoadWarning = "";
        UpdateCertificateStatus();
        if (savePaths) certificatePathsChanged?.Invoke(certificatePath, privateKeyPath);
    }

    private void LoadDefaultCertificate(bool savePreference = false)
    {
        SetCertificateAuthority(WinRmCertificateAuthority.LoadDefault(), useDefault: true);
        if (savePreference)
        {
            certificateLoadWarning = "";
            certificatePathsChanged?.Invoke("", "");
        }
        UpdateCertificateStatus();
    }

    private void SetCertificateAuthority(WinRmCertificateAuthority loaded, bool useDefault)
    {
        ClearClientCertificates();
        authority?.Dispose();
        authority = loaded;
        usingDefaultCertificate = useDefault;
        certificateTrustWarning = "";
    }

    private string PrepareCertificateTrust()
    {
        if (!useHttps || authority is null) return "";
        string source = usingDefaultCertificate ? "Eingebautes HTTPS-Standardzertifikat" : "Gewähltes HTTPS-Zertifikat";
        try
        {
            if (!isAuthorityTrusted(authority)) trustAuthority(authority);
            certificateTrustWarning = "";
            UpdateCertificateStatus();
            return source + ": öffentliche CA und Sperrliste am Updater vertraut.\r\n" +
                (certificateLoadWarning.Length == 0 ? "" : certificateLoadWarning + "\r\n") + "\r\n";
        }
        catch (Exception ex)
        {
            certificateTrustWarning = "CA-Vertrauen erfordert Administratorrechte";
            UpdateCertificateStatus();
            return source + ": CA-Vertrauen konnte nicht automatisch eingerichtet werden. Den Updater als Administrator starten.\r\n" + ex.Message + "\r\n\r\n";
        }
    }

    private void UpdateCertificateStatus()
    {
        bool trusted = authority is not null && isAuthorityTrusted(authority);
        trustCertificate.Enabled = authority is not null && !trusted && !generatingCode;
        trustCertificate.Text = trusted ? "CA vertraut" : "CA vertrauen";
        defaultCertificate.Enabled = !usingDefaultCertificate && !generatingCode;
        certificateStatus.Text = authority is null ? "Keine CA geladen." : (usingDefaultCertificate ? "Standardzertifikat" : "Eigenes Zertifikat") +
            " · " + (trusted ? "am Updater vertraut" : certificateTrustWarning.Length > 0 ? certificateTrustWarning : "CA-Vertrauen wird automatisch eingerichtet") +
            (certificateLoadWarning.Length == 0 ? "" : " · eigene Dateien nicht verfügbar");
        certificateStatus.ForeColor = trusted ? UiTheme.Success : certificateTrustWarning.Length > 0 ? UiTheme.Warning : UiTheme.Muted;
        certificateStatus.AccessibleDescription = authority?.Subject ?? "";
    }

    private void ClearClientCertificates()
    {
        foreach (var certificate in clientCertificates.Values) certificate.Dispose();
        clientCertificates.Clear();
    }

    internal async Task CopyClientSetupAsync(int rowIndex)
    {
        if (generatingCode || rowIndex < 0 || rowIndex >= rows.Length || rows[rowIndex].Cells["result"].Value is not "Fehler") return;
        if (useHttps && authority is null)
        {
            LoadDefaultCertificate();
            if (authority is null) return;
        }
        generatingCode = true; chooseCertificate.Enabled = defaultCertificate.Enabled = trustCertificate.Enabled = retry.Enabled = false;
        var row = rows[rowIndex];
        row.Cells["clientSetup"].Value = WinRmSetupState.Copying;
        row.Cells["clientSetup"].ToolTipText = "Einzeiler wird vorbereitet …";
        try
        {
            PrepareCertificateTrust();
            string host = diagnostics[rowIndex].Host;
            WinRmClientCertificate? certificate = null;
            if (useHttps && !clientCertificates.TryGetValue(host, out certificate))
            {
                var signer = authority!;
                certificate = await Task.Run(() => signer.Issue(host));
                if (IsDisposed) { certificate.Dispose(); return; }
                clientCertificates.Add(host, certificate);
            }
            string script = WinRmClientSetup.CreateScript(host, useHttps, httpsPort, certificate);
            copyText(WinRmClientSetup.OneLine(script, useHttps));
            row.Cells["clientSetup"].Value = WinRmSetupState.Copied;
            row.Cells["clientSetup"].ToolTipText = "Kopiert. Auf " + host + " in einer PowerShell als Administrator ausführen; danach erneut prüfen.";
            localStatus.Text = host + ": " + (useHttps ? "HTTPS" : "HTTP / 5985") + "-Einzeiler kopiert. Auf diesem Client als Administrator ausführen.";
            localStatus.ForeColor = UiTheme.Accent;
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                row.Cells["clientSetup"].ToolTipText = "Kopieren fehlgeschlagen. Erneut versuchen.";
                localStatus.Text = "Einzeiler konnte nicht kopiert werden.";
                localStatus.ForeColor = UiTheme.Danger;
                ConfigurationError("Der Clientcode konnte nicht kopiert werden.\n" + ex.Message);
            }
        }
        finally
        {
            generatingCode = false;
            if (!IsDisposed)
            {
                if (row.Cells["clientSetup"].Value is WinRmSetupState.Copying) row.Cells["clientSetup"].Value = WinRmSetupState.Ready;
                chooseCertificate.Enabled = true; retry.Enabled = !running;
                UpdateCertificateStatus();
            }
        }
    }

    private void ConfigurationError(string message)
    {
        if (showConfigurationError is { } show) show(message);
        else AppMessageBox.Show(this, message, "WinRM Konfiguration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
