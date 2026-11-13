using System.Drawing.Drawing2D;

namespace NovaTouchUpdater;

internal enum SectionNavigationIcon { Settings, RemoteHosts, Upload, Report, Command }

internal sealed class SectionNavigationButton : AppButton
{
    private readonly SectionNavigationIcon icon;
    private bool active;
    private bool hovered;

    internal SectionNavigationButton(string title, SectionNavigationIcon icon)
    {
        this.icon = icon;
        AccessibleName = title;
        AccessibleRole = icon is SectionNavigationIcon.Settings or SectionNavigationIcon.RemoteHosts ? AccessibleRole.ButtonDropDown : AccessibleRole.PushButton;
        AutoSize = false;
        Size = new Size(52, 52);
        Padding = Padding.Empty;
        Margin = new Padding(0, 0, 0, 8);
        Active = false;
    }

    internal bool Active
    {
        get => active;
        set
        {
            active = value;
            Role = value ? ButtonRole.Accent : ButtonRole.Normal;
            AccessibleDescription = icon is SectionNavigationIcon.Settings or SectionNavigationIcon.RemoteHosts
                ? value ? "Geöffnet. Zum Schließen klicken." : "Geschlossen. Zum Öffnen klicken."
                : AccessibleName;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = Math.Max(DeviceDpi / 96F, Font.SizeInPoints / 9.5F);
        Color color = !Enabled ? SystemColors.GrayText : SystemInformation.HighContrast ? SystemColors.ControlText
            : active ? UiTheme.AccentButtonText : hovered ? UiTheme.Accent : UiTheme.Muted;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.8F * scale)
        { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        PointF Point(float x, float y) => new(Width / 2F + x * scale, Height / 2F + y * scale);
        if (icon == SectionNavigationIcon.Settings)
        {
            var points = new List<PointF>();
            for (int tooth = 0; tooth < 8; tooth++)
                foreach (var (offset, radius) in new[] { (-20F, 10F), (-14F, 13F), (14F, 13F), (20F, 10F) })
                {
                    double angle = (tooth * 45 + offset) * Math.PI / 180;
                    points.Add(Point((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius));
                }
            e.Graphics.DrawPolygon(pen, points.ToArray());
            PointF center = Point(-4.25F, -4.25F);
            e.Graphics.DrawEllipse(pen, center.X, center.Y, 8.5F * scale, 8.5F * scale);
        }
        else if (icon == SectionNavigationIcon.RemoteHosts)
        {
            foreach (float x in new[] { -13F, 2F })
            {
                PointF corner = Point(x, -10);
                using var monitor = UiTheme.RoundRectangle(new RectangleF(corner.X, corner.Y, 11 * scale, 10 * scale), 1.5F * scale);
                e.Graphics.DrawPath(pen, monitor);
                e.Graphics.DrawLine(pen, Point(x + 5.5F, 0), Point(x + 5.5F, 4));
                e.Graphics.DrawLine(pen, Point(x + 2.5F, 4), Point(x + 8.5F, 4));
            }
            e.Graphics.DrawLines(pen, new[] { Point(-7.5F, 4), Point(-7.5F, 8), Point(7.5F, 8), Point(7.5F, 4) });
            e.Graphics.DrawLine(pen, Point(0, 8), Point(0, 12));
        }
        else if (icon == SectionNavigationIcon.Upload)
        {
            e.Graphics.DrawLines(pen, new[] { Point(-11, 3), Point(-11, 10), Point(11, 10), Point(11, 3) });
            e.Graphics.DrawLine(pen, Point(0, 4), Point(0, -11));
            e.Graphics.DrawLines(pen, new[] { Point(-6, -5), Point(0, -11), Point(6, -5) });
        }
        else if (icon == SectionNavigationIcon.Report)
        {
            e.Graphics.DrawLines(pen, new[] { Point(-9, -13), Point(4, -13), Point(10, -7), Point(10, 13), Point(-9, 13), Point(-9, -13) });
            e.Graphics.DrawLines(pen, new[] { Point(4, -13), Point(4, -7), Point(10, -7) });
            foreach (float y in new[] { -2F, 3F, 8F }) e.Graphics.DrawLine(pen, Point(-4, y), Point(5, y));
        }
        else
        {
            PointF corner = Point(-13, -10);
            using var terminal = UiTheme.RoundRectangle(new RectangleF(corner.X, corner.Y, 26 * scale, 20 * scale), 2 * scale);
            e.Graphics.DrawPath(pen, terminal);
            e.Graphics.DrawLines(pen, new[] { Point(-7, -4), Point(-3, 0), Point(-7, 4) });
            e.Graphics.DrawLine(pen, Point(1, 4), Point(7, 4));
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); }
}
