using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace NovaTouchUpdater;

internal sealed class UpdaterConfig
{
    [JsonPropertyOrder(int.MaxValue)]
    public string[]? Hosts { get; set; } = [];
    public string? RemotePath { get; set; }
    public string? Username { get; set; }
    public bool UseSsl { get; set; }
    public bool IrisUseTls { get; set; }
    public bool CompileAfterInstallation { get; set; }
    public bool UploadToZzzSoftware { get; set; } = true;
    public bool RestartPosAfterUpdate { get; set; }
    public bool ScanOnStartup { get; set; } = true;
    public string Theme { get; set; } = ThemeModes.System;
    public bool SkipLocalPackageCopy { get; set; }
    public bool SkipRemotePackageCopy { get; set; }
    public bool SkipZipTransfer { get; set; }
    public int HttpsPort { get; set; } = 5986;
    public int ScanParallelism { get; set; } = 40;
    public int UpdateParallelism { get; set; } = 8;
    public bool ForceUpdate { get; set; }
    public string? WinRmCertificatePath { get; set; }
    public string? WinRmPrivateKeyPath { get; set; }
    public string ExecutionMode { get; set; } = "Remote";
    public string? SoftwarePath { get; set; }
    public string UseBuild { get; set; } = BuildChannels.Default;
    public string[]? VisibleHostColumns { get; set; }
    public string? PasswordProtectionScope { get; set; }
    public string? EncryptedPassword { get; set; }

    // Read compatibility only. Save always builds a new config without this field.
    [JsonPropertyName("Password"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPassword { get; set; }
}

internal sealed record LoadedConfig(UpdaterConfig Settings, string Password, string? PasswordWarning);

internal static class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static LoadedConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<UpdaterConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Die Konfiguration enthält keine Einstellungen.");
        Validate(config);
        config.Hosts = NormalizeHosts(config.Hosts);
        string password = "";
        string? warning = null;
        if (config.EncryptedPassword is not null)
        {
            try { password = DecryptPassword(config.EncryptedPassword, config.PasswordProtectionScope); }
            catch (Exception ex) when (ex is CryptographicException or FormatException or DecoderFallbackException)
            {
                warning = "Das gespeicherte Passwort konnte auf diesem Rechner nicht entschlüsselt werden oder ist beschädigt. " +
                    "Die übrigen Einstellungen wurden geladen. Bitte das Passwort neu eingeben; es wird automatisch verschlüsselt gespeichert. " +
                    "Eine ältere, benutzergebundene Config kann alternativ vom ursprünglichen Windows-Benutzer geöffnet werden; nach erneuter Passworteingabe wird der Schutz aktualisiert.";
            }
        }
        else password = config.LegacyPassword ?? "";
        config.LegacyPassword = null;
        return new LoadedConfig(config, password, warning);
    }

    public static void Save(string path, UpdaterConfig settings, string password)
    {
        Validate(settings);
        var config = new UpdaterConfig
        {
            Hosts = NormalizeHosts(settings.Hosts), RemotePath = settings.RemotePath,
            Username = settings.Username, UseSsl = settings.UseSsl, HttpsPort = settings.HttpsPort,
            ScanParallelism = settings.ScanParallelism, UpdateParallelism = settings.UpdateParallelism, ForceUpdate = settings.ForceUpdate,
            WinRmCertificatePath = settings.WinRmCertificatePath, WinRmPrivateKeyPath = settings.WinRmPrivateKeyPath,
            IrisUseTls = settings.IrisUseTls,
            CompileAfterInstallation = settings.CompileAfterInstallation,
            UploadToZzzSoftware = settings.UploadToZzzSoftware, RestartPosAfterUpdate = settings.RestartPosAfterUpdate,
            ExecutionMode = settings.ExecutionMode, SoftwarePath = settings.SoftwarePath, UseBuild = settings.UseBuild,
            ScanOnStartup = settings.ScanOnStartup,
            Theme = settings.Theme,
            VisibleHostColumns = settings.VisibleHostColumns,
            SkipLocalPackageCopy = settings.SkipLocalPackageCopy, SkipRemotePackageCopy = settings.SkipRemotePackageCopy, SkipZipTransfer = settings.SkipZipTransfer,
            PasswordProtectionScope = nameof(DataProtectionScope.LocalMachine),
            EncryptedPassword = EncryptPassword(password)
        };
        WriteAtomically(path, config);
    }

    public static void SaveVisibleHostColumns(string path, IEnumerable<string> columns, string? fallbackConfigPath = null)
        => SavePreference(path, nameof(UpdaterConfig.VisibleHostColumns), JsonSerializer.SerializeToNode(HostColumnSettings.Normalize(columns)), fallbackConfigPath);

    public static void SaveTheme(string path, string theme, string? fallbackConfigPath = null)
        => SavePreference(path, nameof(UpdaterConfig.Theme), JsonValue.Create(ThemeModes.Normalize(theme)), fallbackConfigPath);

    public static void SaveChangedSettings(string path, UpdaterConfig settings, IEnumerable<string> fields, string? changedPassword = null, string? fallbackConfigPath = null)
    {
        Validate(settings);
        settings.Hosts = NormalizeHosts(settings.Hosts);
        var snapshot = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        var changes = new Dictionary<string, JsonNode?>();
        foreach (string field in fields.Distinct())
        {
            if (typeof(UpdaterConfig).GetProperty(field) is null || field is nameof(UpdaterConfig.LegacyPassword) or nameof(UpdaterConfig.EncryptedPassword) or nameof(UpdaterConfig.PasswordProtectionScope))
                throw new ArgumentException("Ungültige Konfigurationseinstellung: " + field);
            changes[field] = snapshot[field]?.DeepClone();
        }
        SavePreferences(path, changes, fallbackConfigPath, changedPassword, updateCredentials: true);
    }

