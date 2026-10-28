namespace NovaTouchUpdater;

// A separate dialog keeps diagnostics independent from scan/update/retry state.
internal sealed class WinRmTestForm : Form
{
    private readonly DataGridView grid = new();
    private readonly TextBox details = new();
    private readonly Label summary = new();
    private readonly Label localStatus = new() { Name = "localWinRmStatus", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button cancel = new ModernButton { Text = "Abbrechen" };
    private readonly CancellationTokenSource cancellation = new();
    private bool running = true;
    private int completed;
    private readonly string protocol;
    private readonly int parallelism;
    private readonly WorkerRequest preparation;
    private readonly WorkerRequest[] diagnostics;
    private readonly DataGridViewRow[] rows;
    private string preparationDetails = "";
    private bool preparationFailed;

    public WinRmTestForm(string[] hosts, string username, string password, int parallelism, bool useSsl, int httpsPort = 5986,
        string[]? trustedHosts = null)
    {
        SuspendLayout();
        protocol = useSsl ? $"HTTPS / {httpsPort}" : "HTTP / 5985";
        this.parallelism = Math.Clamp(parallelism, 1, 150);
        preparation = new WorkerRequest { Action = "winrm-prepare", Hosts = (trustedHosts ?? hosts).ToArray() };
        diagnostics = hosts.Select(host => new WorkerRequest
        {
            Action = "winrm-test", Host = host, Username = username, Password = password, UseSsl = useSsl, HttpsPort = httpsPort
        }).ToArray();
        Text = "Test WinRM – " + (useSsl ? "HTTPS" : "HTTP");
        Size = new Size(1100, 700);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = UiTheme.BodyFont();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summary.Dock = DockStyle.Fill;
        summary.AutoSize = true;
        summary.Text = $"{hosts.Length} Hosts; maximal {this.parallelism} Hosts parallel. Remote-Protokoll: {protocol}.\nZuerst lokales WinRM prüfen und TrustedHosts ergänzen, danach die ausgewählten Remote-Hosts testen.";
        layout.Controls.Add(summary, 0, 0);
        localStatus.Text = "Lokales WinRM prüfen und TrustedHosts vorbereiten …";
        localStatus.Margin = new Padding(3, 8, 3, 10);
        layout.Controls.Add(localStatus, 0, 1);
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
        foreach (string title in new[] { "Host", "Protokoll / Port", "TCP", "New-PSSession", "Invoke-Command", "Ergebnis" })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns[5].CellTemplate = new StatusBadgeCell();
        layout.Controls.Add(grid, 0, 2);
        details.Name = "winRmDetails";
        details.Dock = DockStyle.Fill;
        details.MinimumSize = new Size(0, 90);
        details.Multiline = true;
        details.ReadOnly = true;
        details.ScrollBars = ScrollBars.Both;
        details.WordWrap = false;
        layout.Controls.Add(details, 0, 3);
        cancel.Anchor = AnchorStyles.Right;
        layout.Controls.Add(cancel, 0, 4);
        Controls.Add(WindowLayout.CreateScrollArea(layout, grid, details));
        WindowLayout.CompleteInitialization(this);
        rows = hosts.Select(host =>
            grid.Rows[grid.Rows.Add(host, protocol, "Wartet", "Wartet", "Wartet", "Wartet")]).ToArray();
        grid.SelectionChanged += (_, _) => ShowDetails();
        cancel.Click += (_, _) => { if (running) { cancellation.Cancel(); cancel.Enabled = false; } else Close(); };
        FormClosing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            cancellation.Cancel();
            cancel.Enabled = false;
        };
        Disposed += (_, _) => cancellation.Dispose();
        Shown += async (_, _) => await RunDiagnosticsAsync();
    }

    internal async Task RunDiagnosticsAsync(IWorkerRunner? worker = null)
    {
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            using var runner = worker ?? new WorkerRunner("NovaTouchUpdater.WinRmWorker.ps1");
            WorkerResult ready = await RunWithTimeoutAsync(runner, preparation, cancellation.Token);
            preparationDetails = $"Lokale WinRM-Vorbereitung\r\n{ready.Message}\r\n{ready.Error}";
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
                    Post(() => row.Cells[5].Value = "Läuft");
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
                if (row.Cells[5].Value is "Wartet" or "Läuft")
                {
                    row.Cells[5].Value = cancellation.IsCancellationRequested ? "Abgebrochen"
                        : preparationFailed ? "Nicht gestartet" : "Nicht abgeschlossen";
                    row.Tag = $"{row.Cells[0].Value} – {row.Cells[1].Value}\r\n{row.Cells[5].Value}. Es liegt kein vollständiges Testergebnis vor.";
                    for (int column = 2; column <= 4; column++) row.Cells[column].Value = "Nicht bestätigt";
                }
            summary.Text = $"{completed} von {diagnostics.Length} Hosttests abgeschlossen" +
                (cancellation.IsCancellationRequested ? " – abgebrochen." : ".") +
                (preparationFailed ? "\nRemote-Tests wurden wegen eines Fehlers nicht fortgesetzt."
                    : $"\nRemote-Protokoll: {protocol}. Zeile auswählen für vollständige Details.");
            cancel.Enabled = true;
            cancel.Text = "Schließen";
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
        row.Cells[3].Value = result.Session;
        row.Cells[4].Value = result.Command;
        row.Cells[5].Value = result.Success ? "OK" : "Fehler";
        row.Tag = $"{row.Cells[0].Value} – {row.Cells[1].Value}\r\nTCP: {result.Tcp}\r\nNew-PSSession: {result.Session}\r\nInvoke-Command: {result.Command}\r\n\r\n{result.Message}\r\n{result.Error}";
        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = result.Error;
        completed++;
        summary.Text = $"{completed} Hosttests abgeschlossen. Zeile auswählen für vollständige Details.\nTCP, New-PSSession und Invoke-Command werden separat ausgewertet.";
        ShowDetails();
    }

    private void ShowDetails() => details.Text = preparationDetails + "\r\n\r\n" +
        (grid.CurrentRow?.Tag as string ?? "Für diese Zeile liegt noch kein Ergebnis vor.");
}
