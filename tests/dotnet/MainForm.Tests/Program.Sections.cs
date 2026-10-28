using NovaTouchUpdater;

internal static partial class Program
{
    private static void ExpandableSectionTests()
    {
        foreach (float scale in new[] { 1F, 2F })
        {
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            Populate(form);
            var fonts = All(form).ToDictionary(c => c, c => c.Font);
            form.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * scale, font.Style);
            form.MinimumSize = Size.Empty;
            var settings = All(form).OfType<ExpandableSection>().Single(c => c.Name == "settingsCard");
            var hosts = All(form).OfType<ExpandableSection>().Single(c => c.Name == "hostsCard");
            var packages = All(form).Single(c => c.Name == "localUpdateCard");
            var files = All(form).Single(c => c.Name == "filesCard");
            var table = Field<DataGridView>(form, "table");
            foreach (int width in new[] { 1710, 1000, 640 })
            {
                form.ClientSize = new Size((int)(width * scale), (int)(980 * scale));
                foreach (Control control in All(form)) _ = control.Handle;
                for (int pass = 0; pass < 3; pass++) Layout(form);
                Call(form, "ApplyColumnWidths");
                Assert(!settings.Expanded && !hosts.Expanded && settings.Text.EndsWith('▼') && hosts.Text.EndsWith('▼'),
                    $"settings and remote hosts start collapsed with down arrows ({width}px, {scale:P0})");
                Assert(packages.Parent == files.Parent && files.Top >= packages.Bottom && files.Top - packages.Bottom <= 30 * scale,
                    "update files sit directly below installation packages in an independent column");
                Rectangle tableBefore = InForm(form, table), filesBefore = InForm(form, files);
                ClickSection(settings);
                Assert(settings.Expanded && settings.Text.EndsWith('▲') &&
                    InForm(form, table) == tableBefore && InForm(form, files) == filesBefore,
                    "opening settings changes its arrow and overlays content without moving packages or hosts");
                Assert(form.ClientRectangle.Contains(settings.Popup.Bounds), "settings popup stays within the window");
                Assert(form.Controls.GetChildIndex(settings.Popup) == 0, "open content is above the main scroll area");
                Assert(settings.Popup.Bounds.IntersectsWith(InForm(form, hosts)), "settings popup overlaps the following menu header");
                var user = Field<TextBox>(form, "txtUser");
                user.Text = "test-only-user";
                var insideClick = Message.Create(user.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                Assert(!settings.PreFilterMessage(ref insideClick) && settings.Expanded,
                    "clicking a field inside the popup keeps it open");
                if (width == 1710 && scale == 1F)
                {
                    using var popup = new Bitmap(settings.Popup.Width, settings.Popup.Height);
                    settings.Popup.DrawToBitmap(popup, new Rectangle(Point.Empty, popup.Size));
                    popup.Save(Path.Combine(TestWorkspace.Artifacts, "settings-popup.png"));
                    using var expanded = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(expanded, new Rectangle(Point.Empty, expanded.Size));
                    // The form stays hidden: DrawToBitmap omits the floating
                    // child window, so compose its verified bounds explicitly.
                    Point clientOrigin = form.PointToScreen(Point.Empty) - (Size)form.Location;
                    using (var graphics = Graphics.FromImage(expanded))
                        graphics.DrawImageUnscaled(popup, clientOrigin + (Size)settings.Popup.Location);
                    expanded.Save(Path.Combine(TestWorkspace.Artifacts, "main-sections-open.png"));
                }
                ClickSection(settings);
                ClickSection(settings);
                Assert(user.Text == "test-only-user" && ((UpdaterConfig)Call(form, "CaptureConfig")!).Username == "test-only-user",
                    "closing and reopening settings preserves fields and configuration capture");
                ClickSection(hosts);
                Assert(hosts.Expanded && !settings.Expanded && hosts.Text.EndsWith('▲') && settings.Text.EndsWith('▼'),
                    "opening remote hosts closes settings and updates both arrows");
                Assert(hosts.Popup.Contains(Field<Button>(form, "btnImport")) && settings.Popup.Contains(Field<Button>(form, "btnSaveConfig")),
                    "host actions and configuration saving remain in their respective popups");
                var escape = Message.Create(hosts.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                Assert(hosts.PreFilterMessage(ref escape) && !hosts.Expanded, "Escape closes the open section");
                ClickSection(settings);
                var outsideClick = Message.Create(table.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                Assert(!settings.PreFilterMessage(ref outsideClick) && !settings.Expanded,
                    "clicking outside closes the popup and still reaches the clicked control");
                Assert(InForm(form, table) == tableBefore && InForm(form, files) == filesBefore,
                    "repeated opening and closing preserves the main layout");
            }
        }
    }

    private static Rectangle InForm(Form form, Control control) => form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));

    private static void ClickSection(ExpandableSection section) =>
        typeof(ExpandableSection).GetMethod("OnClick", Private)!.Invoke(section, [EventArgs.Empty]);
}
