namespace NovaTouchUpdater;

internal sealed class HostTable : DataGridView
{
    internal Func<int, int, ContextMenuStrip?>? ContextMenuForCell { get; set; }
    internal Func<ContextMenuStrip?>? BackgroundContextMenu { get; set; }
    internal ContextMenuStrip[] ContextMenus { get; set; } = [];

    internal void RefreshContextMenus() { foreach (var menu in ContextMenus) UiTheme.StyleMenu(menu); }

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
