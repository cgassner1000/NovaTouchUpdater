using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal sealed class ConnectionBadgeCell : StatusBadgeCell
{
    internal int IconSpace => Tag is true ? (int)Math.Ceiling(19 * (DataGridView?.DeviceDpi ?? 96) / 96F) : 0;

    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates cellState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        if (IconSpace == 0)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText, cellStyle, advancedBorderStyle, paintParts);
            return;
        }
        var padded = cellStyle.Clone();
        padded.Padding = new Padding(cellStyle.Padding.Left, cellStyle.Padding.Top, cellStyle.Padding.Right + IconSpace, cellStyle.Padding.Bottom);
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText, padded, advancedBorderStyle, paintParts);
        if ((paintParts & DataGridViewPaintParts.ContentForeground) == 0) return;
        float scale = (DataGridView?.DeviceDpi ?? 96) / 96F;
        float x = cellBounds.Right - cellStyle.Padding.Right - 17 * scale;
        float y = cellBounds.Y + (cellBounds.Height - 14 * scale) / 2;
        var state = graphics.Save();
        graphics.SetClip(Rectangle.Intersect(clipBounds, cellBounds));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color ink = SystemInformation.HighContrast
            ? (cellState & DataGridViewElementStates.Selected) != 0 ? SystemColors.HighlightText : SystemColors.ControlText
            : UiTheme.Success;
        using var pen = new Pen(ink, 1.4F * scale) { LineJoin = LineJoin.Round };
        graphics.DrawArc(pen, x + 3 * scale, y, 6 * scale, 10 * scale, 180, 180);
        using var body = UiTheme.RoundRectangle(new RectangleF(x, y + 5 * scale, 12 * scale, 8 * scale), 2 * scale);
        graphics.DrawPath(pen, body);
        using var brush = new SolidBrush(ink);
        graphics.FillEllipse(brush, x + 5 * scale, y + 7.5F * scale, 2 * scale, 2 * scale);
        graphics.DrawLine(pen, x + 6 * scale, y + 9 * scale, x + 6 * scale, y + 11 * scale);
        graphics.Restore(state);
    }
}
