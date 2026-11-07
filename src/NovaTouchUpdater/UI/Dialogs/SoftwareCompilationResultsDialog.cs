namespace NovaTouchUpdater;

internal sealed record SoftwareCompilationHostOutcome(string Host, WorkerResult Result);

internal sealed class SoftwareCompilationResultsDialog : Form
{
    public SoftwareCompilationResultsDialog(IReadOnlyList<SoftwareCompilationHostOutcome> outcomes)
    {
        SuspendLayout();
        Text = "Softwarepakete kompilieren – Übersicht";
        Font = UiTheme.BodyFont(); BackColor = Color.White;
        ClientSize = new Size(1100, 680); MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var namespaces = outcomes.SelectMany(host => host.Result.SoftwareCompilationResults).ToArray();
        int errors = namespaces.Sum(ns => ns.CompilationErrors.Length);
        layout.Controls.Add(new Label { Name = "summary", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14),
            Text = $"{outcomes.Count} Server · {namespaces.Count(ns => ns.Success)} Namespace(s) erfolgreich · " +
                $"{namespaces.Count(ns => !ns.Success)} Namespace(s) mit Fehlern · {errors} Kompilierungsfehler" }, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var namespaceTab = new TabPage("Namespaces") { Padding = new Padding(8) };
        var errorTab = new TabPage($"Kompilierungsfehler ({errors})") { Padding = new Padding(8) };
        var namespaceTable = Table("namespaces", ("Server", 22), ("Instanz", 20), ("Namespace", 18), ("Ergebnis", 22), ("Pakete", 18));
        var namespaceDetails = DetailBox("");
        foreach (var host in outcomes)
        {
            foreach (var ns in host.Result.SoftwareCompilationResults)
            {
                int row = namespaceTable.Rows.Add(host.Host, ns.Instance, ns.Namespace,
                    ns.Success ? "Abgeschlossen" : "Mit Fehlern", $"{ns.ImportedResources} geladen / {ns.SkippedResources} übersprungen");
                namespaceTable.Rows[row].Tag = ns.Error.Length > 0 ? ns.Error : "Erfolgreich abgeschlossen.";
                if (!ns.Success) namespaceTable.Rows[row].DefaultCellStyle.ForeColor = UiTheme.Danger;
            }
            if (host.Result.SoftwareCompilationResults.Length == 0)
            {
                int row = namespaceTable.Rows.Add(host.Host, "–", "–", host.Result.Skipped ? "Übersprungen" : host.Result.Success ? "Abgeschlossen" : "Fehler", "–");
                namespaceTable.Rows[row].Tag = host.Result.Error.Length > 0 ? host.Result.Error : host.Result.Message;
                if (!host.Result.Success) namespaceTable.Rows[row].DefaultCellStyle.ForeColor = UiTheme.Danger;
            }
        }
        namespaceTab.Controls.Add(WithDetails(namespaceTable, namespaceDetails));
        var errorTable = Table("compilationErrors", ("Server", 17), ("Instanz", 15), ("Namespace", 13), ("Typ", 10), ("Klasse / Routine", 28), ("Paket", 17));
        foreach (var host in outcomes)
            foreach (var ns in host.Result.SoftwareCompilationResults)
                foreach (var failure in ns.CompilationErrors)
                {
                    int row = errorTable.Rows.Add(host.Host, failure.Instance, failure.Namespace, failure.ItemType, failure.Item, failure.Resource);
                    errorTable.Rows[row].Tag = $"{failure.Description}{Environment.NewLine}" +
                        $"Server: {host.Host}{Environment.NewLine}Instanz: {failure.Instance}{Environment.NewLine}" +
                        $"Namespace: {failure.Namespace}{Environment.NewLine}Paket: {failure.Resource}{Environment.NewLine}{Environment.NewLine}{failure.Details}";
                }
        var errorDetails = DetailBox(errors == 0 ? "Keine Kompilierungsfehler." : "");
        errorDetails.Name = "compilationErrorDetails";
        errorTab.Controls.Add(WithDetails(errorTable, errorDetails));
        tabs.TabPages.AddRange([namespaceTab, errorTab]);
        if (errors > 0) tabs.SelectedTab = errorTab;
        layout.Controls.Add(tabs, 0, 1);
        var ok = OkButton();
        layout.Controls.Add(ButtonBar(ok), 0, 2);
        AcceptButton = CancelButton = ok;
        Controls.Add(layout);
        WindowLayout.CompleteInitialization(this);
    }

    private static DataGridView Table(string name, params (string Label, int Weight)[] columns)
    {
        var table = new DataGridView { Name = name, Dock = DockStyle.Fill, ReadOnly = true,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Padding = new Padding(3, 4, 3, 4) } };
        foreach (var column in columns)
            table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Label, FillWeight = column.Weight, SortMode = DataGridViewColumnSortMode.Automatic });
        return table;
    }

    private static TableLayoutPanel WithDetails(DataGridView table, TextBox details)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        panel.Controls.Add(table, 0, 0); panel.Controls.Add(details, 0, 1);
        table.CurrentCellChanged += (_, _) => { if (table.CurrentRow?.Tag is string text) details.Text = text; };
        if (table.Rows.Count > 0) details.Text = Convert.ToString(table.Rows[0].Tag);
        return panel;
    }

    internal static TextBox DetailBox(string text) => new() { Name = "details", Text = text, Dock = DockStyle.Fill,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Margin = new Padding(0, 8, 0, 0) };

    internal static Button OkButton() => new ModernButton { Text = "OK", Name = "ok", DialogResult = DialogResult.OK,
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(22, 7, 22, 7), Margin = Padding.Empty, Role = ButtonRole.Primary };

    internal static FlowLayoutPanel ButtonBar(Button ok)
    {
        var bar = new WrappingFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 14, 0, 0) };
        bar.Controls.Add(ok);
        return bar;
    }
}
