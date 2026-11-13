using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal enum WinRmSetupState { None, Ready, Copying, Copied }

internal sealed class WinRmSetupButtonCell : DataGridViewButtonCell
{
    public WinRmSetupButtonCell() => ValueType = typeof(WinRmSetupState);

    protected override AccessibleObject CreateAccessibilityInstance() => new SetupAccessibilityObject(this);

    private sealed class SetupAccessibilityObject(WinRmSetupButtonCell owner) : DataGridViewButtonCellAccessibleObject(owner)
    {
        public override string? Name
        {
            get => owner.Value switch
            {
                WinRmSetupState.Ready => "Client-Einrichtung kopieren",
                WinRmSetupState.Copying => "Einrichtung wird vorbereitet",
                WinRmSetupState.Copied => "Kopiert; erneut kopieren",
                _ => "Keine Client-Einrichtung erforderlich"
            };
            set { }
        }
    }

    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates cellState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        var action = value is WinRmSetupState current ? current : WinRmSetupState.None;
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, "", "", errorText, cellStyle, advancedBorderStyle,
            paintParts & ~(DataGridViewPaintParts.ContentBackground | DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.Focus));
        if (action == WinRmSetupState.None || (paintParts & DataGridViewPaintParts.ContentForeground) == 0) return;
        float scale = DataGridView?.DeviceDpi / 96F ?? 1;
        float size = Math.Min(30 * scale, Math.Min(cellBounds.Width, cellBounds.Height) - 8 * scale);
        if (size < 12 * scale) return;
        var bounds = new RectangleF(cellBounds.X + (cellBounds.Width - size) / 2, cellBounds.Y + (cellBounds.Height - size) / 2, size, size);
        var state = graphics.Save();
        graphics.SetClip(RectangleF.Intersect(clipBounds, bounds));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.RoundRectangle(bounds, 5 * scale);
        bool selected = (cellState & DataGridViewElementStates.Selected) != 0;
        Color ink = SystemInformation.HighContrast ? selected ? SystemColors.HighlightText : SystemColors.ControlText
            : action == WinRmSetupState.Copied ? UiTheme.Success : UiTheme.AccentButtonText;
        using var fill = new SolidBrush(SystemInformation.HighContrast ? selected ? SystemColors.Highlight : SystemColors.Control
            : action == WinRmSetupState.Copied ? UiTheme.SuccessSoft : UiTheme.AccentSoft);
        graphics.FillPath(fill, shape);
        using var pen = new Pen(ink, 1.7F * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2;
        if (action == WinRmSetupState.Copied)
            graphics.DrawLines(pen, new PointF[] { new(x - 7 * scale, y), new(x - 2 * scale, y + 5 * scale), new(x + 7 * scale, y - 5 * scale) });
        else if (action == WinRmSetupState.Copying)
            graphics.DrawArc(pen, x - 7 * scale, y - 7 * scale, 14 * scale, 14 * scale, -70, 270);
        else
        {
            graphics.DrawLines(pen, new PointF[] { new(x - 6 * scale, y + 3 * scale), new(x - 8 * scale, y + 3 * scale),
                new(x - 8 * scale, y - 8 * scale), new(x + 3 * scale, y - 8 * scale), new(x + 3 * scale, y - 6 * scale) });
            using var front = UiTheme.RoundRectangle(new RectangleF(x - 4 * scale, y - 4 * scale, 12 * scale, 12 * scale), 2 * scale);
            graphics.DrawPath(pen, front);
        }
        if ((paintParts & DataGridViewPaintParts.Focus) != 0 && DataGridView?.CurrentCell == this && DataGridView.Focused)
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Ceiling(RectangleF.Inflate(bounds, 2 * scale, 2 * scale)));
        graphics.Restore(state);
    }
}
