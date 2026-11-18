namespace NovaTouchUpdater;

internal sealed class HostTable : DataGridView
{
    private int hoveredRow = -1;
    internal Func<int, int, ContextMenuStrip?>? ContextMenuForCell { get; set; }
    internal Func<ContextMenuStrip?>? BackgroundContextMenu { get; set; }
    internal ContextMenuStrip[] ContextMenus { get; set; } = [];

    internal void RefreshContextMenus() { foreach (var menu in ContextMenus) UiTheme.StyleMenu(menu); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHoveredRow(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e) { SetHoveredRow(-1); base.OnMouseLeave(e); }

    protected override void OnScroll(ScrollEventArgs e)
    {
        base.OnScroll(e);
        if (IsHandleCreated) UpdateHoveredRow(PointToClient(MousePosition));
    }

    protected override void OnRowsRemoved(DataGridViewRowsRemovedEventArgs e) { SetHoveredRow(-1); base.OnRowsRemoved(e); }

    private void UpdateHoveredRow(Point point)
    {
        HitTestInfo hit = HitTest(point.X, point.Y);
        SetHoveredRow(hit.Type is DataGridViewHitTestType.Cell or DataGridViewHitTestType.RowHeader && hit.RowIndex != NewRowIndex ? hit.RowIndex : -1);
    }

    private void SetHoveredRow(int row)
    {
        if (hoveredRow == row) return;
        int previous = hoveredRow;
        hoveredRow = row;
        if (previous >= 0 && previous < Rows.Count) InvalidateRow(previous);
        if (row >= 0 && row < Rows.Count) InvalidateRow(row);
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex != hoveredRow || SystemInformation.HighContrast || e.CellStyle is not { } style) { base.OnCellPainting(e); return; }
        Color background = style.BackColor, selected = style.SelectionBackColor;
        try
        {
            style.BackColor = HoverBackground(background);
            style.SelectionBackColor = HoverBackground(selected);
            base.OnCellPainting(e);
            if (!e.Handled) { e.Paint(e.ClipBounds, e.PaintParts); e.Handled = true; }
        }
        finally { style.BackColor = background; style.SelectionBackColor = selected; }
    }

    private static Color HoverBackground(Color background)
    {
        if (background.IsEmpty) background = UiTheme.Surface;
        Color tint = UiTheme.Text;
        return Color.FromArgb(background.A, (background.R * 95 + tint.R * 5) / 100,
            (background.G * 95 + tint.G * 5) / 100, (background.B * 95 + tint.B * 5) / 100);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != 0x007B) { base.WndProc(ref m); return; } // WM_CONTEXTMENU
        Point point;
        if (m.LParam == new IntPtr(-1))
        {
            if (CurrentCell is not { } cell) return;
            Rectangle bounds = GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true);
            point = new Point(bounds.Left + 4, bounds.Top + bounds.Height / 2);
        }
        else
        {
            long position = m.LParam.ToInt64();
            point = PointToClient(new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff)));
        }
        HitTestInfo hit = HitTest(point.X, point.Y);
        var menu = hit.Type == DataGridViewHitTestType.None ? BackgroundContextMenu?.Invoke() : ContextMenuForCell?.Invoke(hit.RowIndex, hit.ColumnIndex);
        menu?.Show(this, point);
        m.Result = IntPtr.Zero;
    }
}
