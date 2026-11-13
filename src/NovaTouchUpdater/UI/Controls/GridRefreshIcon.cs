using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal static class GridRefreshIcon
{
    internal static int Space(int dpi) => (int)Math.Ceiling(24 * dpi / 96F);

    internal static Rectangle Bounds(Rectangle cell, int dpi)
    {
        int width = Math.Min(Space(dpi), cell.Width);
        return new Rectangle(cell.Right - width, cell.Top, width, cell.Height);
    }

    internal static void Draw(Graphics graphics, Rectangle bounds, int dpi, bool enabled)
    {
        float scale = dpi / 96F;
        float x = bounds.X + (bounds.Width - 13 * scale) / 2;
        float y = bounds.Y + (bounds.Height - 13 * scale) / 2;
        var state = graphics.Save();
        graphics.SetClip(bounds, CombineMode.Intersect);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = SystemInformation.HighContrast ? enabled ? SystemColors.ControlText : SystemColors.GrayText
            : enabled ? UiTheme.Accent : UiTheme.Muted;
        using var pen = new Pen(color, 1.5F * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawArc(pen, x, y, 12 * scale, 12 * scale, 35, 285);
        graphics.DrawLines(pen, new[] { new PointF(x + 8 * scale, y + 1 * scale), new PointF(x + 12 * scale, y + 1 * scale), new PointF(x + 12 * scale, y - 3 * scale) });
        graphics.Restore(state);
    }
}
