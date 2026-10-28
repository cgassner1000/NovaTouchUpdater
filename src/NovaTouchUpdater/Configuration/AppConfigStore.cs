using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaTouchUpdater;

internal sealed class UpdaterConfig
{
    [JsonPropertyOrder(int.MaxValue)]
    public string[]? Hosts { get; set; } = [];
    public string? RemotePath { get; set; }
    public string? Username { get; set; }
    public bool UseSsl { get; set; }
    public bool ScanOnStartup { get; set; } = true;
    public int HttpsPort { get; set; } = 5986;
    public string ExecutionMode { get; set; } = "Remote";
    public string? SoftwarePath { get; set; }
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
                    "Die übrigen Einstellungen wurden geladen. Bitte das Passwort neu eingeben und die Config erneut speichern. " +
                    "Eine ältere, benutzergebundene Config kann alternativ vom ursprünglichen Windows-Benutzer geöffnet und erneut gespeichert werden.";
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
            ExecutionMode = settings.ExecutionMode, SoftwarePath = settings.SoftwarePath,
            ScanOnStartup = settings.ScanOnStartup,
            PasswordProtectionScope = nameof(DataProtectionScope.LocalMachine),
            EncryptedPassword = EncryptPassword(password)
        };
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
            // Replace only the complete, encrypted file; preserve an existing
            // config if encryption, writing or the final replacement fails.
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
        if (settings.HttpsPort is < 1 or > 65535) throw new JsonException("HttpsPort muss zwischen 1 und 65535 liegen.");
        if (settings.ExecutionMode is not ("Local" or "LocalAndRemote" or "Remote")) throw new JsonException("Ungültiger Ausführungsmodus in config.json.");
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
