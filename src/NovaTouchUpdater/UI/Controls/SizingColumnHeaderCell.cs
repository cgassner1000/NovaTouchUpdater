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
        style.Padding = new Padding(cellStyle.Padding.Left, cellStyle.Padding.Top,
            cellStyle.Padding.Right + GridRefreshIcon.Space(DataGridView?.DeviceDpi ?? 96), cellStyle.Padding.Bottom);
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
        Rectangle iconBounds = string.IsNullOrEmpty(Convert.ToString(formattedValue)) ? cellBounds : GridRefreshIcon.Bounds(cellBounds, DataGridView?.DeviceDpi ?? 96);
        GridRefreshIcon.Draw(graphics, iconBounds, DataGridView?.DeviceDpi ?? 96, RefreshEnabled);
        graphics.Restore(state);
    }
}
