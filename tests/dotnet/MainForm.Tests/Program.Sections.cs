using NovaTouchUpdater;

internal static partial class Program
{
    private static void ExpandableSectionTests()
    {
        foreach (float scale in new[] { 1F, 2F })
        {
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            Populate(form);
            var fixturePackages = HostUpdateState.Each(UpdateComponents.ZipServices | UpdateComponents.Msi).Select(component =>
                new SoftwarePackage(component, component + "-Update-2026.4.9792.zip", "2026.4.9792",
                    (component & UpdateComponents.Msi) == 0 ? null : new MsiPackageRequest
                    {
                        Kind = component.ToString().ToLowerInvariant(), ProductVersion = "2026.4.9792",
                        PayloadPath = component + "-Update-2026.4.9792.zip"
                    })).ToArray();
            SetField(form, "folderPackages", fixturePackages);
            Call(form, "UseFolderPackages");
            var fonts = All(form).ToDictionary(c => c, c => c.Font);
            form.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * scale, font.Style);
            form.MinimumSize = Size.Empty;
            var settings = All(form).OfType<ExpandableSection>().Single(c => c.Name == "settingsCard");
            var hosts = All(form).OfType<ExpandableSection>().Single(c => c.Name == "hostsCard");
            var packages = All(form).Single(c => c.Name == "localUpdateCard");
            var files = All(form).OfType<ExpandableSection>().Single(c => c.Name == "filesCard");
            var selectedPackages = All(form).Single(c => c.Name == "selectedPackagesCard");
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
                Assert(selectedPackages.Parent == hosts.Parent && selectedPackages.Top >= hosts.Bottom,
                    "selected packages are listed directly below remote hosts");
                var packageList = Field<Label>(form, "selectedPackagesList");
                Assert(packageList.Text.Split(Environment.NewLine).Length == 7 &&
                    packageList.PreferredSize.Height <= packageList.Height,
                    "all seven selected packages fit in the list at the current width and scaling");
                if (width == 1710 && scale == 1F)
                {
                    using var collapsed = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(collapsed, new Rectangle(Point.Empty, collapsed.Size));
                    collapsed.Save(Path.Combine(TestWorkspace.Artifacts, "main-packages.png"));
                }
                Rectangle tableBefore = InForm(form, table), filesBefore = InForm(form, files);
                Rectangle settingsBefore = InForm(form, settings);
                ClickSection(settings);
                Assert(settings.Expanded && settings.Text.EndsWith('▲') &&
                    InForm(form, table) == tableBefore && InForm(form, files) == filesBefore,
                    "opening settings changes its arrow and overlays content without moving packages or hosts");
                Assert(form.ClientRectangle.Contains(settings.Bounds), "expanded settings stay within the window");
                Assert(settings.Parent == form && form.Controls.GetChildIndex(settings) == 0 &&
                    settings.Location == settingsBefore.Location && settings.Width == settingsBefore.Width && settings.Height > settingsBefore.Height,
                    "the original card expands at its existing position above the main scroll area");
                Assert(settings.BorderStyle == BorderStyle.None && !All(settings).OfType<SurfaceCard>().Any(),
                    "expanded settings have no native rectangular border or nested card");
                Assert(settings.Bounds.IntersectsWith(InForm(form, hosts)), "expanded settings overlap the following menu header");
                foreach (string name in new[] { "chkSkipLocalPackageCopy", "chkSkipZipTransfer", "chkIrisTls" })
                {
                    var toggle = Field<CheckBox>(form, name);
                    Assert(toggle.Right <= toggle.Parent!.ClientSize.Width && toggle.Height >= toggle.GetPreferredSize(new Size(toggle.Width, 0)).Height,
                        $"{name} stays fully readable inside settings at {width}px / {scale:P0}");
                }
                var user = Field<TextBox>(form, "txtUser");
                user.Text = "test-only-user";
                var insideClick = Message.Create(user.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                Assert(!settings.PreFilterMessage(ref insideClick) && settings.Expanded,
                    "clicking a field inside the popup keeps it open");
                if (width == 1710 && scale == 1F)
                {
                    using var popup = new Bitmap(settings.Width, settings.Height);
                    settings.DrawToBitmap(popup, new Rectangle(Point.Empty, popup.Size));
                    popup.Save(Path.Combine(TestWorkspace.Artifacts, "settings-popup.png"));
                    using var expanded = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(expanded, new Rectangle(Point.Empty, expanded.Size));
                    // The form stays hidden: DrawToBitmap omits the floating
                    // child window, so compose its verified bounds explicitly.
                    Point clientOrigin = form.PointToScreen(Point.Empty) - (Size)form.Location;
                    using (var graphics = Graphics.FromImage(expanded))
                        graphics.DrawImageUnscaled(popup, clientOrigin + (Size)settings.Location);
                    expanded.Save(Path.Combine(TestWorkspace.Artifacts, "main-sections-open.png"));
                }
                ClickSection(settings);
                ClickSection(settings);
                Assert(user.Text == "test-only-user" && ((UpdaterConfig)Call(form, "CaptureConfig")!).Username == "test-only-user",
                    "closing and reopening settings preserves fields and configuration capture");
                ClickSection(hosts);
                Assert(hosts.Expanded && !settings.Expanded && hosts.Text.EndsWith('▲') && settings.Text.EndsWith('▼'),
                    "opening remote hosts closes settings and updates both arrows");
                var save = Field<Button>(form, "btnSaveConfig");
                var path = Field<TextBox>(form, "txtRemotePath");
                Assert(hosts.Contains(Field<Button>(form, "btnImport")) && settings.Contains(save) && settings.Contains(path) &&
                    InForm(form, path).Top < InForm(form, save).Top && !files.Contains(path),
                    "the destination sits above configuration saving inside settings");
                var escape = Message.Create(hosts.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                Assert(hosts.PreFilterMessage(ref escape) && !hosts.Expanded, "Escape closes the open section");
                ClickSection(settings);
                var outsideClick = Message.Create(table.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                Assert(!settings.PreFilterMessage(ref outsideClick) && !settings.Expanded,
                    "clicking outside closes the popup and still reaches the clicked control");
                Assert(InForm(form, table) == tableBefore && InForm(form, files) == filesBefore,
                    "repeated opening and closing preserves the main layout");
                ClickSection(files);
                Assert(files.Expanded && files.Text == "Update-Dateien Einzeln  ▲" && files.Parent == form && !settings.Expanded && !hosts.Expanded,
                    "individual update files expand as the original card");
                if (width == 1710 && scale == 1F)
                {
                    using var picker = new Form();
                    _ = picker.Handle;
                    Func<DialogResult> simulatePicker = () =>
                    {
                        typeof(Form).GetMethod("OnDeactivate", Private)!.Invoke(form, [EventArgs.Empty]);
                        var pickerClick = Message.Create(picker.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                        var cancelPicker = Message.Create(picker.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                        Assert(files.Expanded && !files.PreFilterMessage(ref pickerClick) && !files.PreFilterMessage(ref cancelPicker) && files.Expanded,
                            "opening and using a package picker, including Escape, keeps the individual-files section open");
                        return DialogResult.OK;
                    };
                    foreach (SoftwarePackage package in fixturePackages.Take(2))
                    {
                        Assert((DialogResult)Call(form, "ShowIndividualPackageDialog", simulatePicker)! == DialogResult.OK,
                            "the package dialog returns its result without closing the section");
                        Call(form, "SelectIndividualPackage", package);
                        Assert(files.Expanded, "selecting a package keeps the section open for the next package");
                    }
                    Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == (UpdateComponents.Payment | UpdateComponents.Fiscal),
                        "successive individual selections retain both chosen packages");
                    _ = Call(form, "ShowIndividualPackageDialog", (Func<DialogResult>)(() => DialogResult.Cancel));
                    Assert(files.Expanded, "cancelling another file selection also keeps the section open");
                    try
                    {
                        _ = Call(form, "ShowIndividualPackageDialog", (Func<DialogResult>)(() => throw new IOException("Simulated picker error")));
                    }
                    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is IOException) { }
                    var outsideFiles = Message.Create(table.Handle, 0x0201, IntPtr.Zero, IntPtr.Zero);
                    Assert(!files.PreFilterMessage(ref outsideFiles) && !files.Expanded,
                        "after selecting packages or a picker error, a click outside still closes the section");
                    ClickSection(files);
                    ClickSection(files);
                    Assert(!files.Expanded, "clicking the individual-files heading still closes its section");
                    ClickSection(files);
                    Call(form, "UseFolderPackages");
                    using var expandedFiles = new Bitmap(files.Width, files.Height);
                    files.DrawToBitmap(expandedFiles, new Rectangle(Point.Empty, expandedFiles.Size));
                    expandedFiles.Save(Path.Combine(TestWorkspace.Artifacts, "individual-files.png"));
                }
                ClickSection(settings);
                Assert(!files.Expanded && files.Parent == packages.Parent && InForm(form, files) == filesBefore,
                    "switching to settings restores the individual-files header in its original column");
                var ssl = Field<CheckBox>(form, "chkSsl");
                var port = Field<NumericUpDown>(form, "nHttpsPort");
                var https = All(settings).OfType<TableLayoutPanel>().Single(c => c.Name == "httpsOptions");
                port.Value = 6443;
                ssl.Checked = true;
                Layout(settings);
                Assert(https.GetColumnWidths()[1] > 0 && https.GetColumnWidths()[2] > 0 &&
                    All(settings).Single(c => c.Name == "httpsPortLabel").Text == "Port:", "HTTPS shows the Port label and field");
                ssl.Checked = false;
                Layout(settings);
                Assert(https.GetColumnWidths()[1] == 0 && https.GetColumnWidths()[2] == 0 && port.Value == 6443,
                    "HTTP hides the complete port input while preserving its value");
                if (width == 1000)
                {
                    form.ClientSize = new Size((int)(640 * scale), form.ClientSize.Height);
                    for (int pass = 0; pass < 3; pass++) Layout(form);
                    var anchor = All(form).Single(c => c.Name == "settingsCardPlaceholder");
                    Assert(settings.Location == InForm(form, anchor).Location && settings.Width == anchor.Width &&
                        form.ClientRectangle.Contains(settings.Bounds), "an expanded card follows its original header when the layout changes columns");
                    form.ClientSize = new Size((int)(width * scale), form.ClientSize.Height);
                    for (int pass = 0; pass < 3; pass++) Layout(form);
                }
                settings.Collapse();
            }
        }
        var closingForm = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        var closingSection = All(closingForm).OfType<ExpandableSection>().Single(c => c.Name == "filesCard");
        var payment = Field<Button>(closingForm, "btnPayment");
        try { closingSection.Expand(); }
        finally { closingForm.Dispose(); }
        Assert(closingSection.IsDisposed && payment.IsDisposed, "closing the window also disposes an expanded card and its content");
    }

    private static Rectangle InForm(Form form, Control control)
    {
        Point origin = Point.Empty;
        for (Control? current = control; current is not null && current != form; current = current.Parent)
            origin.Offset(current.Location);
        return new Rectangle(origin, control.ClientSize);
    }

    private static void ClickSection(ExpandableSection section) =>
        typeof(ExpandableSection).GetMethod("OnClick", Private)!.Invoke(section, [EventArgs.Empty]);
}
