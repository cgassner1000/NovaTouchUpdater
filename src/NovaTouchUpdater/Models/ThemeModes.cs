namespace NovaTouchUpdater;

internal static class ThemeModes
{
    internal const string System = "System";
    internal static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" or "hell" => "Light",
        "dark" or "dunkel" => "Dark",
        _ => System
    };
}
