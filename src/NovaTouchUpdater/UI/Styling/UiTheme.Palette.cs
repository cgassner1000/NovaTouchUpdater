namespace NovaTouchUpdater;

internal static partial class UiTheme
{
    // Each role has a light and dark counterpart, including status and interaction states.
    private enum Role { Background, Surface, Text, Muted, Border, Accent, AccentSoft, Success, SuccessSoft,
        Danger, DangerSoft, Warning, WarningSoft, Input, ReadOnlySurface, Header, Alternate, Hover,
        Primary, PrimaryHover, PrimaryText, AccentHover, DangerHover, Disabled, DisabledText,
        ToggleOn, ToggleHover, ToggleOff, ToggleOffHover, Thumb, NeutralSoft, DropSurface, DropBorder,
        ProgressTrack, ProgressSelectedTrack, ProgressFill, ProgressSelectedFill, ProgressText }

    private static readonly Color[] Light = Colors(
        "F4F7FB FFFFFF 1C2A3E 5F7086 DCE4EE 2563EB EBF2FF 16704F E7F6ED B1303F FDECEF 885114 FFF5E1 " +
        "FFFFFF F8FAFD F1F5FA F9FBFE F0F5FC 2563EB 1D4ED8 FFFFFF DCE9FF FADEE4 EFF2F7 8E9AAD " +
        "EA6F19 D95E0E AEB9C8 94A3B8 FFFFFF EFF3F8 F8FAFF BACDEE F2FAF4 E3F3E9 A3DAB0 8BCF9D 142D1D");
    private static readonly Color[] Dark = Colors(
        "11161F 1B2330 DEE5F0 9AAABE 344153 9DBBDC 283B53 9AC9AE 243D34 D9A7AF 432F39 D6BF97 40392C " +
        "151D28 19212E 222D3D 18202C 263345 3D5F85 4B7098 EDF3FA 304761 503742 202936 8190A5 " +
        "587DA5 688DB4 465367 56677E DDE6F2 273344 182230 425C7B 1C2D2A 263D36 355B4B 426E5B DAECE1");

    private static Color[] Colors(string hex) => hex.Split(' ').Select(value => Color.FromArgb(
        255, Color.FromArgb(Convert.ToInt32(value, 16)))).ToArray();
    private static Color Get(Role role) => (IsDark ? Dark : Light)[(int)role];
    internal static Color Background => Get(Role.Background);
    internal static Color Surface => Get(Role.Surface);
    internal static Color Text => Get(Role.Text);
    internal static Color Muted => Get(Role.Muted);
    internal static Color Border => Get(Role.Border);
    internal static Color Accent => Get(Role.Accent);
    internal static Color AccentSoft => Get(Role.AccentSoft);
    internal static Color Success => Get(Role.Success);
    internal static Color SuccessSoft => Get(Role.SuccessSoft);
    internal static Color Danger => Get(Role.Danger);
    internal static Color DangerSoft => Get(Role.DangerSoft);
    internal static Color Warning => Get(Role.Warning);
    internal static Color WarningSoft => Get(Role.WarningSoft);
    internal static Color Input => Get(Role.Input);
    internal static Color ReadOnlySurface => Get(Role.ReadOnlySurface);
    internal static Color Header => Get(Role.Header);
    internal static Color Alternate => Get(Role.Alternate);
    internal static Color Hover => Get(Role.Hover);
    internal static Color Primary => Get(Role.Primary);
    internal static Color PrimaryHover => Get(Role.PrimaryHover);
    internal static Color PrimaryText => Get(Role.PrimaryText);
    internal static Color AccentHover => Get(Role.AccentHover);
    internal static Color DangerHover => Get(Role.DangerHover);
    internal static Color Disabled => Get(Role.Disabled);
    internal static Color DisabledText => Get(Role.DisabledText);
    internal static Color ToggleOn => Get(Role.ToggleOn);
    internal static Color ToggleHover => Get(Role.ToggleHover);
    internal static Color ToggleOff => Get(Role.ToggleOff);
    internal static Color ToggleOffHover => Get(Role.ToggleOffHover);
    internal static Color Thumb => Get(Role.Thumb);
    internal static Color NeutralSoft => Get(Role.NeutralSoft);
    internal static Color DropSurface => Get(Role.DropSurface);
    internal static Color DropBorder => Get(Role.DropBorder);
    internal static Color ProgressTrack => Get(Role.ProgressTrack);
    internal static Color ProgressSelectedTrack => Get(Role.ProgressSelectedTrack);
    internal static Color ProgressFill => Get(Role.ProgressFill);
    internal static Color ProgressSelectedFill => Get(Role.ProgressSelectedFill);
    internal static Color ProgressText => Get(Role.ProgressText);

    private static Color Remap(Color color, bool background, Color inherited)
    {
        if (color == Color.Transparent || color.IsEmpty) return color;
        for (int i = 0; i < Light.Length; i++)
            if (color.ToArgb() == Light[i].ToArgb() || color.ToArgb() == Dark[i].ToArgb()) return Get((Role)i);
        if (color == SystemColors.Control || color == SystemColors.Window) return background ? inherited : Text;
        if (color == SystemColors.ControlText || color == SystemColors.WindowText) return Text;
        if (color == SystemColors.GrayText) return DisabledText;
        if (color.ToArgb() == Color.Firebrick.ToArgb()) return Danger;
        if (!background && (color.R == 85 && color.G == 98 || color.R == 75 && color.G == 89)) return Muted;
        return color;
    }

    private static void RemapStyle(DataGridViewCellStyle style)
    {
        style.BackColor = Remap(style.BackColor, true, Surface);
        style.ForeColor = Remap(style.ForeColor, false, Text);
        style.SelectionBackColor = Remap(style.SelectionBackColor, true, AccentSoft);
        style.SelectionForeColor = Remap(style.SelectionForeColor, false, Text);
    }

    private static void RemapNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            node.ForeColor = Remap(node.ForeColor, false, Text);
            node.BackColor = Remap(node.BackColor, true, Input);
            RemapNodes(node.Nodes);
        }
    }
}
