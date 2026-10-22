namespace NovaTouchUpdater;

internal static class WindowLayout
{
    public static Panel CreateScrollArea(TableLayoutPanel content, params Control[] flexibleControls)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        content.Dock = DockStyle.None;
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        content.Location = Point.Empty;
        viewport.Controls.Add(content);

        // Keep the flexible rows usable and scroll when the automatic rows need
        // more space. AutoSize plus percent rows would retain previous preferred
        // heights after resizing or changing DPI.
        void FitContent()
        {
            int width = Math.Max(content.MinimumSize.Width, viewport.ClientSize.Width);
            if (content.Width != width) content.Width = width;
            int requiredHeight = content.Padding.Vertical;
            float flexibleHeight = 0;
            float totalWeight = content.RowStyles.Cast<RowStyle>().Where(row => row.SizeType == SizeType.Percent).Sum(row => row.Height);
            foreach (Control control in content.Controls)
            {
                if (flexibleControls.Contains(control))
                {
                    float weight = content.RowStyles[content.GetRow(control)].Height;
                    flexibleHeight = Math.Max(flexibleHeight, (control.MinimumSize.Height + control.Margin.Vertical) * totalWeight / weight);
                }
                else requiredHeight += control.Height + control.Margin.Vertical;
            }
            requiredHeight += (int)Math.Ceiling(flexibleHeight);
            int height = Math.Max(viewport.ClientSize.Height, requiredHeight);
            if (content.Height != height) content.Height = height;
        }
        content.Layout += (_, _) => FitContent();
        viewport.ClientSizeChanged += (_, _) => FitContent();
        FitContent();
        return viewport;
    }

    // All programmatic layouts use logical pixels at 96 DPI. Suspend layout before
    // constructing the controls, then scale the complete hierarchy exactly once.
    public static void CompleteInitialization(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.ResumeLayout(true);
        form.Load += (_, _) => FitToScreen(form);
        form.DpiChanged += (_, _) => form.BeginInvoke((Action)(() => FitToScreen(form)));
    }

    private static void FitToScreen(Form form)
    {
        if (form.IsDisposed || form.WindowState != FormWindowState.Normal) return;
        Rectangle area = Screen.FromControl(form).WorkingArea;
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
        Size size = new(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
        form.Bounds = new Rectangle(
            Math.Clamp(form.Left, area.Left, area.Right - size.Width),
            Math.Clamp(form.Top, area.Top, area.Bottom - size.Height), size.Width, size.Height);
    }
}
