using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal static partial class UiTheme
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
    internal static Font BodyFont() => new("Segoe UI", 9.5F);

    internal static void Apply(Form form)
    {
        Register(form);
        form.Icon = ApplicationIcon.Value;
        ApplyWindowFrame(form);
        if (SystemInformation.HighContrast)
        {
            UseSystemColors(form);
            return;
        }
        form.BackColor = Background;
        form.ForeColor = Text;
        ApplyChildren(form);
        form.Invalidate(true);
    }

    private static void UseSystemColors(Control control)
    {
        if (control.ContextMenuStrip is { } menu) StyleMenu(menu);
        if (control is TextBoxBase or ComboBox or TreeView or ListBox or ScrollBar or ScrollableControl { AutoScroll: true }) StyleNativeControl(control);
        if (control is AppTabs tabs) tabs.RefreshTheme();
        if (control is AppComboBox combo) combo.RefreshTheme();
        if (control is AppProgressBar progress) progress.RefreshTheme();
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
            foreach (Control child in grid.Controls) StyleNativeControl(child);
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
            control.BackColor = Remap(control.BackColor, background: true, parent.BackColor);
            control.ForeColor = Remap(control.ForeColor, background: false, parent.ForeColor);
            if (control.ContextMenuStrip is { } menu) StyleMenu(menu);
            if (control is TextBoxBase or ComboBox or TreeView or ListBox or ScrollBar or ScrollableControl { AutoScroll: true }) StyleNativeControl(control);
            switch (control)
            {
                case SurfaceCard card:
                    if (card.BackColor == Background) card.BackColor = Surface;
                    card.BorderColor = Remap(card.BorderColor, true, Border);
                    break;
                case AppTabs tabs:
                    tabs.RefreshTheme();
                    break;
                case AppProgressBar progress:
                    progress.RefreshTheme();
                    break;
                case AppButton button:
                    button.FlatStyle = FlatStyle.Flat;
                    button.UseVisualStyleBackColor = false;
                    break;
                case AdministratorButton administrator:
                    administrator.RefreshTheme();
                    break;
                case DataGridView grid:
                    StyleGrid(grid);
                    foreach (Control child in grid.Controls) StyleNativeControl(child);
                    continue; // Native grid scrollbars and editing controls manage themselves.
                case TextBox input:
                    input.BorderStyle = BorderStyle.FixedSingle;
                    input.BackColor = input.ReadOnly ? ReadOnlySurface : Input;
                    input.ForeColor = Text;
                    break;
                case ComboBox combo:
                    if (combo is AppComboBox appCombo) appCombo.RefreshTheme();
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Input;
                    combo.ForeColor = Text;
                    break;
                case NumericUpDown number:
                    number.BorderStyle = BorderStyle.FixedSingle;
                    number.BackColor = Input;
                    number.ForeColor = Text;
                    continue;
                case LinkLabel link:
                    link.LinkColor = Accent;
                    link.ActiveLinkColor = Accent;
                    link.VisitedLinkColor = Accent;
                    link.LinkBehavior = LinkBehavior.HoverUnderline;
                    break;
                case TabPage page:
                    page.UseVisualStyleBackColor = false;
                    page.BackColor = Surface;
                    break;
                case TreeView tree:
                    tree.BackColor = Input;
                    tree.ForeColor = Text;
                    tree.LineColor = Border;
                    RemapNodes(tree.Nodes);
                    break;
                case ListBox list:
                    list.BackColor = Input;
                    list.ForeColor = Text;
                    break;
            }
            ApplyChildren(control);
            control.Invalidate();
        }
    }

    internal static void StyleGrid(DataGridView grid)
    {
        if (SystemInformation.HighContrast) return;
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Border;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Header;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        if (grid.ColumnHeadersDefaultCellStyle.Font is not Font headerFont || !headerFont.Bold ||
            headerFont.SizeInPoints != grid.Font.SizeInPoints || headerFont.FontFamily.Name != grid.Font.FontFamily.Name)
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 11, 10, 11);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Padding = new Padding(10, 9, 10, 9);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Alternate;
        for (int index = 0; index < grid.Rows.Count; index++)
        {
            // Do not unshare/materialize every row of a virtual log table on a theme change.
            var row = grid.Rows.SharedRow(index);
            if (row.HasDefaultCellStyle) RemapStyle(row.DefaultCellStyle);
            foreach (DataGridViewCell cell in row.Cells) if (cell.HasStyle) RemapStyle(cell.Style);
        }
        foreach (DataGridViewColumn column in grid.Columns) RemapStyle(column.DefaultCellStyle);
        grid.Invalidate();
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

// Retain the native checkbox's checked state, keyboard and accessibility behavior.
internal sealed class AppToggleSwitch : CheckBox
{
    private bool hovered;
    private float SwitchScale => Math.Max(DeviceDpi / 96F, Font.SizeInPoints / 9.5F);
    private TextFormatFlags TextFlags => TextFormatFlags.WordBreak | TextFormatFlags.NoPadding |
        (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix) |
        (UseMnemonic ? TextFormatFlags.Default : TextFormatFlags.NoPrefix) |
        (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Default);

