using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace NovaTouchUpdater;

internal static partial class UiTheme
{
    private static readonly List<WeakReference<Form>> Forms = [];
    private static readonly object FormsLock = new();
    private static readonly ConditionalWeakTable<Control, object> NativeControls = new();
    private static bool listening;
    internal static string Mode { get; private set; } = ThemeModes.System;
    internal static bool IsDark { get; private set; } = ReadSystemDark();
    internal static Func<bool> SystemDarkReader { get; set; } = ReadSystemDark;

    internal static void SetMode(string? mode)
    {
        Mode = ThemeModes.Normalize(mode);
        RefreshSystemTheme();
    }

    internal static void RefreshSystemTheme()
    {
        IsDark = !SystemInformation.HighContrast && (Mode == "Dark" || Mode == ThemeModes.System && SystemDarkReader());
        WeakReference<Form>[] forms;
        lock (FormsLock) forms = Forms.ToArray();
        foreach (var reference in forms)
        {
            if (!reference.TryGetTarget(out var form) || form.IsDisposed || !form.IsHandleCreated) continue;
            if (form.InvokeRequired)
            {
                try { form.BeginInvoke(() => { if (!form.IsDisposed) Apply(form); }); }
                catch (InvalidOperationException) { /* The window closed during the notification. */ }
            }
            else Apply(form);
        }
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    private static void Register(Form form)
    {
        lock (FormsLock)
        {
            if (Forms.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, form))) return;
            Forms.Add(new(form));
        }
        form.HandleCreated += (_, _) => Apply(form);
        form.Disposed += (_, _) =>
        {
            lock (FormsLock)
            {
                Forms.RemoveAll(reference => !reference.TryGetTarget(out var existing) || existing.IsDisposed);
                if (Forms.Count != 0 || !listening) return;
            }
            SystemEvents.UserPreferenceChanged -= SystemPreferencesChanged;
            listening = false;
        };
        if (listening) return;
        SystemEvents.UserPreferenceChanged += SystemPreferencesChanged;
        listening = true;
    }

    private static void SystemPreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => RefreshSystemTheme();

    private static void ApplyWindowFrame(Form form)
    {
        if (!form.IsHandleCreated) return;
        bool useDark = IsDark && !SystemInformation.HighContrast;
        int dark = useDark ? 1 : 0;
        // Unsupported attributes are ignored by older Windows versions.
        _ = DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        int caption = useDark ? ColorTranslator.ToWin32(Background) : -1;
        int text = useDark ? ColorTranslator.ToWin32(Text) : -1;
        _ = DwmSetWindowAttribute(form.Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private static void StyleNativeControl(Control control)
    {
        if (!NativeControls.TryGetValue(control, out _))
        {
            NativeControls.Add(control, new object());
            control.HandleCreated += (_, _) => ApplyNativeTheme(control);
        }
        ApplyNativeTheme(control);
    }

    private static void ApplyNativeTheme(Control control)
    {
        if (control.IsHandleCreated)
            _ = SetWindowTheme(control.Handle, SystemInformation.HighContrast ? null : IsDark ? "DarkMode_Explorer" : "Explorer", null);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);

    internal static void StyleMenu(ContextMenuStrip menu)
    {
        menu.Renderer = SystemInformation.HighContrast ? new ToolStripSystemRenderer() : new ToolStripProfessionalRenderer(new MenuColors());
        menu.BackColor = SystemInformation.HighContrast ? SystemColors.Control : Surface;
        menu.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Text;
        foreach (ToolStripItem item in menu.Items) { item.BackColor = menu.BackColor; item.ForeColor = menu.ForeColor; }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => AccentSoft;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color CheckBackground => AccentSoft;
        public override Color CheckSelectedBackground => AccentSoft;
        public override Color CheckPressedBackground => AccentSoft;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }
}
