using System.Globalization;

namespace NovaTouchUpdater;

internal sealed class HostLogDialog : Form
{
    private sealed record Entry(string Raw, string Time, string Phase, string Message)
    {
        internal static Entry Parse(string raw)
        {
            string time = "–", message = raw;
            if (raw.Length > 25 && DateTime.TryParseExact(raw[..23], "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime timestamp))
            {
                int end = raw.IndexOf("] ", 24, StringComparison.Ordinal);
                if (end >= 0)
                {
                    time = timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    message = raw[(end + 2)..];
                }
            }
            int separator = message.IndexOf(": ", StringComparison.Ordinal);
            return separator is > 0 and < 90
                ? new(raw, time, message[..separator], message[(separator + 2)..])
                : new(raw, time, "", message);
        }
    }

    private readonly Func<int, string[]> readEntries;
    private readonly List<Entry> entries = [];
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 500 };
    private readonly AppToggleSwitch autoRefresh = new() { Name = "autoRefresh", Text = "Auto-Refresh", Checked = true, Margin = new Padding(0, 7, 18, 7) };
    private readonly Label summary = new() { Name = "logSummary", AutoSize = true, ForeColor = UiTheme.Muted, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 12) };
    private readonly DataGridView table = new()
    {
        Name = "logEntries", Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 42 },
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText, Margin = Padding.Empty
    };
    private readonly TextBox details = new()
    {
        Name = "logDetails", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = new Font("Consolas", 9.5F), Margin = Padding.Empty
    };
    private readonly ToolTip tips = new();
    private string? selectedRaw;

    internal HostLogDialog(string host, Func<int, string[]> readEntries)
    {
        this.readEntries = readEntries;
        SuspendLayout();
        Text = "Protokoll – " + host;
        Font = UiTheme.BodyFont();
        ClientSize = new Size(1120, 700); MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(18) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (SizeType type in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize })
            layout.RowStyles.Add(new RowStyle(type));
        layout.RowStyles[3].Height = 72; layout.RowStyles[5].Height = 28;
        layout.Controls.Add(new Label { Text = "Protokoll · " + host, AutoSize = true, Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        layout.Controls.Add(summary, 0, 1);

        var toolbar = new WrappingFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        var refresh = new AppButton { Name = "refreshLog", Text = "Aktualisieren", AutoSize = true, Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 0, 8, 0) };
        var copy = new AppButton { Name = "copyLog", Text = "Protokoll kopieren", AutoSize = true, Padding = new Padding(12, 7, 12, 7), Margin = Padding.Empty };
        refresh.Click += (_, _) => RefreshLog();
        copy.Click += (_, _) =>
        {
            try
            {
                string text = string.Join(Environment.NewLine, readEntries(0));
                if (text.Length == 0) return;
                Clipboard.SetText(text); summary.Text += " · Protokoll kopiert";
            }
            catch (System.Runtime.InteropServices.ExternalException) { AppMessageBox.Show(this, "Die Zwischenablage ist gerade belegt. Bitte erneut versuchen.", "Protokoll kopieren", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        toolbar.Controls.AddRange([autoRefresh, refresh, copy]);
        layout.Controls.Add(toolbar, 0, 2);
        tips.SetToolTip(autoRefresh, "Neue Einträge automatisch anzeigen und zum Ende scrollen. Zum Lesen älterer Einträge ausschalten.");
        tips.SetToolTip(refresh, "Neue Protokolleinträge laden. Bei ausgeschaltetem Auto-Refresh bleiben Auswahl und Leseposition erhalten.");

        table.Columns.Add(new DataGridViewTextBoxColumn { Name = "time", HeaderText = "Zeit", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        table.Columns.Add(new DataGridViewTextBoxColumn { Name = "phase", HeaderText = "Schritt", FillWeight = 28, MinimumWidth = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        table.Columns.Add(new DataGridViewTextBoxColumn { Name = "message", HeaderText = "Meldung", FillWeight = 72, MinimumWidth = 180, SortMode = DataGridViewColumnSortMode.NotSortable });
        table.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= entries.Count) return;
            Entry entry = entries[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => entry.Time, 1 => entry.Phase, _ => entry.Message };
        };
        table.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < entries.Count) e.ToolTipText = entries[e.RowIndex].Raw; };
        table.CellFormatting += (_, e) =>
        {
            if (SystemInformation.HighContrast || e.RowIndex < 0 || e.RowIndex >= entries.Count) return;
            Entry entry = entries[e.RowIndex];
            if (entry.Phase.Contains("Fehler", StringComparison.OrdinalIgnoreCase) || entry.Raw.Contains("=> FEHLER", StringComparison.Ordinal) || entry.Message.StartsWith("FEHLER:", StringComparison.Ordinal))
                e.CellStyle!.ForeColor = UiTheme.Danger;
            else if (entry.Phase.Contains("Warnung", StringComparison.OrdinalIgnoreCase) || entry.Phase == "Setup wiederholen")
                e.CellStyle!.ForeColor = UiTheme.Warning;
        };
        table.CurrentCellChanged += (_, _) => ShowSelection();
        layout.Controls.Add(table, 0, 3);
        layout.Controls.Add(new Label { Text = "Ausgewählter Eintrag · vollständig und kopierbar", AutoSize = true, Margin = new Padding(0, 12, 0, 6) }, 0, 4);
        layout.Controls.Add(details, 0, 5);
        var close = new AppButton { Name = "closeLog", Text = "Schließen", AutoSize = true, Padding = new Padding(16, 7, 16, 7), DialogResult = DialogResult.OK };
        layout.Controls.Add(SoftwareCompilationResultsDialog.ButtonBar(close), 0, 6);
        CancelButton = close;
        Controls.Add(layout);
        autoRefresh.CheckedChanged += (_, _) =>
        {
            refreshTimer.Enabled = autoRefresh.Checked && Visible;
            if (autoRefresh.Checked) { RefreshLog(); FollowLatest(); }
            else UpdateSummary();
        };
        refreshTimer.Tick += (_, _) => RefreshLog();
        WindowLayout.CompleteInitialization(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshLog();
        refreshTimer.Enabled = autoRefresh.Checked;
    }

    internal void RefreshLog()
    {
        int firstVisible = table.FirstDisplayedScrollingRowIndex;
        string[] added = readEntries(entries.Count);
        if (added.Length > 0)
        {
            entries.AddRange(added.Select(Entry.Parse));
            table.RowCount = entries.Count;
            if (autoRefresh.Checked) FollowLatest();
            else if (firstVisible >= 0) table.FirstDisplayedScrollingRowIndex = firstVisible;
        }
        if (entries.Count == 0) details.Text = "Noch keine Protokolleinträge vorhanden.";
        UpdateSummary();
    }

    private void FollowLatest()
    {
        if (entries.Count == 0) return;
        table.CurrentCell = table[0, entries.Count - 1];
        table.FirstDisplayedScrollingRowIndex = Math.Max(0, entries.Count - Math.Max(1, table.DisplayedRowCount(false)));
        ShowSelection();
    }

    private void ShowSelection()
    {
        int index = table.CurrentCell?.RowIndex ?? -1;
        if (index < 0 || index >= entries.Count || selectedRaw == entries[index].Raw) return;
        selectedRaw = entries[index].Raw;
        details.Text = selectedRaw;
        details.SelectionStart = 0;
    }

    private void UpdateSummary() => summary.Text = $"{entries.Count:N0} Einträge · " +
        (autoRefresh.Checked ? "Live · Neue Einträge werden automatisch angezeigt" : "Pausiert · Auto-Refresh einschalten oder manuell aktualisieren");

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refreshTimer.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }
}