    internal AppToggleSwitch()
    {
        AutoSize = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (SystemInformation.HighContrast) return base.GetPreferredSize(proposedSize);
        float scale = SwitchScale;
        int switchWidth = (int)Math.Ceiling(40 * scale);
        int gap = string.IsNullOrEmpty(Text) ? 0 : (int)Math.Ceiling(10 * scale);
        int chrome = Padding.Horizontal + switchWidth + gap + 2;
        int width = proposedSize.Width;
        if (MaximumSize.Width > 0 && (width <= 0 || width > MaximumSize.Width)) width = MaximumSize.Width;
        Size text = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font,
            new Size(width > 0 ? Math.Max(1, width - chrome) : int.MaxValue, int.MaxValue), TextFlags);
        return new Size(chrome + text.Width,
            Padding.Vertical + Math.Max((int)Math.Ceiling(24 * scale), text.Height + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        // ButtonBase marks the control opaque and skips OnPaintBackground.
        // Clear the shared buffer so previous controls and labels cannot bleed through.
        e.Graphics.Clear(BackColor);
        float scale = SwitchScale;
        float trackWidth = 40 * scale, trackHeight = 22 * scale;
        bool rtl = RightToLeft == RightToLeft.Yes;
        float x = rtl ? Width - Padding.Right - trackWidth - 1 : Padding.Left + 1;
        var track = new RectangleF(x, Padding.Top + (Height - Padding.Vertical - trackHeight) / 2F,
            trackWidth, trackHeight);
        Color fill = !Enabled ? (UiTheme.IsDark ? UiTheme.Disabled : Color.FromArgb(224, 228, 234))
            : Checked ? (hovered ? UiTheme.ToggleHover : UiTheme.ToggleOn)
            : hovered ? UiTheme.ToggleOffHover : UiTheme.ToggleOff;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.RoundRectangle(track, trackHeight / 2F);
        using var trackBrush = new SolidBrush(fill);
        e.Graphics.FillPath(trackBrush, shape);
        float inset = 3 * scale, diameter = trackHeight - inset * 2;
        float thumbX = Checked != rtl ? track.Right - inset - diameter : track.Left + inset;
        using var thumbBrush = new SolidBrush(Enabled ? UiTheme.Thumb : UiTheme.IsDark ? UiTheme.DisabledText : Color.FromArgb(247, 248, 250));
        e.Graphics.FillEllipse(thumbBrush, thumbX, track.Top + inset, diameter, diameter);
        int labelSpace = (int)Math.Ceiling(trackWidth + 10 * scale) + 2;
        var textBounds = new Rectangle(Padding.Left + (rtl ? 0 : labelSpace), Padding.Top,
            Math.Max(0, Width - Padding.Horizontal - labelSpace), Math.Max(0, Height - Padding.Vertical));
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
            Enabled ? ForeColor : UiTheme.DisabledText, TextFlags | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), ForeColor, BackColor);
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

// Keep the native Button's keyboard, mnemonic, DialogResult and accessibility
// behavior; only its painting changes. Sizes remain driven by text and DPI.
internal class AppButton : Button
{
    private ButtonRole role;
    private bool hovered;
    private bool pressed;
    internal ButtonRole Role { get => role; set { role = value; Invalidate(); } }

    internal AppButton()
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
            ButtonRole.Primary => hovered ? UiTheme.PrimaryHover : UiTheme.Primary,
            ButtonRole.Accent => hovered ? UiTheme.AccentHover : UiTheme.AccentSoft,
            ButtonRole.Danger => hovered ? UiTheme.DangerHover : UiTheme.DangerSoft,
            _ => hovered ? UiTheme.Hover : UiTheme.Surface
        };
        Color foreground = role switch { ButtonRole.Primary => UiTheme.PrimaryText, ButtonRole.Accent => UiTheme.Accent, ButtonRole.Danger => UiTheme.Danger, _ => UiTheme.Text };
        Color border = role == ButtonRole.Normal ? UiTheme.Border : fill;
        if (!Enabled) { fill = UiTheme.Disabled; foreground = UiTheme.DisabledText; border = fill; }
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
        var horizontal = TextAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.MiddleLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter
        };
        var flags = horizontal | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
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

internal sealed class AppTabs : TabControl
{
    internal AppTabs()
    {
        Padding = new Point(16, 8);
        RefreshTheme();
    }

    internal void RefreshTheme()
    {
        DrawMode = SystemInformation.HighContrast ? TabDrawMode.Normal : TabDrawMode.OwnerDrawFixed;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, !SystemInformation.HighContrast);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(UiTheme.Background);
        using var border = new Pen(UiTheme.Border);
        var frame = Rectangle.Inflate(DisplayRectangle, 1, 1);
        e.Graphics.DrawRectangle(border, frame);
        for (int index = 0; index < TabCount; index++)
            OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, GetTabRect(index), index,
                index == SelectedIndex && Focused ? DrawItemState.Focus : DrawItemState.None));
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

internal sealed class AppProgressBar : ProgressBar
{
    internal AppProgressBar()
    {
        RefreshTheme();
    }

    internal void RefreshTheme() => SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, !SystemInformation.HighContrast);

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
