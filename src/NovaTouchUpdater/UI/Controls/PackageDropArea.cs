using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal sealed class PackageDropArea : Control
{
    private bool acceptingDrop;
    private float ScaleFactor => Math.Max(DeviceDpi / 96F, Font.SizeInPoints / 10.5F);

    internal PackageDropArea()
    {
        Name = "packageDropArea";
        Text = "Pakete hierher ziehen";
        AccessibleName = Text;
        AccessibleDescription = "Eine oder mehrere Paketdateien per Drag & Drop auswählen.";
        AccessibleRole = AccessibleRole.Pane;
        AutoSize = true;
        Dock = DockStyle.Fill;
        Margin = new Padding(3, 0, 3, 8);
        Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(proposedSize.Width > 0 ? proposedSize.Width : (int)Math.Ceiling(320 * ScaleFactor),
            (int)Math.Ceiling(Math.Max(92 * ScaleFactor, Font.Height + 32 * ScaleFactor)));

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        SetAcceptingDrop((e.Effect & DragDropEffects.Copy) != 0);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        SetAcceptingDrop((e.Effect & DragDropEffects.Copy) != 0);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        SetAcceptingDrop(false);
        base.OnDragLeave(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        SetAcceptingDrop(false);
        base.OnDragDrop(e);
    }

    private void SetAcceptingDrop(bool value)
    {
        if (acceptingDrop == value) return;
        acceptingDrop = value;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? BackColor);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = ScaleFactor;
        if (Width < 4 * scale || Height < 4 * scale) return;
        bool contrast = SystemInformation.HighContrast;
        Color accent = contrast ? SystemColors.Highlight : UiTheme.Accent;
        Color fill = contrast ? SystemColors.Window : acceptingDrop ? UiTheme.AccentSoft : Color.FromArgb(248, 250, 255);
        Color border = acceptingDrop || contrast ? accent : Color.FromArgb(186, 205, 238);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.RoundRectangle(new RectangleF(scale, scale, Width - 2 * scale, Height - 2 * scale), 12 * scale);
        using var background = new SolidBrush(fill);
        using var outline = new Pen(border, 1.2F * scale) { DashPattern = [4, 4], DashCap = DashCap.Round };
        e.Graphics.FillPath(background, shape);
        e.Graphics.DrawPath(outline, shape);

        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        int textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        float iconSize = 44 * scale, gap = 14 * scale;
        float contentWidth = Math.Min(textWidth + iconSize + gap, Math.Max(0, Width - 32 * scale));
        float x = (Width - contentWidth) / 2F, y = (Height - iconSize) / 2F;
        using var badge = UiTheme.RoundRectangle(new RectangleF(x, y, iconSize, iconSize), 12 * scale);
        using var badgeBrush = new SolidBrush(acceptingDrop ? accent : contrast ? SystemColors.Control : Color.FromArgb(231, 239, 255));
        e.Graphics.FillPath(badgeBrush, badge);
        using var icon = new Pen(acceptingDrop ? contrast ? SystemColors.HighlightText : Color.White : accent, 2 * scale)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF Point(float dx, float dy) => new(x + dx * scale, y + dy * scale);
        e.Graphics.DrawLine(icon, Point(22, 10), Point(22, 27));
        e.Graphics.DrawLines(icon, [Point(15, 20), Point(22, 27), Point(29, 20)]);
        e.Graphics.DrawLines(icon, [Point(10, 28), Point(10, 34), Point(34, 34), Point(34, 28)]);
        var textBounds = Rectangle.Round(new RectangleF(x + iconSize + gap, 0, Math.Max(1, contentWidth - iconSize - gap), Height));
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
            contrast ? SystemColors.WindowText : acceptingDrop ? accent : UiTheme.Text, flags);
    }
}
