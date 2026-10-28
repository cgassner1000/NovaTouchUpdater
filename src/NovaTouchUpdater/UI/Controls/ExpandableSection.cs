using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

// Expand the original card over the form. A placeholder holds its collapsed
// position, so the other cards and the host table do not move.
internal sealed class ExpandableSection : Panel, IMessageFilter
{
    private readonly string title;
    private readonly SectionHeader header = new();
    private readonly Panel body = new() { AutoScroll = true, Visible = false, BorderStyle = BorderStyle.None };
    private readonly TableLayoutPanel content;
    private Control? overlayOwner;
    private Form? ownerForm;
    private Panel? placeholder;
    private DockStyle collapsedDock;
    private bool positioning;
    private bool moving;
    private bool refreshQueued;

    internal bool Expanded { get; private set; }
    internal event EventHandler? ExpandedChanged;

    internal ExpandableSection(string title, TableLayoutPanel content)
    {
        this.title = title;
        this.content = content;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 0, 16, 0);
        BackColor = UiTheme.Surface;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        header.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        header.Padding = new Padding(16, 12, 16, 12);
        header.AccessibleName = title;
        header.AccessibleRole = AccessibleRole.ButtonDropDown;
        header.TabIndex = 0;
        body.TabIndex = 1;
        header.Click += (_, _) => OnClick(EventArgs.Empty);
        content.Margin = Padding.Empty;
        content.Dock = DockStyle.None;
        content.AutoSize = false;
        body.Controls.Add(content);
        Controls.Add(body);
        Controls.Add(header);
        content.Layout += ContentLayout;
        UpdateHeader();
    }

    public override Size GetPreferredSize(Size proposedSize) => header.GetPreferredSize(proposedSize);

    internal void AttachOverlay(Control owner)
    {
        overlayOwner = owner;
        owner.SizeChanged += OwnerSizeChanged;
        ownerForm = owner as Form;
        if (ownerForm is not null) ownerForm.Deactivate += OwnerDeactivated;
    }

    internal void Expand()
    {
        if (Expanded || Parent is null || overlayOwner is null || overlayOwner.IsDisposed) return;
        Control parent = Parent;
        int index = parent.Controls.GetChildIndex(this);
        var table = parent as TableLayoutPanel;
        var cell = table?.GetCellPosition(this) ?? default;
        int columns = table?.GetColumnSpan(this) ?? 1, rows = table?.GetRowSpan(this) ?? 1;
        collapsedDock = Dock;
        placeholder = new Panel { Name = Name + "Placeholder", Bounds = Bounds, Dock = Dock, Anchor = Anchor, Margin = Margin, TabStop = false };
        moving = true;
        parent.SuspendLayout();
        try
        {
            AutoSize = false;
            Dock = DockStyle.None;
            parent.Controls.Remove(this);
            parent.Controls.Add(placeholder);
            if (table is not null)
            {
                table.SetCellPosition(placeholder, cell);
                table.SetColumnSpan(placeholder, columns);
                table.SetRowSpan(placeholder, rows);
            }
            parent.Controls.SetChildIndex(placeholder, index);
            overlayOwner.Controls.Add(this);
            Expanded = true;
            body.Visible = true;
            UpdateHeader();
        }
        finally { parent.ResumeLayout(true); moving = false; }
        placeholder.LocationChanged += AnchorChanged;
        placeholder.SizeChanged += AnchorChanged;
        parent.Layout += ParentLayout;
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
        PositionExpandedCard();
        BringToFront();
        Application.AddMessageFilter(this);
    }

    internal void Collapse(bool restoreFocus = false)
    {
        if (!Expanded) return;
        Expanded = false;
        Application.RemoveMessageFilter(this);
        moving = true;
        body.Visible = false;
        UpdateHeader();
        if (placeholder?.Parent is { IsDisposed: false, Disposing: false } parent)
        {
            parent.Layout -= ParentLayout;
            placeholder.LocationChanged -= AnchorChanged;
            placeholder.SizeChanged -= AnchorChanged;
            var table = parent as TableLayoutPanel;
            var cell = table?.GetCellPosition(placeholder) ?? default;
            int columns = table?.GetColumnSpan(placeholder) ?? 1, rows = table?.GetRowSpan(placeholder) ?? 1;
            int index = parent.Controls.GetChildIndex(placeholder);
            Rectangle bounds = placeholder.Bounds;
            parent.SuspendLayout();
            try
            {
                parent.Controls.Remove(placeholder);
                Bounds = bounds;
                Dock = collapsedDock;
                AutoSize = true;
                parent.Controls.Add(this);
                if (table is not null)
                {
                    table.SetCellPosition(this, cell);
                    table.SetColumnSpan(this, columns);
                    table.SetRowSpan(this, rows);
                }
                parent.Controls.SetChildIndex(this, index);
            }
            finally { parent.ResumeLayout(true); }
        }
        placeholder?.Dispose();
        placeholder = null;
        moving = false;
        PerformLayout();
        if (restoreFocus && header.CanFocus) header.Focus();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        if (Expanded) Collapse(restoreFocus: true);
        else Expand();
        base.OnClick(e);
    }

    private void UpdateHeader()
    {
        Text = header.Text = title + (Expanded ? "  ▲" : "  ▼");
        header.AccessibleDescription = Expanded ? "Geöffnet. Zum Zuklappen klicken." : "Geschlossen. Zum Aufklappen klicken.";
        header.ForeColor = Expanded ? UiTheme.Accent : UiTheme.Text;
    }

    private void PositionExpandedCard()
    {
        if (positioning || moving || !Expanded || placeholder is null || overlayOwner is null || overlayOwner.IsDisposed) return;
        positioning = true;
        try
        {
            // Hidden controls can have their HWND parked during reparenting.
            // Managed locations also work there and include scroll offsets.
            Point origin = placeholder.Location;
            for (Control? parent = placeholder.Parent; parent is not null && parent != overlayOwner; parent = parent.Parent)
                origin.Offset(parent.Location);
            int width = Math.Min(placeholder.Width, Math.Max(1, overlayOwner.ClientSize.Width - origin.X));
            int headerHeight = header.GetPreferredSize(new Size(width, 0)).Height;
            int inset = Padding.Left;
            int contentWidth = Math.Max(1, width - inset * 2 - SystemInformation.VerticalScrollBarWidth);
            content.Width = contentWidth;
            content.PerformLayout();
            content.Height = content.GetPreferredSize(new Size(contentWidth, 0)).Height;
            int height = Math.Min(headerHeight + content.Height + inset,
                Math.Max(headerHeight, overlayOwner.ClientSize.Height - origin.Y - 6));
            Bounds = new Rectangle(origin, new Size(width, height));
            PerformLayout();
        }
        finally { positioning = false; }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (header is null || body is null) return;
        int height = header.GetPreferredSize(new Size(Width, 0)).Height;
        header.Bounds = new Rectangle(0, 0, Width, height);
        if (!Expanded) return;
        body.Bounds = new Rectangle(0, height, Width, Math.Max(0, Height - height));
        body.Padding = new Padding(Padding.Left, 0, Padding.Right, Padding.Left);
        content.Location = body.AutoScrollPosition + new Size(Padding.Left, 0);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var shape = UiTheme.RoundRectangle(new RectangleF(0, 0, Width, Height), 9 * DeviceDpi / 96F);
        Region? previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(BackColor);
    private void ContentLayout(object? sender, LayoutEventArgs e)
    {
        // Measure after nested layout passes have settled.
        if (!Expanded || positioning || moving || refreshQueued || !IsHandleCreated) return;
        refreshQueued = true;
        BeginInvoke(() => { refreshQueued = false; if (!IsDisposed) PositionExpandedCard(); });
    }
    private void AnchorChanged(object? sender, EventArgs e) => PositionExpandedCard();
    private void ParentLayout(object? sender, LayoutEventArgs e) => PositionExpandedCard();
    private void OwnerSizeChanged(object? sender, EventArgs e) => PositionExpandedCard();
    private void OwnerDeactivated(object? sender, EventArgs e) => Collapse();

    public bool PreFilterMessage(ref Message m)
    {
        if (!Expanded) return false;
        if (m.Msg == 0x0100 && (Keys)m.WParam.ToInt32() == Keys.Escape)
        {
            if (HasOpenCombo(this)) return false;
            Collapse(restoreFocus: true);
            return true;
        }
        if (m.Msg is 0x0201 or 0x0204 or 0x0207 or 0x020B or 0x00A1 or 0x020A)
        {
            Control? target = Control.FromHandle(m.HWnd);
            bool inside = target is not null ? target == this || Contains(target)
                : RectangleToScreen(ClientRectangle).Contains(Cursor.Position);
            if (!inside && !HasOpenCombo(this)) Collapse();
        }
        return false;
    }

    private static bool HasOpenCombo(Control root) => root is ComboBox { DroppedDown: true } ||
        root.Controls.Cast<Control>().Any(HasOpenCombo);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            if (overlayOwner is not null) overlayOwner.SizeChanged -= OwnerSizeChanged;
            if (ownerForm is not null) ownerForm.Deactivate -= OwnerDeactivated;
            if (placeholder?.Parent is { } parent) parent.Layout -= ParentLayout;
            placeholder?.Dispose();
            content.Layout -= ContentLayout;
        }
        base.Dispose(disposing);
    }

    private sealed class SectionHeader : ModernButton
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor);
            var bounds = new Rectangle(Padding.Left, 0, Math.Max(0, Width - Padding.Horizontal), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var shape = UiTheme.RoundRectangle(new RectangleF(3, 3, Width - 6, Height - 6), 5);
                using var pen = new Pen(UiTheme.Accent) { DashStyle = DashStyle.Dot };
                e.Graphics.DrawPath(pen, shape);
            }
        }
    }
}
