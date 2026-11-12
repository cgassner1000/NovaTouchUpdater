using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NovaTouchUpdater;

internal sealed class AdministratorButton : Button
{
    internal AdministratorButton()
    {
        Name = "restartAsAdministrator";
        Text = "NovaTouchUpdater als\nAdministrator starten";
        AccessibleName = "NovaTouchUpdater als Administrator starten";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = UiTheme.AccentSoft;
        FlatAppearance.MouseDownBackColor = UiTheme.AccentSoft;
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Accent;
        Font = new Font("Segoe UI", 8.5F);
        Cursor = Cursors.Hand;
        Padding = new Padding(6, 2, 6, 2);
        TextImageRelation = TextImageRelation.ImageBeforeText;
        TextAlign = ContentAlignment.MiddleLeft;
        UpdateShield();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        UpdateShield();
        base.OnDpiChangedAfterParent(e);
    }

    private void UpdateShield()
    {
        int size = (int)Math.Round(24 * DeviceDpi / 96F);
        Image? previous = Image;
        Image = CreateShield(size);
        previous?.Dispose();
    }

    internal static Bitmap CreateShield(int size)
    {
        // Render curves at four times the target resolution, then reduce once.
        // Every DPI gets its own pixels rather than an enlarged system bitmap.
        using var source = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(source.Width / 24F, source.Height / 24F);
            using var shield = new GraphicsPath();
            shield.StartFigure();
            shield.AddBezier(12, 1.5F, 15, 3.2F, 18, 3.8F, 21, 4);
            shield.AddLine(21, 4, 21, 10.5F);
            shield.AddBezier(21, 10.5F, 21, 16, 17, 20, 12, 22.5F);
            shield.AddBezier(12, 22.5F, 7, 20, 3, 16, 3, 10.5F);
            shield.AddLine(3, 10.5F, 3, 4);
            shield.AddBezier(3, 4, 6, 3.8F, 9, 3.2F, 12, 1.5F);
            shield.CloseFigure();
            Color blue = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(42, 105, 193);
            Color gold = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(255, 202, 54);
            using var blueBrush = new SolidBrush(blue);
            using var goldBrush = new SolidBrush(gold);
            graphics.FillPath(blueBrush, shield);
            var state = graphics.Save();
            graphics.SetClip(shield);
            graphics.FillRectangle(goldBrush, 0, 0, 12, 11);
            graphics.FillRectangle(goldBrush, 12, 11, 12, 13);
            graphics.Restore(state);
            using var outline = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(40, 72, 118), .85F)
            { LineJoin = LineJoin.Round };
            graphics.DrawPath(outline, shield);
        }
        var image = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        return image;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Image?.Dispose(); Image = null; }
        base.Dispose(disposing);
    }
}
