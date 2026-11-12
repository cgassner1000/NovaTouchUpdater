namespace NovaTouchUpdater;

internal static class HostColumnSettings
{
    internal static string[] Defaults => ["include", "hostname", "connection", "dotnet48", "payment", "fiscal", "serversuite", "pos", "ntc", "information", "result"];

    internal static string[] Normalize(IEnumerable<string>? columns)
    {
        string[] known = [.. Defaults, "ksm", "ntv"];
        return columns is null ? Defaults : columns
            .Select(name => known.FirstOrDefault(key => key.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
