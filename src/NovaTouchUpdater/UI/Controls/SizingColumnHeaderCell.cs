namespace NovaTouchUpdater;

internal sealed class SizingColumnHeaderCell : DataGridViewColumnHeaderCell
{
    internal bool RefreshAction { get; set; }
    internal bool RefreshEnabled { get; set; }
    // Measure an unwrapped heading without changing the live grid's styles.
    // Temporarily switching wrapping can trigger another layout while resizing.
    internal int SingleLineWidth(Graphics graphics, Padding padding)
    {
        DataGridViewCellStyle style = InheritedStyle.Clone();
        style.WrapMode = DataGridViewTriState.False;
        style.Padding = padding;
        return GetPreferredSize(graphics, style, -1, Size.Empty).Width;
    }

    protected override Size GetPreferredSize(Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex, Size constraintSize)
        => base.GetPreferredSize(graphics, WithActionPadding(cellStyle), rowIndex, constraintSize);

    private DataGridViewCellStyle WithActionPadding(DataGridViewCellStyle cellStyle)
    {
        if (!RefreshAction) return cellStyle;
        var style = cellStyle.Clone();
        int inset = (int)Math.Ceiling(2 * (DataGridView?.DeviceDpi ?? 96) / 96F);
        style.Padding = new Padding(inset, cellStyle.Padding.Top, inset, cellStyle.Padding.Bottom);
        return style;
    }

    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates cellState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        var style = WithActionPadding(cellStyle);
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText, style, advancedBorderStyle, paintParts);
        if (!RefreshAction || (paintParts & DataGridViewPaintParts.ContentForeground) == 0) return;
        var state = graphics.Save();
        graphics.SetClip(Rectangle.Intersect(clipBounds, cellBounds));
        GridRefreshIcon.Draw(graphics, cellBounds, DataGridView?.DeviceDpi ?? 96, RefreshEnabled);
        graphics.Restore(state);
    }
}