    private static void SavePreference(string path, string name, JsonNode? value, string? fallbackConfigPath)
        => SavePreferences(path, new Dictionary<string, JsonNode?> { [name] = value }, fallbackConfigPath);

    public static void SaveWinRmCertificatePaths(string path, string certificatePath, string privateKeyPath, string? fallbackConfigPath = null)
        => SavePreferences(path, new Dictionary<string, JsonNode?>
        {
            [nameof(UpdaterConfig.WinRmCertificatePath)] = JsonValue.Create(certificatePath),
            [nameof(UpdaterConfig.WinRmPrivateKeyPath)] = JsonValue.Create(privateKeyPath)
        }, fallbackConfigPath);

    private static void SavePreferences(string path, Dictionary<string, JsonNode?> values, string? fallbackConfigPath, string? changedPassword = null, bool updateCredentials = false)
    {
        // Change only these preferences: retain saved credentials, unknown
        // settings and unsaved form edits without decrypting/re-encrypting them.
        string source = File.Exists(path) ? path : fallbackConfigPath ?? path;
        JsonObject config;
        try
        {
            config = File.Exists(source) ? JsonNode.Parse(File.ReadAllText(source)) as JsonObject
                ?? throw new JsonException("Die Konfiguration enthält keine Einstellungen.") : new JsonObject();
            if (updateCredentials)
            {
                var legacy = config.Where(property => property.Key.Equals("Password", StringComparison.OrdinalIgnoreCase)).ToArray();
                bool encrypted = config.Any(property => property.Key.Equals(nameof(UpdaterConfig.EncryptedPassword), StringComparison.OrdinalIgnoreCase));
                string? password = changedPassword;
                if (password is null && !encrypted && legacy.Length > 0)
                    password = legacy[0].Value?.GetValue<string>() ?? "";
                if (password is not null)
                {
                    values[nameof(UpdaterConfig.EncryptedPassword)] = JsonValue.Create(EncryptPassword(password));
                    values[nameof(UpdaterConfig.PasswordProtectionScope)] = JsonValue.Create(nameof(DataProtectionScope.LocalMachine));
                }
                if (password is not null || encrypted)
                    foreach (var property in legacy) config.Remove(property.Key);
            }
            foreach (string key in config.Select(property => property.Key)
                .Where(key => values.Keys.Any(name => key.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray())
                config.Remove(key);
        }
        catch (ArgumentException ex) { throw new JsonException("Die Konfiguration enthält doppelte Einträge.", ex); }
        foreach (var (name, value) in values) config[name] = value;
        // As with a normal config save, keep the host list at the end.
        var hosts = config.Where(property => property.Key.Equals(nameof(UpdaterConfig.Hosts), StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var property in hosts) config.Remove(property.Key);
        foreach (var property in hosts) config[property.Key] = property.Value;
        WriteAtomically(path, config);
    }

    private static void WriteAtomically<T>(string path, T config)
    {
        string target = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(target)!;
        string temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, config, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // Publish only the complete file; retain an existing config if
            // writing or the final replacement fails.
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Validate(UpdaterConfig settings)
    {
        settings.Theme = ThemeModes.Normalize(settings.Theme);
        if (settings.HttpsPort is < 1 or > 65535) throw new JsonException("HttpsPort muss zwischen 1 und 65535 liegen.");
        if (settings.ScanParallelism is < 1 or > 150) throw new JsonException("Prüfen parallel muss zwischen 1 und 150 liegen.");
        if (settings.UpdateParallelism is < 1 or > 100) throw new JsonException("Update parallel muss zwischen 1 und 100 liegen.");
        if (settings.ExecutionMode is not ("Local" or "LocalAndRemote" or "Remote")) throw new JsonException("Ungültiger Ausführungsmodus in config.json.");
        try { settings.UseBuild = BuildChannels.Normalize(settings.UseBuild); }
        catch (ArgumentException ex) { throw new JsonException("Ungültige use Build-Einstellung in config.json: ALPHA, BETA oder STABLE erwartet.", ex); }
        if (settings.VisibleHostColumns is not null) settings.VisibleHostColumns = HostColumnSettings.Normalize(settings.VisibleHostColumns);
    }

    private static string[] NormalizeHosts(string[]? hosts) => (hosts ?? [])
        .Where(host => !string.IsNullOrWhiteSpace(host))
        .Select(host => host.Trim())
        .Where(host => !host.StartsWith('#') && !host.StartsWith(';') && !host.Any(char.IsWhiteSpace))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string EncryptPassword(string password)
    {
        if (password.Length == 0) return "";
        byte[] plain = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static string DecryptPassword(string encrypted, string? protectionScope)
    {
        if (encrypted.Length == 0) return "";
        // Configs saved before the scope field existed used CurrentUser.
        var scope = protectionScope switch
        {
            null or "" => DataProtectionScope.CurrentUser,
            nameof(DataProtectionScope.CurrentUser) => DataProtectionScope.CurrentUser,
            nameof(DataProtectionScope.LocalMachine) => DataProtectionScope.LocalMachine,
            _ => throw new CryptographicException("Unbekannter Passwortschutz.")
        };
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, scope);
        try { return new UTF8Encoding(false, true).GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
