namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private bool operationCancellationRequested;
    private bool CanStartHostScan => !busy && !selfUpdating && !loadingPackages && !preparingDatabaseUpload && !restartingAsAdministrator && !connectionClosing;
    private bool CanRefreshVersions => CanStartHostScan && SelectedHosts().Length > 0;

    private void CancelCurrentOperation()
    {
        operationCancellationRequested = true;
        selfUpdateCancellation?.Cancel();
        cancellation?.Cancel();
        packageCancellation?.Cancel();
        hostScanCancellation?.Cancel();
        InvalidateConnectionChecks();
        UpdateButtons();
    }

    private void ConfigureTableRefresh()
    {
        var heading = (SizingColumnHeaderCell)table.Columns["include"].HeaderCell;
        heading.RefreshAction = true;
        heading.ToolTipText = "Versionen der ausgewählten Hosts prüfen (F5). Die Auswahl bleibt unverändert.";
        table.CellMouseClick += async (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !IsTableRefreshHit(e.RowIndex, e.ColumnIndex, e.X, e.Y)) return;
            await RunScansAsync();
        };
        table.CellMouseMove += (_, e) =>
        {
            table.Cursor = CanRefreshVersions && IsTableRefreshHit(e.RowIndex, e.ColumnIndex, e.X, e.Y) ? Cursors.Hand : Cursors.Default;
        };
        table.MouseLeave += (_, _) => table.Cursor = Cursors.Default;
        table.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.F5) return;
            e.Handled = e.SuppressKeyPress = true;
            await RunScansAsync();
        };
    }

    private bool IsTableRefreshHit(int rowIndex, int columnIndex, int x, int y)
    {
        if (rowIndex != -1 || columnIndex < 0 || table.Columns[columnIndex].Name != "include") return false;
        Rectangle bounds = table.GetCellDisplayRectangle(columnIndex, rowIndex, false);
        return new Rectangle(Point.Empty, bounds.Size).Contains(x, y);
    }

    private Task StartHostScan(HostState[] targets)
    {
        if (!CanStartHostScan || !hostScanTask.IsCompleted) return Task.CompletedTask;
        return hostScanTask = RunScansCoreAsync(targets);
    }

    private void UpdateTableRefresh(bool idle)
    {
        if (table.Columns["include"].HeaderCell is SizingColumnHeaderCell heading)
            heading.RefreshEnabled = idle && SelectedHosts().Length > 0;
        table.InvalidateCell(table.Columns["include"].HeaderCell);
        bool active = busy || selfUpdating || loadingPackages || preparingDatabaseUpload;
        if (!active) operationCancellationRequested = false;
        btnCancel.Text = active && operationCancellationRequested ? "Abbruch läuft …" : "Abbrechen";
        btnCancel.Enabled = active && !operationCancellationRequested;
    }
}
