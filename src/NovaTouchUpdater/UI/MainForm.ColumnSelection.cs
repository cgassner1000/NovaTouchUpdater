namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly ContextMenuStrip hostColumnsMenu = new();
    private Action<string>? showHostColumnSaveError = null;

    private void ConfigureHostColumnSelection()
    {
        hostColumnsMenu.Font = Font;
        hostColumnsMenu.Items.Add(new ToolStripMenuItem("Spalten anzeigen") { Enabled = false });
        hostColumnsMenu.Items.Add(new ToolStripSeparator());
        foreach (DataGridViewColumn column in table.Columns.Cast<DataGridViewColumn>().Where(column => column.Name != "include").OrderBy(column => column.DisplayIndex))
        {
            var item = new ToolStripMenuItem(column.HeaderText)
            { Name = column.Name, Tag = column.Name, CheckOnClick = true };
            item.Click += (_, _) =>
            {
                table.EndEdit();
                column.Visible = item.Checked;
                ApplyColumnWidths();
                SaveHostColumnSelection();
            };
            hostColumnsMenu.Items.Add(item);
        }
        hostColumnsMenu.Items.Add(new ToolStripSeparator());
        var reset = new ToolStripMenuItem("Standard wiederherstellen") { Name = "restoreDefaultHostColumns" };
        reset.Click += (_, _) => { ApplyVisibleHostColumns(null); SaveHostColumnSelection(); };
        hostColumnsMenu.Items.Add(reset);
        hostColumnsMenu.Opening += (_, _) => UpdateHostColumnsMenu();
        table.ContextMenuStrip = hostColumnsMenu;
        table.ColumnStateChanged += (_, e) => { if ((e.StateChanged & DataGridViewElementStates.Visible) != 0) QueueColumnSizing(); };
        ApplyVisibleHostColumns(null);
    }

    private string[] VisibleHostColumns() => table.Columns.Cast<DataGridViewColumn>()
        .Where(column => column.Visible).OrderBy(column => column.DisplayIndex).Select(column => column.Name).ToArray();

    private void UpdateHostColumnsMenu()
    {
        foreach (ToolStripMenuItem item in hostColumnsMenu.Items.OfType<ToolStripMenuItem>())
            if (item.Tag is string name) item.Checked = table.Columns[name].Visible;
    }

    private void ApplyVisibleHostColumns(IEnumerable<string>? names)
    {
        var visible = HostColumnSettings.Normalize(names).ToHashSet(StringComparer.OrdinalIgnoreCase);
        table.SuspendLayout();
        try { foreach (DataGridViewColumn column in table.Columns) column.Visible = column.Name == "include" || visible.Contains(column.Name); }
        finally { table.ResumeLayout(true); }
        UpdateHostColumnsMenu();
        if (table.IsHandleCreated) ApplyColumnWidths();
    }

    private void SaveHostColumnSelection()
    {
        try
        {
            Directory.CreateDirectory(userDataPaths.RootDirectory);
            AppConfigStore.SaveVisibleHostColumns(userDataPaths.ConfigFile, VisibleHostColumns(), startupConfigPath);
            UpdateHostColumnsMenu();
            Log("SYSTEM", "Spaltenauswahl gespeichert: " + userDataPaths.ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            string message = "Die Spaltenauswahl gilt für diese Sitzung, konnte aber nicht gespeichert werden.\n" + ex.Message;
            Log("SYSTEM", "Spaltenauswahl speichern fehlgeschlagen: " + ex.Message);
            if (showHostColumnSaveError is { } show) show(message);
            else MessageBox.Show(this, message, "Spaltenauswahl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
