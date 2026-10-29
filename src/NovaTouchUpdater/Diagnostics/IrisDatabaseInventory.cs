using Microsoft.Win32;
using System.Security;
using System.Text;

namespace NovaTouchUpdater;

internal sealed record IrisRegistration(string Name, string Directory, string ConfigurationFile, string Version, string Error = "");
internal sealed record IrisInstanceInfo(string Name, string Directory, string ConfigurationFile, string Version, string[] Namespaces, string Error = "", int SuperServerPort = 0, string ConnectionError = "");
internal sealed record IrisInventoryResult(IrisInstanceInfo[] Instances, string[] Warnings);

// Local registration and CPF reads only: no database login, commands or service changes.
internal sealed class IrisDatabaseInventory
{
    private const string RegistryPath = @"SOFTWARE\InterSystems\IRIS\Configurations";
    private readonly Func<RegistryView, CancellationToken, IrisRegistration[]> readRegistry;

    internal IrisDatabaseInventory(Func<RegistryView, CancellationToken, IrisRegistration[]>? readRegistry = null) =>
        this.readRegistry = readRegistry ?? ReadRegistry;

    internal Task<IrisInventoryResult> ReadAsync(CancellationToken ct) => Task.Run(() =>
    {
        var registrations = new List<IrisRegistration>();
        var warnings = new List<string>();
        foreach (RegistryView view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            ct.ThrowIfCancellationRequested();
            try { registrations.AddRange(readRegistry(view, ct)); }
            catch (Exception ex) when (IsReadError(ex)) { warnings.Add($"IRIS-Registrierung ({view}) konnte nicht gelesen werden: {ex.Message}"); }
        }
        var instances = new List<IrisInstanceInfo>();
        foreach (var group in registrations.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            IrisRegistration registration = group.OrderByDescending(item => item.Error.Length == 0)
                .ThenByDescending(item => item.ConfigurationFile.Length > 0).ThenByDescending(item => item.Directory.Length > 0).First();
            instances.Add(ReadInstance(registration, ct));
        }
        return new IrisInventoryResult(instances.ToArray(), warnings.ToArray());
    }, ct);

    private static IrisRegistration[] ReadRegistry(RegistryView view, CancellationToken ct)
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var configurations = registry.OpenSubKey(RegistryPath, writable: false);
        if (configurations is null) return [];
        var registrations = new List<IrisRegistration>();
        foreach (string name in configurations.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var key = configurations.OpenSubKey(name, writable: false);
                if (key is null) throw new IOException("Instanzregistrierung ist nicht mehr vorhanden.");
                string Read(string value, string? child = null)
                {
                    string text = Convert.ToString(key.GetValue(value))?.Trim() ?? "";
                    if (text.Length > 0 || child is null) return text;
                    using var subkey = key.OpenSubKey(child, writable: false);
                    return Convert.ToString(subkey?.GetValue(""))?.Trim() ?? "";
                }
                string cpf = Read("ConfigFile");
                if (cpf.Length == 0) cpf = Read("Properties", "Properties");
                registrations.Add(new(name, Read("Directory", "Directory"), cpf, Read("Version")));
            }
            catch (Exception ex) when (IsReadError(ex)) { registrations.Add(new(name, "", "", "", ex.Message)); }
        }
        return registrations.ToArray();
    }

    private static IrisInstanceInfo ReadInstance(IrisRegistration registration, CancellationToken ct)
    {
        string directory = registration.Directory, cpf = registration.ConfigurationFile;
        try
        {
            if (registration.Error.Length > 0) throw new IOException(registration.Error);
            directory = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
            cpf = Environment.ExpandEnvironmentVariables(cpf.Trim().Trim('"'));
            if (directory.Length == 0 && Path.IsPathFullyQualified(cpf)) directory = Path.GetDirectoryName(cpf)!;
            if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("Kein gültiger IRIS-Installationspfad in der Registrierung.");
            directory = Path.GetFullPath(directory);
            cpf = Path.GetFullPath(cpf.Length == 0 ? Path.Combine(directory, "iris.cpf") : Path.IsPathFullyQualified(cpf) ? cpf : Path.Combine(directory, cpf));
            using var stream = new FileStream(cpf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string configuration = reader.ReadToEnd();
            int port = ReadSuperServerPort(new StringReader(configuration), ct);
            return new(registration.Name, directory, cpf, registration.Version, ReadNamespaces(new StringReader(configuration), ct),
                SuperServerPort: port, ConnectionError: port == 0 ? "Kein gültiger [Startup] DefaultPort in der IRIS-Konfiguration." : "");
        }
        catch (Exception ex) when (IsReadError(ex)) { return new(registration.Name, directory, cpf, registration.Version, [], ex.Message); }
    }

    internal static string[] ReadNamespaces(TextReader reader, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inNamespaces = false, foundSection = false;
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            line = line.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            int end = line.IndexOf(']');
            if (line[0] == '[' && end > 0)
            {
                inNamespaces = line[1..end].Trim().Equals("Namespaces", StringComparison.OrdinalIgnoreCase);
                foundSection |= inNamespaces;
                continue;
            }
            int equals = line.IndexOf('=');
            if (!inNamespaces || equals <= 0) continue;
            string name = line[..equals].Trim();
            if (name.Length > 0 && !name.Equals("%SYS", StringComparison.OrdinalIgnoreCase)) names.Add(name);
        }
        ct.ThrowIfCancellationRequested();
        if (!foundSection) throw new InvalidDataException("Der Abschnitt [Namespaces] fehlt in der IRIS-Konfigurationsdatei.");
        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static int ReadSuperServerPort(TextReader reader, CancellationToken ct)
    {
        bool startup = false;
        int port = 0, occurrences = 0;
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            line = line.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            int end = line.IndexOf(']');
            if (line[0] == '[' && end > 0) { startup = line[1..end].Trim().Equals("Startup", StringComparison.OrdinalIgnoreCase); continue; }
            int equals = line.IndexOf('=');
            if (!startup || equals <= 0 || !line[..equals].Trim().Equals("DefaultPort", StringComparison.OrdinalIgnoreCase)) continue;
            occurrences++;
            if (!int.TryParse(line[(equals + 1)..].Trim(), out port) || port is < 1 or > 65535) return 0;
        }
        return occurrences == 1 ? port : 0;
    }

    private static bool IsReadError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
