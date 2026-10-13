namespace NovaTouchUpdater;

// A separate dialog keeps diagnostics independent from scan/update/retry state.
internal sealed class WinRmTestForm : Form
{
    private readonly DataGridView grid = new();
    private readonly TextBox details = new();
    private readonly Label summary = new();
    private readonly Button cancel = new() { Text = "Abbrechen", AutoSize = true };
    private readonly CancellationTokenSource cancellation = new();
    private bool running = true;
    private int completed;

    public WinRmTestForm(string[] hosts, string username, string password, int parallelism)
    {
        Text = "Test WinRM – HTTP und HTTPS";
        Size = new Size(1100, 700);
        MinimumSize = new Size(850, 550);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        summary.Dock = DockStyle.Fill;
        summary.Text = $"{hosts.Length} Hosts; maximal {parallelism} Hosts parallel. Beide Protokolle werden unabhängig geprüft.\nZeile auswählen für vollständige Details. Der Testbefehl gibt ausschließlich einen konstanten Text zurück.";
        layout.Controls.Add(summary, 0, 0);
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (string title in new[] { "Host", "Protokoll / Port", "TCP", "New-PSSession", "Invoke-Command", "Ergebnis" })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable });
        layout.Controls.Add(grid, 0, 1);
        details.Dock = DockStyle.Fill;
        details.Multiline = true;
        details.ReadOnly = true;
        details.ScrollBars = ScrollBars.Both;
        details.WordWrap = false;
        layout.Controls.Add(details, 0, 2);
        cancel.Anchor = AnchorStyles.Right;
        layout.Controls.Add(cancel, 0, 3);
        Controls.Add(layout);
        var rows = hosts.Select(host => new[]
        {
            grid.Rows[grid.Rows.Add(host, "HTTP / 5985", "Wartet", "Wartet", "Wartet", "Wartet")],
            grid.Rows[grid.Rows.Add(host, "HTTPS / 5986", "Wartet", "Wartet", "Wartet", "Wartet")]
        }).ToArray();
        grid.SelectionChanged += (_, _) => ShowDetails();
        cancel.Click += (_, _) => { if (running) { cancellation.Cancel(); cancel.Enabled = false; } else Close(); };
        FormClosing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            cancellation.Cancel();
            cancel.Enabled = false;
        };
        FormClosed += (_, _) => cancellation.Dispose();
        Shown += async (_, _) =>
        {
            try
            {
                using var runner = new WorkerRunner("NovaTouchUpdater.WinRmWorker.ps1");
                await Parallel.ForEachAsync(Enumerable.Range(0, hosts.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellation.Token },
                    async (index, token) =>
                    {
                        for (int transport = 0; transport < 2; transport++)
                        {
                            token.ThrowIfCancellationRequested();
                            var row = rows[index][transport];
                            Post(() => row.Cells[5].Value = "Läuft");
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(60));
                            WorkerResult result;
                            try
                            {
                                result = await runner.RunAsync(new WorkerRequest
                                {
                                    Action = "winrm-test", Host = hosts[index], Username = username,
                                    Password = password, UseSsl = transport == 1
                                }, _ => { }, timeout.Token);
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested)
                            {
                                result = new WorkerResult { Error = "Gesamtzeitlimit von 60 Sekunden für dieses Protokoll überschritten. Der lokale Testprozess wurde beendet; noch laufende Schritte sind nicht bestätigt." };
                            }
                            Post(() => DisplayResult(row, result));
                        }
                    });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                details.Text = ex.ToString();
                MessageBox.Show(this, ex.Message, "WinRM-Test", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                password = "";
                running = false;
                foreach (var row in rows.SelectMany(r => r))
                    if (row.Cells[5].Value is "Wartet" or "Läuft")
                    {
                        row.Cells[5].Value = cancellation.IsCancellationRequested ? "Abgebrochen" : "Nicht abgeschlossen";
                        row.Tag = $"{row.Cells[0].Value} – {row.Cells[1].Value}\r\n{row.Cells[5].Value}. Es liegt kein vollständiges Testergebnis vor.";
                        for (int column = 2; column <= 4; column++) row.Cells[column].Value = "Nicht bestätigt";
                    }
                summary.Text = $"{completed} von {hosts.Length * 2} Protokolltests abgeschlossen" +
                    (cancellation.IsCancellationRequested ? " – abgebrochen." : ".") +
                    "\nZeile auswählen für vollständige Details. HTTP und HTTPS sind getrennte Ergebnisse.";
                cancel.Enabled = true;
                cancel.Text = "Schließen";
                ShowDetails();
            }
        };
    }

    private void Post(Action action) => Invoke(action);

    private void DisplayResult(DataGridViewRow row, WorkerResult result)
    {
        row.Cells[2].Value = result.Tcp;
        row.Cells[3].Value = result.Session;
        row.Cells[4].Value = result.Command;
        row.Cells[5].Value = result.Success ? "OK" : "Fehler";
        row.DefaultCellStyle.BackColor = result.Success ? Color.Honeydew : Color.MistyRose;
        row.Tag = $"{row.Cells[0].Value} – {row.Cells[1].Value}\r\nTCP: {result.Tcp}\r\nNew-PSSession: {result.Session}\r\nInvoke-Command: {result.Command}\r\n\r\n{result.Message}\r\n{result.Error}";
        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = result.Error;
        completed++;
        summary.Text = $"{completed} Protokolltests abgeschlossen. Zeile auswählen für vollständige Details.\nTCP, New-PSSession und Invoke-Command werden separat ausgewertet.";
        ShowDetails();
    }

    private void ShowDetails() => details.Text = grid.CurrentRow?.Tag as string ?? "Für diese Zeile liegt noch kein Ergebnis vor.";
}
