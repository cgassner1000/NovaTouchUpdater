using NovaTouchUpdater;

internal static partial class Program
{
    private static void TogglePaintingTests()
    {
        Application.EnableVisualStyles();
        foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var font = new Font("Segoe UI", 9.5F * scale);
            using var parent = new Panel { BackColor = UiTheme.Surface };
            using var toggle = new ModernToggleSwitch { Font = font, Text = "Bei Programmstart prüfen", AutoSize = false };
            parent.Controls.Add(toggle);
            toggle.Size = toggle.GetPreferredSize(Size.Empty);
            using var reused = new Bitmap(toggle.Width, toggle.Height);
            using var clean = new Bitmap(toggle.Width, toggle.Height);
            using (var graphics = Graphics.FromImage(reused))
            {
                graphics.Clear(Color.Magenta);
                TextRenderer.DrawText(graphics, "Fehler erneut versuchen", font, Point.Empty, Color.Navy);
            }
            using (var graphics = Graphics.FromImage(clean)) graphics.Clear(parent.BackColor);
            PaintToggle(toggle, reused);
            PaintToggle(toggle, clean);
            reused.Save(Path.Combine(TestWorkspace.Artifacts, $"toggle-repaint-{(int)(100 * scale)}.png"));
            Assert(SamePixels(reused, clean),
                $"toggle repaint removes foreign control text from a reused paint buffer at {scale:P0}");

            toggle.Text = "HTTPS";
            toggle.Checked = true;
            PaintToggle(toggle, reused);
            using (var graphics = Graphics.FromImage(clean)) graphics.Clear(parent.BackColor);
            PaintToggle(toggle, clean);
            Assert(SamePixels(reused, clean),
                $"changing toggle text and state leaves no old label or thumb behind at {scale:P0}");

            parent.BackColor = UiTheme.AccentSoft;
            toggle.Text = "Installationsdatei nicht übertragen" + Environment.NewLine + "Lokale Datei verwenden";
            toggle.Size = toggle.GetPreferredSize(Size.Empty);
            using var multiline = new Bitmap(toggle.Width, toggle.Height);
            foreach (bool enabled in new[] { false, true })
            {
                toggle.Enabled = enabled;
                PaintToggle(toggle, multiline);
                Assert(multiline.GetPixel(toggle.Width - 1, 0).ToArgb() == parent.BackColor.ToArgb(),
                    $"multiline toggle uses its current parent background ({scale:P0}, enabled={enabled})");
            }
            multiline.Save(Path.Combine(TestWorkspace.Artifacts, $"toggle-multiline-{(int)(100 * scale)}.png"));
        }
    }

    private static void PaintToggle(ModernToggleSwitch toggle, Bitmap target)
    {
        using var graphics = Graphics.FromImage(target);
        using var args = new PaintEventArgs(graphics, new Rectangle(Point.Empty, target.Size));
        // ButtonBase sets Opaque: WinForms skips background painting and the
        // foreground painter must replace every pixel of the reused buffer.
        typeof(ModernToggleSwitch).GetMethod("OnPaint", Private)!.Invoke(toggle, [args]);
    }

    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }
}
