using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal static class GridRefreshIcon
{
    internal static void Draw(Graphics graphics, Rectangle bounds, int dpi, bool enabled)
    {
        float scale = dpi / 96F;
        float size = Math.Min(24 * scale, Math.Min(bounds.Width, bounds.Height) - 4 * scale);
        if (size <= 0) return;
        float unit = size / 20F;
        float x = bounds.X + (bounds.Width - size) / 2;
        float y = bounds.Y + (bounds.Height - size) / 2;
        var state = graphics.Save();
        graphics.SetClip(bounds, CombineMode.Intersect);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = SystemInformation.HighContrast ? enabled ? SystemColors.ControlText : SystemColors.GrayText
            : enabled ? UiTheme.Accent : UiTheme.Muted;
        using var pen = new Pen(color, 1.6F * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(pen, x + 2.5F * unit, y + 2.5F * unit, 15 * unit, 15 * unit, 45, 270);
        using var arrow = new SolidBrush(color);
        graphics.FillPolygon(arrow, new[]
        {
            new PointF(x + 18 * unit, y + 7.4F * unit),
            new PointF(x + 12.2F * unit, y + 7.4F * unit),
            new PointF(x + 18 * unit, y + 1.6F * unit)
        });
        graphics.Restore(state);
    }
}
