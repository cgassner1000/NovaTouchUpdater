using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal static class UpdatePackageNames
{
    private static readonly Regex ServerSuiteFormat = new(
        @"^nt\.server\.suite-(\d+\.\d+\.\d+(?:\.\d+)?)(?:[-_].*)?\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryServerSuiteVersion(string path, out string version)
    {
        Match match = ServerSuiteFormat.Match(Path.GetFileName(path));
        version = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }
}
