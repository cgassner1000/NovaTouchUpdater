using NovaTouchUpdater;

internal static partial class Program
{
    private static void ExpandableSectionTests()
    {
        foreach (float scale in new[] { 1F, 2F })
        {
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            Populate(form);
            var fixturePackages = HostUpdateState.Each(UpdateComponents.All).Select(component =>
                new SoftwarePackage(component, component + "-Update-2026.4.9792.zip", "2026.4.9792",
                    (component & UpdateComponents.Msi) == 0 ? null : new MsiPackageRequest
                    { Kind = component.ToString().ToLowerInvariant(), ProductVersion = "2026.4.9792", PayloadPath = component + "-Update-2026.4.9792.zip" })).ToArray();
            SetField(form, "folderPackages", fixturePackages);
            Call(form, "UseFolderPackages");
            var fonts = All(form).ToDictionary(control => control, control => control.Font);
            form.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * scale, font.Style);
            form.MinimumSize = Size.Empty;
            var settings = All(form).OfType<ExpandableSection>().Single(control => control.Name == "settingsCard");
            var hosts = All(form).OfType<ExpandableSection>().Single(control => control.Name == "hostsCard");
            var packages = All(form).Single(control => control.Name == "localUpdateCard");
            var selectedPackages = All(form).Single(control => control.Name == "selectedPackagesCard");
            var settingsLauncher = (SectionNavigationButton)settings.Launcher!;
            var hostsLauncher = (SectionNavigationButton)hosts.Launcher!;
            var tooltips = Field<ToolTip>(form, "updateToolTip");
            var table = Field<DataGridView>(form, "table");
            Assert(!All(form).Any(control => control.Name == "filesCard" || control.Text.Contains("Update-Dateien Einzeln")),
                "the removed individual update-files section leaves no hidden selectors behind");
            Assert(Field<Button>(form, "browseSoftware").Text == "Ordner durchsuchen" &&
                Field<Button>(form, "browsePackageFiles").Text == "Durchsuchen" && Field<Button>(form, "readPackages").Text == "Pakete aktualisieren",
                "the installation card distinguishes folder browsing, file browsing and refreshing packages");
            foreach (int width in new[] { 1710, 1000, 640 })
            {
                form.ClientSize = new Size((int)(width * scale), (int)(980 * scale));
                foreach (Control control in All(form)) _ = control.Handle;
                for (int pass = 0; pass < 3; pass++) Layout(form);
                Call(form, "ApplyColumnWidths");
                Assert(!settings.Expanded && !hosts.Expanded && settings.Text == "Einstellungen",
                    $"settings start closed without an expansion arrow ({width}px, {scale:P0})");
                Assert(selectedPackages.Parent!.Name == "selectedPackagesColumn" && selectedPackages.Top == 0,
                    "selected packages start at the top of their own column");
                Assert(settingsLauncher.Text.Length == 0 && hostsLauncher.Text.Length == 0 &&
                    tooltips.GetToolTip(settingsLauncher) == "Einstellungen" && tooltips.GetToolTip(hostsLauncher) == "Remote-Hosts" &&
                    InForm(form, settingsLauncher).Right < InForm(form, packages).Left && hostsLauncher.Top > settingsLauncher.Top,
                    "the left icon rail has clear tooltips and precedes the package cards");
                var configuration = settingsLauncher.Parent!.Parent!;
                var actionBar = Field<Button>(form, "btnUpdate").Parent!.Parent!;
                Assert(settingsLauncher.Parent.Bottom <= configuration.Height &&
                    InForm(form, actionBar).Top >= InForm(form, configuration).Bottom &&
                    InForm(form, actionBar).Left == InForm(form, configuration).Left && actionBar.Width == configuration.Width,
                    "the action bar below the configuration area keeps its full width");
                var packageList = Field<TableLayoutPanel>(form, "selectedPackagesTable");
                Assert(packageList.RowCount == 7 && packageList.Controls.OfType<Button>().Count() == 7 &&
                    packageList.Controls.Cast<Control>().All(control => control.Right <= packageList.Width && control.Bottom <= packageList.Height),
                    "all seven selected packages and their remove actions remain visible");
                foreach (string name in new[] { "browseSoftware", "browsePackageFiles", "readPackages" })
                {
                    var button = Field<Button>(form, name);
                    Assert(button.Right <= button.Parent!.ClientSize.Width && button.Bottom <= button.Parent.Height,
                        $"{name} fits the installation card at {width}px / {scale:P0}");
                }
                if (width == 1710 && scale == 1F)
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "main-packages.png"));
                }
                Rectangle tableBefore = InForm(form, table), packagesBefore = InForm(form, packages);
                Rectangle launcherBefore = InForm(form, settingsLauncher);
                ClickSection(settings);
                Assert(settings.Expanded && settings.Text == "Einstellungen" && settingsLauncher.Active &&
                    settings.Left > launcherBefore.Right && settings.Top == launcherBefore.Top && form.ClientRectangle.Contains(settings.Bounds),
                    "settings open to the right of the active gear without an upward arrow");
                Assert(InForm(form, table) == tableBefore && InForm(form, packages) == packagesBefore &&
                    !settings.Bounds.IntersectsWith(InForm(form, hostsLauncher)), "open settings preserve packages, the host table and access to the hosts icon");
                var source = Field<TextBox>(form, "softwarePath");
                var destination = Field<TextBox>(form, "txtRemotePath");
                Assert(source.Parent!.Parent == destination.Parent!.Parent && source.Parent.Bottom <= destination.Parent.Top &&
                    source.Right <= source.Parent.ClientSize.Width, "the package source fits directly above the destination in settings");
                foreach (string name in new[] { "chkSkipLocalPackageCopy", "chkSkipRemotePackageCopy", "chkSkipZipTransfer", "chkUploadToZzzSoftware", "chkRestartPosAfterUpdate" })
                {
                    var toggle = Field<CheckBox>(form, name);
                    Assert(toggle.Right <= toggle.Parent!.ClientSize.Width && toggle.Height >= toggle.GetPreferredSize(new Size(toggle.Width, 0)).Height,
                        $"{name} remains readable at {width}px / {scale:P0}");
                }
                var user = Field<TextBox>(form, "txtUser");
                user.Text = "test-only-user";
                var insideClick = Message.Create(user.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                Assert(!settings.PreFilterMessage(ref insideClick) && settings.Expanded, "clicking a field keeps the popup open");
                var launcherClick = Message.Create(settingsLauncher.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                Assert(!settings.PreFilterMessage(ref launcherClick) && settings.Expanded, "the active launcher receives its own toggle click");
                if (width == 1710 && scale == 1F)
                {
                    using var popup = new Bitmap(settings.Width, settings.Height);
                    settings.DrawToBitmap(popup, new Rectangle(Point.Empty, popup.Size));
                    popup.Save(Path.Combine(TestWorkspace.Artifacts, "settings-popup.png"));
                    using var expanded = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(expanded, new Rectangle(Point.Empty, expanded.Size));
                    using (var graphics = Graphics.FromImage(expanded))
                    {
                        Point client = form.PointToScreen(Point.Empty) - (Size)form.Location;
                        graphics.DrawImageUnscaled(popup, client + (Size)settings.Location);
                    }
                    expanded.Save(Path.Combine(TestWorkspace.Artifacts, "main-sections-open.png"));
                }
                ClickSection(settings); ClickSection(settings);
                Assert(user.Text == "test-only-user" && ((UpdaterConfig)Call(form, "CaptureConfig")!).Username == "test-only-user",
                    "closing and reopening settings preserves inputs and configuration capture");
                ClickSection(hosts);
                Assert(hosts.Expanded && !settings.Expanded && hostsLauncher.Active && !settingsLauncher.Active,
                    "opening hosts switches the active menu and launcher");
                var escape = Message.Create(hosts.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                Assert(hosts.PreFilterMessage(ref escape) && !hosts.Expanded, "Escape closes the open menu");
                ClickSection(settings);
                var outside = Message.Create(table.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                Assert(!settings.PreFilterMessage(ref outside) && !settings.Expanded && InForm(form, table) == tableBefore,
                    "outside clicks close the popup without moving the host table");
                ClickSection(settings);
                var ssl = Field<CheckBox>(form, "chkSsl");
                var port = Field<NumericUpDown>(form, "nHttpsPort");
                var https = All(settings).OfType<TableLayoutPanel>().Single(control => control.Name == "httpsOptions");
                port.Value = 6443; ssl.Checked = true; Layout(settings);
                Assert(https.GetColumnWidths()[1] > 0 && https.GetColumnWidths()[2] > 0, "HTTPS shows its port input");
                ssl.Checked = false; Layout(settings);
                Assert(https.GetColumnWidths()[1] == 0 && https.GetColumnWidths()[2] == 0 && port.Value == 6443, "HTTP hides the port without losing its value");
                if (width == 1000)
                {
                    form.ClientSize = new Size((int)(640 * scale), form.ClientSize.Height);
                    for (int pass = 0; pass < 3; pass++) Layout(form);
                    Assert(settings.Left > InForm(form, settingsLauncher).Right && form.ClientRectangle.Contains(settings.Bounds),
                        "the menu stays beside its icon when the window narrows");
                    form.ClientSize = new Size((int)(1000 * scale), form.ClientSize.Height);
                    for (int pass = 0; pass < 3; pass++) Layout(form);
                    Assert(((TableLayoutPanel)configuration).ColumnCount == 3 && InForm(form, selectedPackages).Top == InForm(form, packages).Top,
                        "restoring a wide window restores all three configuration columns");
                }
                settings.Collapse();
            }
        }
        var closingForm = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        var closingSection = All(closingForm).OfType<ExpandableSection>().Single(control => control.Name == "settingsCard");
        var input = Field<TextBox>(closingForm, "txtUser");
        closingSection.Expand(); closingForm.Dispose();
        Assert(closingSection.IsDisposed && input.IsDisposed, "closing the window disposes an open settings menu and its content");
    }

    private static Rectangle InForm(Form form, Control control)
    {
        Point origin = Point.Empty;
        for (Control? current = control; current is not null && current != form; current = current.Parent) origin.Offset(current.Location);
        return new Rectangle(origin, control.ClientSize);
    }

    private static void ClickSection(ExpandableSection section)
    {
        if (section.Launcher is Button launcher) typeof(Button).GetMethod("OnClick", Private)!.Invoke(launcher, [EventArgs.Empty]);
        else typeof(ExpandableSection).GetMethod("OnClick", Private)!.Invoke(section, [EventArgs.Empty]);
    }
}