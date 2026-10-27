namespace NovaTouchUpdater;

internal sealed class LdapComputerSearchDialog : Form
{
    private readonly ComputerDiscovery discovery;
    private readonly ComboBox source = new() { Name = "searchSource", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox networkRange = new() { Name = "networkRange", Dock = DockStyle.Fill, PlaceholderText = "Leer = aktive IPv4-Netze; z. B. 192.168.1.0/24" };
    private readonly TextBox domain = new() { Name = "domain", Dock = DockStyle.Fill, PlaceholderText = "Leer = aktuelle Domäne" };
    private readonly TextBox searchBase = new() { Name = "searchBase", Dock = DockStyle.Fill, PlaceholderText = "Optional, z. B. OU=Kassen,DC=firma,DC=local" };
    private readonly TextBox pattern = new() { Name = "pattern", Dock = DockStyle.Fill, PlaceholderText = "z. B. PC*-RST-POS-* oder SERVER-*" };
    private readonly TextBox customFilter = new()
    {
        Name = "customFilter", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font("Consolas", 9F), PlaceholderText = "z. B. (&(name=SERVER-*)(operatingSystem=*Windows*))"
    };
    private readonly TabControl modes = new ModernTabs { Name = "modes", Dock = DockStyle.Fill, Height = 145 };
    private readonly DataGridView results = new() { Name = "results" };
    private readonly Label status = new() { Name = "searchStatus", AutoSize = true, Dock = DockStyle.Fill, Text = "Noch keine Suche gestartet." };
    private readonly Label selection = new() { Name = "selectedCount", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button find = MakeButton("Suchen", "find");
    private readonly Button stop = MakeButton("Suche abbrechen", "stop");
    private readonly Button selectAll = MakeButton("Alle auswählen", "selectAll");
    private readonly Button clearSelection = MakeButton("Auswahl aufheben", "clearSelection");
    private readonly Button import = MakeButton("Auswahl übernehmen", "import");
    private CancellationTokenSource? pending;
    private bool changingRows;
    private bool stale = true;

    internal string[] SelectedHosts { get; private set; } = [];

    internal LdapComputerSearchDialog(ComputerDiscovery? discovery = null)
    {
        this.discovery = discovery ?? new ComputerDiscovery();
        SuspendLayout();
        Text = "Computer suchen – Domäne oder lokales Netzwerk";
        Font = UiTheme.BodyFont();
        ClientSize = new Size(960, 730);
        MinimumSize = new Size(700, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 7, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 7; row++) layout.RowStyles.Add(new RowStyle(row == 4 ? SizeType.Percent : SizeType.AutoSize, row == 4 ? 100 : 0));
        layout.Controls.Add(new Label
        {
            Text = "Automatisch: Domäne durchsuchen; ohne erkannte Domäne das lokale Netzwerk. Gewünschte Computer per Zeile oder Kontrollkästchen auswählen.",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 10)
        }, 0, 0);

        var scope = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 4 };
        scope.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scope.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        source.Items.AddRange(["Automatisch (Domäne, sonst Netzwerk)", "Domäne (LDAP)", "Lokales Netzwerk"]);
        source.SelectedIndex = 0;
        scope.Controls.Add(new Label { Text = "Suchen in:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        scope.Controls.Add(source, 1, 0);
        scope.Controls.Add(new Label { Text = "Domäne / Domaincontroller:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        scope.Controls.Add(domain, 1, 1);
        scope.Controls.Add(new Label { Text = "Suchbasis (DN):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        scope.Controls.Add(searchBase, 1, 2);
        scope.Controls.Add(new Label { Text = "IP-Bereich (optional):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        scope.Controls.Add(networkRange, 1, 3);
        layout.Controls.Add(scope, 0, 1);

        var names = new TabPage("Namensmuster") { Padding = new Padding(8) };
        var nameLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        nameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nameLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        nameLayout.Controls.Add(pattern, 0, 0);
        nameLayout.Controls.Add(new Label { Text = "Das Muster gilt für Domäne und Netzwerk. * steht für beliebig viele Zeichen. Leer oder * = alle gefundenen Geräte. Im Netzwerk können Firewalls oder fehlende Namen Treffer verhindern.", AutoSize = true, Dock = DockStyle.Fill }, 0, 1);
        names.Controls.Add(nameLayout);
        var advanced = new TabPage("Erweiterte Suche (LDAP, nur Domäne)") { Padding = new Padding(8) };
        var filterLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        filterLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        filterLayout.Controls.Add(new Label { Text = "Nur für Domänen: eigenen LDAP-Filter eingeben. Die Suche wird zusätzlich auf Computer beschränkt.", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        filterLayout.Controls.Add(customFilter, 0, 1);
        advanced.Controls.Add(filterLayout);
        modes.TabPages.Add(names);
        modes.TabPages.Add(advanced);
        modes.SelectedIndex = 0;
        layout.Controls.Add(modes, 0, 2);

        var searchButtons = Flow(find, stop);
        layout.Controls.Add(searchButtons, 0, 3);
        results.Dock = DockStyle.Fill;
        results.MinimumSize = new Size(200, 140);
        results.AllowUserToAddRows = results.AllowUserToDeleteRows = false;
        results.RowHeadersVisible = false;
        results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        results.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        results.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        results.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        results.MultiSelect = false;
        results.EditMode = DataGridViewEditMode.EditOnEnter;
        results.BackgroundColor = Color.White;
        results.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "include", HeaderText = "Auswahl", AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells,
            DefaultCellStyle = new DataGridViewCellStyle { NullValue = false, Padding = Padding.Empty, Alignment = DataGridViewContentAlignment.MiddleCenter }
        });
        foreach (var column in new[] { ("name", "Computer"), ("dns", "DNS-Name"), ("os", "Betriebssystem"), ("description", "Beschreibung") })
            results.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Item1, HeaderText = column.Item2, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
        layout.Controls.Add(results, 0, 4);
        status.Margin = new Padding(3, 8, 3, 8);
        layout.Controls.Add(status, 0, 5);
        var cancel = MakeButton("Abbrechen", "cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var footer = Flow(selectAll, clearSelection, selection, import, cancel);
        layout.Controls.Add(footer, 0, 6);
        Controls.Add(WindowLayout.CreateScrollArea(layout, results));
        CancelButton = cancel;
        AcceptButton = find;
        ((ModernButton)find).Role = ButtonRole.Primary;
        ((ModernButton)import).Role = ButtonRole.Accent;

        find.Click += async (_, _) => await SearchAsync();
        stop.Click += (_, _) => CancelSearch();
        selectAll.Click += (_, _) => SetAllSelected(true);
        clearSelection.Click += (_, _) => SetAllSelected(false);
        import.Click += (_, _) =>
        {
            if (!import.Enabled) return;
            results.EndEdit();
            SelectedHosts = GetSelectedHosts();
            if (SelectedHosts.Length == 0) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        results.CurrentCellDirtyStateChanged += (_, _) => { if (results.IsCurrentCellDirty) results.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        results.CellValueChanged += (_, _) => { if (!changingRows) UpdateSelection(); };
        results.CellClick += (_, e) => { if (e.ColumnIndex > 0) ToggleSelectedRow(e.RowIndex); };
        results.KeyDown += (_, e) =>
        {
            // The checkbox column retains its native mouse, keyboard and accessibility
            // behavior. Space on a read-only text cell selects the same computer.
            if (e.KeyCode != Keys.Space || results.CurrentCell is not { ColumnIndex: > 0 } cell) return;
            ToggleSelectedRow(cell.RowIndex);
            e.Handled = e.SuppressKeyPress = true;
        };
        foreach (TextBox input in new[] { domain, searchBase, pattern, customFilter, networkRange }) input.TextChanged += (_, _) => InvalidateResults();
        source.SelectedIndexChanged += (_, _) =>
        {
            if (source.SelectedIndex == (int)ComputerSearchSource.LocalNetwork) modes.SelectedIndex = 0;
            UpdateScopeControls();
            InvalidateResults();
        };
        modes.Selecting += (_, e) => { if (source.SelectedIndex == (int)ComputerSearchSource.LocalNetwork && e.TabPageIndex == 1) e.Cancel = true; };
        modes.SelectedIndexChanged += (_, _) =>
        {
            if (modes.SelectedIndex == 1 && customFilter.TextLength == 0)
                customFilter.Text = string.IsNullOrWhiteSpace(pattern.Text) ? "(name=*)" : LdapComputerQuery.ForPattern(pattern.Text);
            InvalidateResults();
        };
        FormClosing += (_, _) => CancelSearch();
        Disposed += (_, _) => CancelSearch();
        SetSearching(false);
        WindowLayout.CompleteInitialization(this);
        Shown += (_, _) => pattern.Focus();
    }

    internal async Task SearchAsync()
    {
        if (pending is not null || IsDisposed) return;
        var options = new ComputerSearchOptions((ComputerSearchSource)source.SelectedIndex, domain.Text, searchBase.Text,
            modes.SelectedIndex == 0 ? pattern.Text : customFilter.Text, modes.SelectedIndex == 1, networkRange.Text);
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        results.Rows.Clear();
        stale = true;
        SetSearching(true);
        SetStatus("Suche wird vorbereitet …", false);
        var progress = new SearchProgress(message =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!IsDisposed && ReferenceEquals(pending, cancellation) && !cancellation.IsCancellationRequested) SetStatus(message, false);
                }));
            }
            catch (InvalidOperationException) { /* Dialog is closing. */ }
        });
        try
        {
            var result = await discovery.SearchAsync(options, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            if (!result.Success) throw new InvalidOperationException(result.Error);
            changingRows = true;
            try
            {
                foreach (LdapComputer computer in result.Computers ?? [])
                {
                    if (string.IsNullOrWhiteSpace(computer.Host) || computer.Host.Any(char.IsWhiteSpace)) continue;
                    int row = results.Rows.Add(false, computer.Name, computer.DnsName, computer.OperatingSystem, computer.Description);
                    results.Rows[row].Tag = computer;
                }
            }
            finally { changingRows = false; }
            stale = false;
            SetStatus(result.Truncated
                ? $"Mindestens {results.Rows.Count} Treffer. Es werden die ersten {results.Rows.Count} angezeigt; bitte die Suche eingrenzen. Suchbasis: {result.SearchBase}"
                : $"{results.Rows.Count} Computer / Geräte gefunden. Suchbereich: {result.SearchBase}", false);
        }
        catch (OperationCanceledException) { if (!IsDisposed) SetStatus("Suche abgebrochen.", false); }
        catch (Exception ex) { if (!IsDisposed) SetStatus("Suche fehlgeschlagen: " + ex.Message, true); }
        finally
        {
            pending = null;
            if (!IsDisposed) SetSearching(false);
        }
    }

    internal void CancelSearch() => pending?.Cancel();

    internal void SetAllSelected(bool value)
    {
        if (pending is not null || stale) return;
        changingRows = true;
        try { foreach (DataGridViewRow row in results.Rows) row.Cells[0].Value = value; }
        finally { changingRows = false; }
        UpdateSelection();
    }

    internal string[] GetSelectedHosts() => results.Rows.Cast<DataGridViewRow>()
        .Where(row => row.Cells[0].Value is true && row.Tag is LdapComputer)
        .Select(row => ((LdapComputer)row.Tag!).Host).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void ToggleSelectedRow(int rowIndex)
    {
        if (pending is not null || stale || changingRows || rowIndex < 0 || rowIndex >= results.Rows.Count) return;
        DataGridViewCell cell = results.Rows[rowIndex].Cells["include"];
        cell.Value = cell.Value is not true;
    }

    private void InvalidateResults()
    {
        stale = true;
        if (results.Rows.Count > 0) SetStatus("Suchangaben geändert. Bitte erneut suchen.", false);
        UpdateSelection();
    }

    private void SetSearching(bool active)
    {
        find.Enabled = !active;
        stop.Enabled = active;
        source.Enabled = modes.Enabled = results.Enabled = !active;
        UpdateScopeControls();
        UpdateSelection();
    }

    private void UpdateScopeControls()
    {
        bool idle = pending is null;
        domain.Enabled = searchBase.Enabled = idle && source.SelectedIndex != (int)ComputerSearchSource.LocalNetwork;
        networkRange.Enabled = idle && source.SelectedIndex != (int)ComputerSearchSource.Domain;
    }

    private void UpdateSelection()
    {
        int count = GetSelectedHosts().Length;
        selection.Text = $"{count} ausgewählt";
        import.Enabled = pending is null && !stale && count > 0;
        selectAll.Enabled = clearSelection.Enabled = pending is null && !stale && results.Rows.Count > 0;
    }

    private void SetStatus(string message, bool error)
    {
        status.Text = message;
        status.ForeColor = error ? Color.Firebrick : Color.FromArgb(75, 89, 107);
    }

    private static Button MakeButton(string text, string name) => new ModernButton { Name = name, Text = text };
    private sealed class SearchProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new WrappingFlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }
}
