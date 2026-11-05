namespace NovaTouchUpdater;

internal static class BuildChannels
{
    internal const string Default = "STABLE";
    internal static readonly string[] Names = ["ALPHA", "BETA", "STABLE"];

    internal static bool IsChannel(string name) => Names.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal static string Normalize(string? name)
    {
        string channel = name?.Trim().ToUpperInvariant() ?? Default;
        return IsChannel(channel) ? channel : throw new ArgumentException("use Build muss ALPHA, BETA oder STABLE sein.");
    }
}
