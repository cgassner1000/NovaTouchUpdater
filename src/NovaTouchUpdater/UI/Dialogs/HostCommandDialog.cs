namespace NovaTouchUpdater;

internal sealed class HostCommandDialog : Form
{
    private readonly TextBox command = new() { Name = "command", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly TextBox output = new() { Name = "output", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly Button execute = new AppButton { Name = "execute", Text = "Ausführen", AutoSize = true, Role = ButtonRole.Accent };
    private readonly Button close = new AppButton { Name = "close", Text = "Schließen", AutoSize = true };
    private CancellationTokenSource? cancellation;

    internal HostCommandDialog(string host, bool local, Func<string, CancellationToken, Task<WorkerResult>> run)
    {
        SuspendLayout();
        Text = "Eingabeaufforderung – " + host;
        Font = UiTheme.BodyFont();
        ClientSize = new Size(780, 520);
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10),
            Text = "Befehl für " + host + (local ? " · lokal" : " · über WinRM") + "\nAusführung mit cmd.exe im verwendeten Windows-Konto. Keine interaktive Sitzung."
        }, 0, 0);
        command.Font = output.Font = new Font("Consolas", 10F);
        layout.Controls.Add(command, 0, 1);
        layout.Controls.Add(new Label { Text = "Ausgabe", AutoSize = true, Margin = new Padding(0, 12, 0, 6) }, 0, 2);
        layout.Controls.Add(output, 0, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(execute);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        execute.Enabled = false;
        command.TextChanged += (_, _) => execute.Enabled = cancellation is null && !string.IsNullOrWhiteSpace(command.Text);
        command.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; execute.PerformClick(); }
        };
        execute.Click += async (_, _) =>
        {
            if (cancellation is not null || string.IsNullOrWhiteSpace(command.Text)) return;
            cancellation = new CancellationTokenSource();
            execute.Enabled = command.Enabled = false;
            close.Text = "Abbrechen";
            output.Text = "Befehl wird ausgeführt …";
            try
            {
                WorkerResult result = await run(command.Text, cancellation.Token);
                output.Text = result.Success ? result.Message : string.Join(Environment.NewLine, new[] { result.Message, result.Error }.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct());
            }
            catch (OperationCanceledException) { output.Text = "Befehl abgebrochen."; }
            catch (Exception ex) { output.Text = ex.Message; }
            finally
            {
                cancellation.Dispose(); cancellation = null;
                command.Enabled = true;
                execute.Enabled = !string.IsNullOrWhiteSpace(command.Text);
                close.Text = "Schließen"; close.Enabled = true;
                output.SelectionStart = output.TextLength; output.ScrollToCaret();
            }
        };
        close.Click += (_, _) => { if (cancellation is null) Close(); else CancelCommand(); };
        FormClosing += (_, e) => { if (cancellation is not null) { e.Cancel = true; CancelCommand(); } };
        CancelButton = close;
        WindowLayout.CompleteInitialization(this);
        Shown += (_, _) => command.Focus();
    }

    private void CancelCommand()
    {
        cancellation?.Cancel();
        close.Enabled = false;
        close.Text = "Abbruch läuft …";
    }
}
