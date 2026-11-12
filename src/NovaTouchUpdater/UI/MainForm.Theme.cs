namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly ComboBox themeMode = new AppComboBox { Name = "themeMode", AccessibleName = "Design",
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private bool applyingThemePreference;
    private Action<string>? showThemeSaveError = null;

    private Control CreateThemePreference()
    {
        themeMode.Items.AddRange(["Hell", "Dunkel", "System"]);
        ApplyThemePreference(ThemeModes.System);
        themeMode.SelectedIndexChanged += (_, _) =>
        {
            if (applyingThemePreference) return;
            UiTheme.SetMode(SelectedTheme);
            try
            {
                Directory.CreateDirectory(userDataPaths.RootDirectory);
                AppConfigStore.SaveTheme(userDataPaths.ConfigFile, SelectedTheme, startupConfigPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                string message = "Das Design wurde angewendet, konnte aber nicht in der Konfiguration gespeichert werden.\n\n" + ex.Message;
                if (showThemeSaveError is not null) showThemeSaveError(message);
                else AppMessageBox.Show(this, message, "Design speichern", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        return FlowRow(new Label { Text = "Design:", AutoSize = true, Anchor = AnchorStyles.Left }, themeMode);
    }

    private string SelectedTheme => themeMode.SelectedIndex switch { 0 => "Light", 1 => "Dark", _ => ThemeModes.System };

    private void ApplyThemePreference(string? value)
    {
        applyingThemePreference = true;
        try
        {
            string mode = ThemeModes.Normalize(value);
            themeMode.SelectedIndex = mode switch { "Light" => 0, "Dark" => 1, _ => 2 };
            UiTheme.SetMode(mode);
        }
        finally { applyingThemePreference = false; }
    }
}
