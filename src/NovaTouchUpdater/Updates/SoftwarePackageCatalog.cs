using System.IO.Compression;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal sealed record SoftwarePackage(UpdateComponents Component, string Path, string Version, MsiPackageRequest? Msi = null);
internal sealed record SoftwareCatalogResult(SoftwarePackage[] Packages, string[] Warnings);

internal sealed class SoftwarePackageCatalog : IDisposable
{
    private static readonly Regex ServiceZip = new(@"^nt\.(payment|fiscal)_(\d+(?:\.\d+){1,3}).*\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string cache = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovaTouchUpdater", "Packages-" + Guid.NewGuid().ToString("N"));

    internal async Task<SoftwareCatalogResult> ReadAsync(string folder, IProgress<string>? progress, CancellationToken ct)
    {
        folder = System.IO.Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Softwareordner nicht gefunden: " + folder);
        var candidates = new List<SoftwarePackage>();
        var warnings = new List<string>();
        foreach (string file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            ct.ThrowIfCancellationRequested();
            string extension = System.IO.Path.GetExtension(file);
            if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)) continue;
            progress?.Report("Pakete lesen: " + System.IO.Path.GetFileName(file));
            try { candidates.AddRange(await ReadFileAsync(file, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
            { warnings.Add(System.IO.Path.GetFileName(file) + ": " + ex.Message); }
        }
        return new(candidates.GroupBy(p => p.Component).Select(group => group.OrderByDescending(p => NumericVersion(p.Version))
            .ThenByDescending(p => File.GetLastWriteTimeUtc(p.Path)).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).First()).ToArray(), warnings.ToArray());
    }

    internal async Task<SoftwarePackage[]> ReadFileAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        path = System.IO.Path.GetFullPath(path);
        Match service = ServiceZip.Match(System.IO.Path.GetFileName(path));
        if (service.Success)
            return [new(service.Groups[1].Value.Equals("payment", StringComparison.OrdinalIgnoreCase) ? UpdateComponents.Payment : UpdateComponents.Fiscal, path, service.Groups[2].Value)];
        if (UpdatePackageNames.TryServerSuiteVersion(path, out string suiteVersion)) return [new(UpdateComponents.ServerSuite, path, suiteVersion)];
        if (System.IO.Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            MsiMetadata metadata = MsiPackageReader.Read(path);
            if (metadata.Component == UpdateComponents.None) return [];
            string parent = System.IO.Path.GetDirectoryName(path)!;
            TransferFileRequest[] companions = metadata.ExternalCabinets.Select(relative =>
            {
                string full = SafeChild(parent, relative);
                if (!File.Exists(full)) throw new FileNotFoundException("Externe MSI-CAB-Datei fehlt: " + full);
                return new TransferFileRequest { LocalPath = full, RelativePath = relative };
            }).ToArray();
            return [Create(path, System.IO.Path.GetFileName(path), metadata, companions)];
        }
        if (!System.IO.Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return [];
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry => entry.FullName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length == 0) return [];
        foreach (var entry in archive.Entries) _ = SafeChild(cache, entry.FullName);
        string id = await WorkerRunner.Sha256Async(path, ct).ConfigureAwait(false);
        var packages = new List<SoftwarePackage>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string entryId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.FullName)));
            string local = System.IO.Path.Combine(cache, id, entryId + ".msi");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
            if (!File.Exists(local))
            {
                string partial = local + ".partial";
                try
                {
                    await using (var source = entry.Open())
                    await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                        await source.CopyToAsync(target, ct).ConfigureAwait(false);
                    File.Move(partial, local, true);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            MsiMetadata metadata = MsiPackageReader.Read(local);
            if (metadata.Component == UpdateComponents.None) continue;
            string prefix = (System.IO.Path.GetDirectoryName(entry.FullName.Replace('/', '\\')) ?? "").Replace('\\', '/');
            foreach (string cabinet in metadata.ExternalCabinets)
            {
                string relative = (prefix.Length == 0 ? "" : prefix + "/") + cabinet.Replace('\\', '/');
                if (!archive.Entries.Any(e => e.FullName.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Externe MSI-CAB-Datei fehlt in ZIP: " + relative);
            }
            packages.Add(Create(path, entry.FullName, metadata, []));
        }
        return packages.ToArray();
    }

    private static SoftwarePackage Create(string payload, string relative, MsiMetadata metadata, TransferFileRequest[] companions) =>
        new(metadata.Component, payload, metadata.ProductVersion, new MsiPackageRequest
        {
            Kind = metadata.Component.ToString().ToLowerInvariant(), PayloadPath = payload, RelativeMsiPath = relative,
            ProductName = metadata.ProductName, ProductVersion = metadata.ProductVersion, ProductCode = metadata.ProductCode,
            UpgradeCode = metadata.UpgradeCode, CompanionFiles = companions
        });

    internal static Version NumericVersion(string value)
    {
        if (!Version.TryParse(value, out var version)) throw new InvalidDataException("Ungültige Paketversion: " + value);
        return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    private static string SafeChild(string root, string relative)
    {
        string prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (System.IO.Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Ungültiger Paketpfad: " + relative);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(prefix, relative.Replace('/', '\\')));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Paketpfad verlässt seinen Ordner: " + relative);
        return full;
    }

    public void Dispose()
    {
        // Only this instance's generated cache is owned by the updater.
        string full = System.IO.Path.GetFullPath(cache);
        string parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovaTouchUpdater")).TrimEnd('\\');
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(System.IO.Path.GetFileName(full), "^Packages-[a-f0-9]{32}$")) return;
        if (Directory.Exists(full)) { try { Directory.Delete(full, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
