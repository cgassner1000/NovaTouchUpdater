namespace NovaTouchUpdater;

internal sealed class AppNumericUpDown : NumericUpDown
{
    private readonly List<SpinnerWindow> spinners = [];

    internal AppNumericUpDown()
    {
        // Retain the native spinner's input, repeat-on-hold and accessibility behavior.
        foreach (Control child in Controls)
        {
            if (child is TextBox) continue;
            var window = new SpinnerWindow(this, child);
            spinners.Add(window);
            child.HandleCreated += (_, _) => window.Attach();
            if (child.IsHandleCreated) window.Attach();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var spinner in spinners) spinner.ReleaseHandle();
        base.Dispose(disposing);
    }

    private sealed class SpinnerWindow(AppNumericUpDown owner, Control buttons) : NativeWindow
    {
        internal void Attach()
        {
            if (Handle != buttons.Handle) { ReleaseHandle(); AssignHandle(buttons.Handle); }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (SystemInformation.HighContrast || m.Msg is not (0xF or 0x317 or 0x318) || Handle == 0) return;
            using var graphics = m.Msg is 0x317 or 0x318 && m.WParam != 0 ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle);
            using var fill = new SolidBrush(owner.Enabled ? UiTheme.Input : UiTheme.Disabled);
            graphics.FillRectangle(fill, buttons.ClientRectangle);
            using var border = new Pen(UiTheme.Border);
            graphics.DrawLine(border, 0, 0, 0, buttons.Height);
            graphics.DrawLine(border, 0, buttons.Height / 2, buttons.Width, buttons.Height / 2);
            using var arrow = new Pen(owner.Enabled ? UiTheme.Muted : UiTheme.DisabledText, Math.Max(1, owner.DeviceDpi / 96F));
            int x = buttons.Width / 2, radius = Math.Max(2, 3 * owner.DeviceDpi / 96);
            int upper = buttons.Height / 4, lower = buttons.Height * 3 / 4;
            graphics.DrawLines(arrow, new Point[] { new(x - radius, upper + 1), new(x, upper - 1), new(x + radius, upper + 1) });
            graphics.DrawLines(arrow, new Point[] { new(x - radius, lower - 1), new(x, lower + 1), new(x + radius, lower - 1) });
        }
    }
}
