using System.Text.Json.Nodes;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void ThemeTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "theme-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        Directory.CreateDirectory(paths.RootDirectory);
        try
        {
            AppConfigStore.Save(paths.ConfigFile, new UpdaterConfig { Hosts = ["SAVED.invalid"], Username = "saved", ScanOnStartup = false }, "Synthetic theme password");
            var saved = JsonNode.Parse(File.ReadAllText(paths.ConfigFile))!.AsObject();
            string encrypted = saved["EncryptedPassword"]!.GetValue<string>();
            saved["FutureSetting"] = "keep";
            File.WriteAllText(paths.ConfigFile, saved.ToJsonString());
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner(), dataPaths: paths);
            _ = form.Handle;
            form.Show();
            Application.DoEvents();
            Populate(form);
            var information = Field<DataGridView>(form, "table").Rows[0].Cells["information"];
            information.Tag = 64;
            information.Value = "64 % · IRIS:1972/USER · Novacom-NT-BOH.xml · Klasse 64 von 100";
            var combo = Field<ComboBox>(form, "themeMode");
            Assert(combo.Items.Cast<string>().SequenceEqual(new[] { "Hell", "Dunkel", "System" }) && combo.SelectedIndex == 2 && !UiTheme.IsDark,
                "General offers Light, Dark and System with System as the default");
            var general = All(form).OfType<Label>().Single(label => label.Text == "ALLGEMEIN");
            Assert(general.Parent!.Parent == combo.Parent!.Parent, "the theme choice belongs to General settings");
            Field<TextBox>(form, "txtUser").Text = "unsaved";
            combo.SelectedIndex = 1;
            Assert(UiTheme.IsDark && form.BackColor == UiTheme.Background && Field<DataGridView>(form, "table").DefaultCellStyle.ForeColor == UiTheme.Text,
                "selecting Dark immediately updates the window and host table");
            using (var bitmap = new Bitmap(combo.Width, combo.Height))
            {
                combo.DrawToBitmap(bitmap, combo.ClientRectangle);
                Assert(bitmap.GetPixel(combo.Width - 32, combo.Height / 2).ToArgb() == UiTheme.Input.ToArgb(),
                    "the native theme dropdown has no white background in Dark mode");
            }
            saved = JsonNode.Parse(File.ReadAllText(paths.ConfigFile))!.AsObject();
            Assert(saved["Theme"]!.GetValue<string>() == "Dark" && saved["Username"]!.GetValue<string>() == "saved" &&
                saved["EncryptedPassword"]!.GetValue<string>() == encrypted && saved["FutureSetting"]!.GetValue<string>() == "keep" && saved.Last().Key == "Hosts",
                "automatic theme persistence preserves saved hosts, credentials, unknown settings and unsaved form edits");
            using var log = new HostLogDialog("THEME-PREVIEW", _ => ["2026-11-12 13:00:00.000 [THEME-PREVIEW] Installation: Paket wird verarbeitet."]);
            Assert(log.BackColor == UiTheme.Background && All(log).OfType<TextBox>().All(input => input.BackColor.GetBrightness() < .2),
                "new log windows and their detail fields use the active dark theme");
            using var confirmation = AppMessageBox.CreateDialog("Ausführung: Lokal\r\nGeplante Komponenten:\r\nNovaTouch Control 2026.4.9804: 1 Host(s)\r\n\r\nSoll das Update gestartet werden?",
                "Update bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            Assert(((Button)confirmation.AcceptButton!).DialogResult == DialogResult.No && ((Button)confirmation.CancelButton!).DialogResult == DialogResult.No,
                "dark confirmations preserve the original safe default and Escape behavior");
            foreach (Control control in All(confirmation).Concat(All(log))) _ = control.Handle;
            typeof(HostLogDialog).GetMethod("RefreshLog", Private)!.Invoke(log, null);
            for (int pass = 0; pass < 3; pass++) { Layout(confirmation); Layout(log); }
            foreach (Control control in All(form)) _ = control.Handle;
            for (int pass = 0; pass < 3; pass++) Layout(form);
            Call(form, "ApplyColumnWidths");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "theme-dark-main.png")); }
            var settings = All(form).OfType<ExpandableSection>().Single(section => section.Name == "settingsCard");
            ClickSection(settings);
            for (int pass = 0; pass < 3; pass++) Layout(form);
            using (var bitmap = new Bitmap(settings.Width, settings.Height))
            {
                settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "theme-dark-settings.png"));
                Point position = settings.PointToClient(combo.PointToScreen(new Point(combo.Width - 32, combo.Height / 2)));
                Assert(bitmap.GetPixel(position.X, position.Y).ToArgb() == UiTheme.Input.ToArgb(),
                    "the design dropdown is present in the rendered settings window");
            }
            settings.Collapse();
            using (var bitmap = new Bitmap(confirmation.Width, confirmation.Height))
            { confirmation.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "theme-dark-confirmation.png")); }
            using (var bitmap = new Bitmap(log.Width, log.Height))
            { log.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "theme-dark-log.png")); }
            var menu = Field<ContextMenuStrip>(form, "hostColumnsMenu");
            Assert(menu.BackColor == UiTheme.Surface && menu.ForeColor == UiTheme.Text, "host column menus follow the active theme");
            combo.SelectedIndex = 0;
            Assert(!UiTheme.IsDark && form.BackColor == Color.FromArgb(244, 247, 251) && log.BackColor == UiTheme.Background && confirmation.BackColor == UiTheme.Background,
                "switching back to Light refreshes all open windows without restarting");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "theme-light-main.png")); }
            UiTheme.SystemDarkReader = () => true;
            combo.SelectedIndex = 2;
            Assert(UiTheme.IsDark && AppConfigStore.Load(paths.ConfigFile).Settings.Theme == "System", "System follows the Windows app theme and stores the preference");
            UiTheme.SystemDarkReader = () => false;
            UiTheme.RefreshSystemTheme();
            Assert(!UiTheme.IsDark && combo.SelectedIndex == 2 && log.BackColor == UiTheme.Background, "a Windows theme change updates open windows while preserving System selection");
            UiTheme.SetMode("Dark");
            UiTheme.RefreshSystemTheme();
            Assert(UiTheme.IsDark, "an explicit Dark preference is unaffected by Windows Light mode");
            byte[] beforeLoad = File.ReadAllBytes(paths.ConfigFile);
            Call(form, "ApplyLoadedConfig", new LoadedConfig(new UpdaterConfig { Theme = "Dark", Hosts = [] }, "", null));
            Assert(combo.SelectedIndex == 1 && beforeLoad.SequenceEqual(File.ReadAllBytes(paths.ConfigFile)) && ((UpdaterConfig)Call(form, "CaptureConfig")!).Theme == "Dark",
                "loading a saved theme does not rewrite the config and a normal save captures it");
            SetField(form, "busy", true); Call(form, "UpdateButtons");
            Assert(!combo.Enabled, "the preference is disabled during active processing");
            SetField(form, "busy", false); Call(form, "UpdateButtons");
            string? error = null;
            SetField(form, "showThemeSaveError", new Action<string>(message => error = message));
            byte[] beforeFailure = File.ReadAllBytes(paths.ConfigFile);
            using (File.Open(paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None)) combo.SelectedIndex = 0;
            Assert(error is not null && !UiTheme.IsDark && beforeFailure.SequenceEqual(File.ReadAllBytes(paths.ConfigFile)),
                "a blocked save reports the failure, retains the config and still applies the selected design");
            using var scaled = new MainForm(preview: true, new FakeRunner(), new FakeRunner(), dataPaths: paths);
            form.Hide();
            Call(scaled, "ApplyThemePreference", "Dark");
            var fonts = All(scaled).ToDictionary(control => control, control => control.Font);
            scaled.Scale(new SizeF(2, 2));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * 2, font.Style);
            scaled.MinimumSize = Size.Empty;
            scaled.ClientSize = new Size(2000, 1960);
            scaled.Show();
            Application.DoEvents();
            foreach (Control control in All(scaled)) _ = control.Handle;
            for (int pass = 0; pass < 3; pass++) Layout(scaled);
            var scaledSettings = All(scaled).OfType<ExpandableSection>().Single(section => section.Name == "settingsCard");
            ClickSection(scaledSettings);
            for (int pass = 0; pass < 3; pass++) Layout(scaled);
            var scaledChoice = Field<ComboBox>(scaled, "themeMode");
            Assert(scaledChoice.Height >= scaledChoice.Font.Height && scaledSettings.Bottom <= scaled.ClientSize.Height,
                "the Dark settings and design choice remain readable and reachable at 200 percent scaling");
            using (var bitmap = new Bitmap(scaledSettings.Width, scaledSettings.Height))
            { scaledSettings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "theme-dark-settings-200.png")); }
        }
        finally
        {
            UiTheme.SystemDarkReader = () => false;
            UiTheme.SetMode("System");
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Theme fixture outside artifacts.");
            Directory.Delete(root, recursive: true);
        }
    }
}
