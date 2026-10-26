using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal sealed record AppRelease(Version Version, Uri DownloadUrl, long Size, string Sha256);

internal sealed class AppReleaseClient(HttpClient http)
{
    internal const string Repository = "cgassner1000/NovaTouchUpdater";
    internal static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private const long MaximumDownloadSize = 1024L * 1024 * 1024;

    internal static Version CurrentVersion => ParseVersion(
        typeof(AppReleaseClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]) ?? Normalize(typeof(AppReleaseClient).Assembly.GetName().Version ?? new Version(1, 0, 0));

    internal static Version? ParseVersion(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"^v?\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.CultureInvariant)) return null;
        return Version.TryParse(value.TrimStart('v'), out var version) ? Normalize(version) : null;
    }

    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    internal static string DisplayVersion(Version value) => value.Revision > 0 ? value.ToString(4) : value.ToString(3);

    internal async Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd($"NovaTouchUpdater/{DisplayVersion(currentVersion)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        return ReadRelease(json.RootElement, currentVersion);
    }

    internal static AppRelease? ReadRelease(JsonElement release, Version currentVersion)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        Version? version = ParseVersion(tag);
        if (version is null || version <= Normalize(currentVersion)) return null;
        string name = $"NovaTouchUpdater-{tag}.exe";
        string expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            long size = asset.GetProperty("size").GetInt64();
            string? url = asset.GetProperty("browser_download_url").GetString();
            string? digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (url != expectedUrl || size < 2 || size > MaximumDownloadSize || digest is null ||
                !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Die GitHub-Release-EXE hat keine gültigen Download- oder Prüfsummendaten.");
            return new AppRelease(version, new Uri(url), size, digest[7..]);
        }
        return null;
    }

    internal async Task DownloadAsync(AppRelease release, string destination, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        bool created = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            request.Headers.UserAgent.ParseAdd($"NovaTouchUpdater/{DisplayVersion(CurrentVersion)}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("Die Downloadgröße stimmt nicht mit dem GitHub-Release überein.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                created = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024];
                long received = 0;
                int lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > release.Size) throw new InvalidDataException("Die Downloadgröße überschreitet die Releasegröße.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    int percent = (int)(received * 99 / release.Size);
                    if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                }
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Die heruntergeladene EXE ist unvollständig oder ihre SHA256-Prüfsumme stimmt nicht.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            progress?.Report(100);
        }
        catch
        {
            if (created) File.Delete(destination);
            throw;
        }
    }
}
