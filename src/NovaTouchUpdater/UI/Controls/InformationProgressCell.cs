namespace NovaTouchUpdater;

// Use the standard text-cell renderer for padding, DPI, ellipsis and focus.
// The palette keeps progress text readable on both fill colors in either theme.
internal sealed class InformationProgressCell : DataGridViewTextBoxCell
{
    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds,
        int rowIndex, DataGridViewElementStates cellState, object? value, object? formattedValue,
        string? errorText, DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle,
        DataGridViewPaintParts paintParts)
    {
        if (Tag is not int percent || SystemInformation.HighContrast)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue,
                errorText, cellStyle, advancedBorderStyle, paintParts);
            return;
        }

        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue,
            errorText, cellStyle, advancedBorderStyle,
            paintParts & ~(DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.ErrorIcon | DataGridViewPaintParts.Focus));
        if ((paintParts & DataGridViewPaintParts.Background) != 0)
        {
            Rectangle border = BorderWidths(advancedBorderStyle);
            var track = new Rectangle(cellBounds.X + border.X, cellBounds.Y + border.Y,
                Math.Max(0, cellBounds.Width - border.X - border.Width),
                Math.Max(0, cellBounds.Height - border.Y - border.Height));
            bool selected = (cellState & DataGridViewElementStates.Selected) != 0;
            using var background = new SolidBrush(selected ? UiTheme.ProgressSelectedTrack : UiTheme.ProgressTrack);
            using var fill = new SolidBrush(selected ? UiTheme.ProgressSelectedFill : UiTheme.ProgressFill);
            var state = graphics.Save();
            graphics.SetClip(Rectangle.Intersect(clipBounds, track));
            graphics.FillRectangle(background, track);
            graphics.FillRectangle(fill, track.X, track.Y, track.Width * Math.Clamp(percent, 0, 100) / 100, track.Height);
            graphics.Restore(state);
        }
        var textStyle = cellStyle.Clone();
        textStyle.ForeColor = textStyle.SelectionForeColor = UiTheme.ProgressText;
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue,
            errorText, textStyle, advancedBorderStyle,
            paintParts & (DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.ErrorIcon | DataGridViewPaintParts.Focus));
    }
}
