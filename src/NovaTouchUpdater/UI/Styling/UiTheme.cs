using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal static class UiTheme
{
    private static readonly Lazy<Icon> ApplicationIcon = new(() =>
    {
        using var stream = typeof(UiTheme).Assembly.GetManifestResourceStream("NovaTouchUpdater.AppIcon.ico")
            ?? throw new InvalidOperationException("Das Programm-Icon fehlt.");
        return new Icon(stream);
    });
    private static readonly Lazy<Image> ApplicationImage = new(() =>
    {
        using var stream = typeof(UiTheme).Assembly.GetManifestResourceStream("NovaTouchUpdater.AppIcon.png")
            ?? throw new InvalidOperationException("Das Programmsymbol fehlt.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    });
    internal static Image BrandImage => ApplicationImage.Value;
    internal static readonly Color Background = Color.FromArgb(244, 247, 251);
    internal static readonly Color Surface = Color.White;
    internal static readonly Color Text = Color.FromArgb(28, 42, 62);
    internal static readonly Color Muted = Color.FromArgb(95, 112, 134);
    internal static readonly Color Border = Color.FromArgb(220, 228, 238);
    internal static readonly Color Accent = Color.FromArgb(37, 99, 235);
    internal static readonly Color AccentSoft = Color.FromArgb(235, 242, 255);
    internal static readonly Color Success = Color.FromArgb(22, 112, 79);
    internal static readonly Color SuccessSoft = Color.FromArgb(231, 246, 237);
    internal static readonly Color Danger = Color.FromArgb(177, 48, 63);
    internal static readonly Color DangerSoft = Color.FromArgb(253, 236, 239);
    internal static readonly Color Warning = Color.FromArgb(136, 81, 20);
    internal static readonly Color WarningSoft = Color.FromArgb(255, 245, 225);

    internal static Font BodyFont() => new("Segoe UI", 9.5F);

    internal static void Apply(Form form)
    {
        form.Icon = ApplicationIcon.Value;
        if (SystemInformation.HighContrast)
        {
            UseSystemColors(form);
            return;
        }
        form.BackColor = Background;
        form.ForeColor = Text;
        ApplyChildren(form);
    }

    private static void UseSystemColors(Control control)
    {
        control.BackColor = SystemColors.Control;
        control.ForeColor = SystemColors.ControlText;
        if (control is TextBox or ComboBox or NumericUpDown)
        {
            control.BackColor = SystemColors.Window;
            control.ForeColor = SystemColors.WindowText;
        }
        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
        }
        if (control is LinkLabel link) link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = SystemColors.HotTrack;
        if (control is DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = true;
            grid.BackgroundColor = grid.DefaultCellStyle.BackColor = grid.AlternatingRowsDefaultCellStyle.BackColor = SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            grid.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
            grid.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
            grid.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
            return;
        }
        foreach (Control child in control.Controls) UseSystemColors(child);
    }

    private static void ApplyChildren(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case DataGridView grid:
                    StyleGrid(grid);
                    continue; // Native grid scrollbars and editing controls manage themselves.
                case TextBox input:
                    input.BorderStyle = BorderStyle.FixedSingle;
                    input.BackColor = input.ReadOnly ? Color.FromArgb(248, 250, 253) : Surface;
                    input.ForeColor = Text;
                    break;
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Surface;
                    combo.ForeColor = Text;
                    break;
                case NumericUpDown number:
                    number.BorderStyle = BorderStyle.FixedSingle;
                    number.BackColor = Surface;
                    number.ForeColor = Text;
                    continue;
                case LinkLabel link:
                    link.LinkColor = Accent;
                    link.ActiveLinkColor = Color.FromArgb(29, 78, 216);
                    link.VisitedLinkColor = Accent;
                    link.LinkBehavior = LinkBehavior.HoverUnderline;
                    break;
                case TabPage page:
                    page.UseVisualStyleBackColor = false;
                    page.BackColor = Surface;
                    break;
            }
            ApplyChildren(control);
        }
    }

    internal static void StyleGrid(DataGridView grid)
    {
        if (SystemInformation.HighContrast) return;
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(220, 228, 238);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 250);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 11, 10, 11);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Padding = new Padding(10, 9, 10, 9);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 254);
        grid.RowTemplate.MinimumHeight = 38;
    }

    internal static GraphicsPath RoundRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) return path;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum ButtonRole { Normal, Primary, Accent, Danger }

