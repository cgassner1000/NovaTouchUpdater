using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal sealed class StatusBadgeCell : DataGridViewTextBoxCell
{
    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds,
        int rowIndex, DataGridViewElementStates cellState, object? value, object? formattedValue,
        string? errorText, DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle,
        DataGridViewPaintParts paintParts)
    {
        string text = Convert.ToString(formattedValue) ?? "";
        if (SystemInformation.HighContrast || string.IsNullOrWhiteSpace(text) || text == "–")
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText, cellStyle, advancedBorderStyle, paintParts);
            return;
        }
        (Color foreground, Color background) = text switch
        {
            "Verbunden" or "Erfolgreich" or "Wieder online" or "Dienste OK" or "POS OK" or "OK" => (UiTheme.Success, UiTheme.SuccessSoft),
            "Nicht erreichbar" or "Fehler" => (UiTheme.Danger, UiTheme.DangerSoft),
            "Aktuell" => (UiTheme.Accent, UiTheme.AccentSoft),
            "Läuft" or "Neustart angefordert" => (UiTheme.Warning, UiTheme.WarningSoft),
            _ => (UiTheme.Muted, Color.FromArgb(239, 243, 248))
        };
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue,
            errorText, cellStyle, advancedBorderStyle, paintParts & ~(DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.ErrorIcon | DataGridViewPaintParts.Focus));
        if ((paintParts & DataGridViewPaintParts.Background) != 0)
        {
            float scale = DataGridView?.DeviceDpi / 96F ?? 1;
            int inset = (int)(5 * scale);
            var badge = Rectangle.Inflate(cellBounds, -inset, -inset);
            var state = graphics.Save();
            graphics.SetClip(Rectangle.Intersect(clipBounds, badge));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = UiTheme.RoundRectangle(badge, 5 * scale);
            using var brush = new SolidBrush(background);
            graphics.FillPath(brush, shape);
            graphics.Restore(state);
        }
        var style = cellStyle.Clone();
        style.ForeColor = style.SelectionForeColor = foreground;
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue,
            errorText, style, advancedBorderStyle, paintParts & (DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.ErrorIcon | DataGridViewPaintParts.Focus));
    }
}
