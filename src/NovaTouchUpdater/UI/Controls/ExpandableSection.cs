namespace NovaTouchUpdater;

// The header participates in layout; its content floats above the form so
// opening a section never moves the package cards, action bar or host table.
internal sealed class ExpandableSection : ModernButton, IMessageFilter
{
    private readonly string title;
    private readonly Panel popup = new() { AutoScroll = true, BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle, Visible = false };
    private readonly SurfaceCard card = new()
    {
        AutoSize = false,
        ColumnCount = 1, RowCount = 1, Margin = Padding.Empty
    };
    private Control? overlayOwner;
    private Form? ownerForm;
    private bool positioning;

    internal bool Expanded { get; private set; }
    internal Panel Popup => popup;
    internal event EventHandler? ExpandedChanged;

    internal ExpandableSection(string title, TableLayoutPanel content)
    {
        this.title = title;
        Dock = DockStyle.Top;
        Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        Padding = new Padding(16, 12, 16, 12);
        TextAlign = ContentAlignment.MiddleLeft;
        UseMnemonic = false;
        AccessibleName = title;
        AccessibleRole = AccessibleRole.ButtonDropDown;
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Margin = Padding.Empty;
        card.Controls.Add(content, 0, 0);
        popup.Controls.Add(card);
        UpdateHeader();
    }

    internal void AttachOverlay(Control owner)
    {
        overlayOwner = owner;
        popup.Name = Name + "Popup";
        owner.Controls.Add(popup);
        popup.SendToBack();
        owner.SizeChanged += OwnerSizeChanged;
        ownerForm = owner as Form;
        if (ownerForm is not null) ownerForm.Deactivate += OwnerDeactivated;
    }

    internal void Expand()
    {
        if (Expanded || overlayOwner is null || overlayOwner.IsDisposed) return;
        PositionPopup();
        Expanded = true;
        UpdateHeader();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
        popup.BringToFront();
        popup.Visible = true;
        Application.AddMessageFilter(this);
        popup.SelectNextControl(null, true, true, true, false);
    }

    internal void Collapse(bool restoreFocus = false)
    {
        if (!Expanded) return;
        Expanded = false;
        Application.RemoveMessageFilter(this);
        popup.Visible = false;
        popup.SendToBack();
        UpdateHeader();
        if (restoreFocus && CanFocus) Focus();
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
        Text = title + (Expanded ? "  ▲" : "  ▼");
        AccessibleDescription = Expanded ? "Geöffnet. Zum Zuklappen klicken." : "Geschlossen. Zum Aufklappen klicken.";
        Role = Expanded ? ButtonRole.Accent : ButtonRole.Normal;
    }

    private void PositionPopup()
    {
        if (positioning || overlayOwner is null || overlayOwner.IsDisposed) return;
        positioning = true;
        try
        {
            float scale = Math.Max(DeviceDpi / 96F, Font.SizeInPoints / 11F);
            int gap = Math.Max(2, (int)Math.Ceiling(6 * scale));
            int width = Math.Min(Math.Max(Width, (int)Math.Ceiling(480 * scale)), Math.Max(1, overlayOwner.ClientSize.Width - 2 * gap));
            popup.Width = width;
            // Reserve scrollbar width before measuring, so a tall popup cannot
            // oscillate between wrapped and unwrapped layouts.
            card.Width = Math.Max(1, popup.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            card.PerformLayout();
            card.Height = card.GetPreferredSize(new Size(card.Width, 0)).Height;
            int desiredHeight = card.Height + 2;
            var anchor = new Rectangle(overlayOwner.PointToClient(PointToScreen(Point.Empty)), Size);
            int below = overlayOwner.ClientSize.Height - anchor.Bottom - gap;
            int above = anchor.Top - gap;
            bool opensBelow = below >= Math.Min(desiredHeight, 100 * scale) || below >= above;
            int height = Math.Min(desiredHeight, Math.Max(1, opensBelow ? below : above));
            height = Math.Min(height, Math.Max(1, overlayOwner.ClientSize.Height - 2 * gap));
            int x = Math.Clamp(anchor.Left, gap, Math.Max(gap, overlayOwner.ClientSize.Width - width - gap));
            int y = Math.Clamp(opensBelow ? anchor.Bottom : anchor.Top - height,
                gap, Math.Max(gap, overlayOwner.ClientSize.Height - height - gap));
            popup.Bounds = new Rectangle(x, y, width, height);
        }
        finally { positioning = false; }
    }

    private void OwnerSizeChanged(object? sender, EventArgs e) { if (Expanded) PositionPopup(); }
    private void OwnerDeactivated(object? sender, EventArgs e) => Collapse();
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (Expanded) PositionPopup(); }
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (Expanded) PositionPopup(); }

    public bool PreFilterMessage(ref Message m)
    {
        if (!Expanded) return false;
        if (m.Msg == 0x0100 && (Keys)m.WParam.ToInt32() == Keys.Escape)
        {
            // Let an open combo box consume its own first Escape.
            if (HasOpenCombo(popup)) return false;
            Collapse(restoreFocus: true);
            return true;
        }
        if (m.Msg is 0x0201 or 0x0204 or 0x0207 or 0x020B or 0x00A1 or 0x020A)
        {
            Control? target = Control.FromHandle(m.HWnd);
            bool inside = target is not null
                ? target == this || Contains(target) || target == popup || popup.Contains(target)
                : RectangleToScreen(ClientRectangle).Contains(Cursor.Position) || popup.RectangleToScreen(popup.ClientRectangle).Contains(Cursor.Position);
            if (!inside && !HasOpenCombo(popup)) Collapse();
        }
        return false;
    }

    private static bool HasOpenCombo(Control root) => root is ComboBox { DroppedDown: true } ||
        root.Controls.Cast<Control>().Any(HasOpenCombo);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Collapse();
            Application.RemoveMessageFilter(this);
            if (overlayOwner is not null) overlayOwner.SizeChanged -= OwnerSizeChanged;
            if (ownerForm is not null) ownerForm.Deactivate -= OwnerDeactivated;
            popup.Dispose();
        }
        base.Dispose(disposing);
    }
}