// Keep the native Button's keyboard, mnemonic, DialogResult and accessibility
// behavior; only its painting changes. Sizes remain driven by text and DPI.
internal sealed class ModernButton : Button
{
    private ButtonRole role;
    private bool hovered;
    private bool pressed;
    internal ButtonRole Role { get => role; set { role = value; Invalidate(); } }

    internal ModernButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 5, 12, 5);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        Color fill = role switch
        {
            ButtonRole.Primary => hovered ? Color.FromArgb(29, 78, 216) : UiTheme.Accent,
            ButtonRole.Accent => hovered ? Color.FromArgb(220, 233, 255) : UiTheme.AccentSoft,
            ButtonRole.Danger => hovered ? Color.FromArgb(250, 222, 228) : UiTheme.DangerSoft,
            _ => hovered ? Color.FromArgb(240, 245, 252) : UiTheme.Surface
        };
        Color foreground = role switch { ButtonRole.Primary => Color.White, ButtonRole.Accent => UiTheme.Accent, ButtonRole.Danger => UiTheme.Danger, _ => UiTheme.Text };
        Color border = role == ButtonRole.Normal ? UiTheme.Border : fill;
        if (!Enabled) { fill = Color.FromArgb(239, 242, 247); foreground = Color.FromArgb(142, 154, 173); border = fill; }
        else if (pressed) fill = ControlPaint.Dark(fill, .06F);
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96F;
        using var shape = UiTheme.RoundRectangle(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 6F * scale);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border);
        e.Graphics.FillPath(brush, shape);
        e.Graphics.DrawPath(pen, shape);
        var textBounds = ClientRectangle;
        textBounds.Inflate(-(int)(6 * scale), 0);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground, flags);
        if (Focused && ShowFocusCues)
        {
            using var focusShape = UiTheme.RoundRectangle(new RectangleF(3 * scale, 3 * scale, Width - 6 * scale, Height - 6 * scale), 4 * scale);
            using var focusPen = new Pen(role == ButtonRole.Primary ? Color.White : UiTheme.Accent, Math.Max(1, scale)) { DashStyle = DashStyle.Dot };
            e.Graphics.DrawPath(focusPen, focusShape);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

internal sealed class SurfaceCard : TableLayoutPanel
{
    internal Color BorderColor { get; set; } = UiTheme.Border;

    internal SurfaceCard()
    {
        BackColor = UiTheme.Surface;
        Padding = new Padding(16);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaintBackground(e); return; }
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.RoundRectangle(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 9 * DeviceDpi / 96F);
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(BorderColor);
        e.Graphics.FillPath(brush, shape);
        e.Graphics.DrawPath(pen, shape);
    }
}

internal sealed class ModernTabs : TabControl
{
    internal ModernTabs()
    {
        DrawMode = SystemInformation.HighContrast ? TabDrawMode.Normal : TabDrawMode.OwnerDrawFixed;
        Padding = new Point(16, 8);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnDrawItem(e); return; }
        bool selected = e.Index == SelectedIndex;
        using var background = new SolidBrush(selected ? UiTheme.Surface : UiTheme.Background);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, e.Bounds,
            selected ? UiTheme.Accent : UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            int height = Math.Max(2, 3 * DeviceDpi / 96);
            e.Graphics.FillRectangle(accent, e.Bounds.X + 8, e.Bounds.Bottom - height, e.Bounds.Width - 16, height);
        }
        if ((e.State & DrawItemState.Focus) != 0) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -4, -4));
    }
}

internal sealed class ModernProgressBar : ProgressBar
{
    internal ModernProgressBar()
    {
        if (!SystemInformation.HighContrast)
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = UiTheme.RoundRectangle(ClientRectangle, 3 * DeviceDpi / 96F);
        using var background = new SolidBrush(UiTheme.Border);
        e.Graphics.FillPath(background, track);
        float fraction = (float)(Value - Minimum) / Math.Max(1, Maximum - Minimum);
        if (fraction <= 0) return;
        using var fill = UiTheme.RoundRectangle(new RectangleF(0, 0, Math.Max(2, Width * fraction), Height), 3 * DeviceDpi / 96F);
        using var accent = new SolidBrush(UiTheme.Accent);
        e.Graphics.FillPath(accent, fill);
    }
}

internal sealed class AppMark : Control
{
    internal AppMark() { Size = new Size(44, 44); SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(UiTheme.BrandImage, ClientRectangle);
    }
}
