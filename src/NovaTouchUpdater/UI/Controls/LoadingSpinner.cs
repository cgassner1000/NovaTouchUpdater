using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal sealed class LoadingSpinner : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 33 };
    private readonly Stopwatch elapsed = new();
    private bool active;

    internal LoadingSpinner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(112, 112);
        BackColor = UiTheme.Surface;
        Visible = false;
        TabStop = false;
        Cursor = Cursors.AppStarting;
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Softwarepakete werden geladen";
        animation.Tick += (_, _) => Invalidate();
    }

    internal bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value) elapsed.Restart(); else elapsed.Stop();
            Visible = value;
            UpdateAnimation();
        }
    }

    private void UpdateAnimation() => animation.Enabled = active && Visible && IsHandleCreated;

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateAnimation(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateAnimation(); }
    protected override void OnHandleDestroyed(EventArgs e) { animation.Stop(); base.OnHandleDestroyed(e); }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var shape = new GraphicsPath();
        shape.AddEllipse(ClientRectangle);
        Region? previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Min(Width, Height), scale = size / 112F;
        var disc = new RectangleF(1, 1, Width - 2, Height - 2);
        Color surface = SystemInformation.HighContrast ? SystemColors.Window : UiTheme.Surface;
        Color border = SystemInformation.HighContrast ? SystemColors.WindowText : UiTheme.Border;
        Color accent = SystemInformation.HighContrast ? SystemColors.Highlight : UiTheme.IsDark ? UiTheme.ToggleOn : UiTheme.Accent;
        using var background = new SolidBrush(surface);
        using var outline = new Pen(border, Math.Max(1, scale));
        e.Graphics.FillEllipse(background, disc);
        e.Graphics.DrawEllipse(outline, disc);
        float inset = 18 * scale;
        var ring = new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
        using var track = new Pen(Color.FromArgb(100, border), 5 * scale);
        e.Graphics.DrawEllipse(track, ring);
        double time = elapsed.Elapsed.TotalSeconds;
        float rotation = (float)(time * 240 % 360);
        float sweep = 210 + 60 * (float)Math.Sin(time * Math.PI);
        for (int segment = 0; segment < 36; segment++)
        {
            using var pen = new Pen(Color.FromArgb(35 + 220 * segment / 35, accent), 5 * scale)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawArc(pen, ring, rotation + sweep * segment / 36, sweep / 36 + 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) animation.Dispose();
        base.Dispose(disposing);
    }
}
