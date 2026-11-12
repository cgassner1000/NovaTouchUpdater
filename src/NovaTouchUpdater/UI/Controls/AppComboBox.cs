namespace NovaTouchUpdater;

// Keep the native list, focus, keyboard and accessibility behavior; paint its chrome and items.
internal sealed class AppComboBox : ComboBox
{
    internal AppComboBox()
    {
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = FontHeight + 2;
        RefreshTheme();
    }

    internal void RefreshTheme()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, !SystemInformation.HighContrast);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = FontHeight + Math.Max(2, 2 * DeviceDpi / 96);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool contrast = SystemInformation.HighContrast;
        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using var brush = new SolidBrush(contrast ? selected ? SystemColors.Highlight : SystemColors.Window : selected ? UiTheme.AccentSoft : UiTheme.Input);
        e.Graphics.FillRectangle(brush, e.Bounds);
        string text = (e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text) ?? "";
        var bounds = Rectangle.Inflate(e.Bounds, -5, 0);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, contrast ? selected ? SystemColors.HighlightText : SystemColors.WindowText : Enabled ? UiTheme.Text : UiTheme.DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        PaintChrome(e.Graphics);
    }

    private void PaintChrome(Graphics graphics)
    {
        int width = Math.Max(18, 24 * DeviceDpi / 96);
        var arrow = new Rectangle(ClientSize.Width - width, 1, width - 1, ClientSize.Height - 2);
        using var brush = new SolidBrush(Enabled ? UiTheme.Input : UiTheme.Disabled);
        graphics.FillRectangle(brush, ClientRectangle);
        var textBounds = new Rectangle(5, 1, Math.Max(0, arrow.Left - 8), Math.Max(0, ClientSize.Height - 2));
        TextRenderer.DrawText(graphics, SelectedIndex < 0 ? Text : GetItemText(SelectedItem), Font, textBounds,
            Enabled ? UiTheme.Text : UiTheme.DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var pen = new Pen(Enabled ? UiTheme.Muted : UiTheme.DisabledText, Math.Max(1, DeviceDpi / 96F));
        int x = arrow.Left + arrow.Width / 2, y = arrow.Top + arrow.Height / 2;
        int radius = Math.Max(3, 4 * DeviceDpi / 96);
        graphics.DrawLines(pen, new Point[] { new(x - radius, y - 2), new(x, y + 2), new(x + radius, y - 2) });
        using var border = new Pen(Focused ? UiTheme.Accent : UiTheme.Border);
        graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
}
