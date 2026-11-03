namespace NovaTouchUpdater;

internal sealed class PackageRemoveButton : Button
{
    private bool hovered;
    private bool pressed;
    internal bool Compact { get; set; }

    internal PackageRemoveButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Compact) { base.OnPaint(e); return; }
        Color background = !Enabled ? SystemColors.Control : pressed ? ControlPaint.Dark(BackColor, .1F)
            : hovered ? ControlPaint.Dark(BackColor, .04F) : BackColor;
        e.Graphics.Clear(background);
        // Native Button text layout reserves chrome even with Padding.Empty.
        // Draw into the full compact box so the unchanged glyph fits in 12 px.
        var glyphBounds = ClientRectangle;
        // Center the × itself, without the extra ascent above its visible strokes.
        glyphBounds.Offset(0, -(int)Math.Round(Font.SizeInPoints * e.Graphics.DpiY / 72F / 8F));
        TextRenderer.DrawText(e.Graphics, Text, Font, glyphBounds,
            Enabled ? ForeColor : SystemColors.GrayText,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1), ForeColor, background);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
}
