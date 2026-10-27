using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaTouchUpdater;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

static void Rejects(Action action, string message)
{
    bool rejected=false;
    try { action(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { rejected=true; }
    Assert(rejected, message);
}

string workspace=TestWorkspace.Root;
Assert(File.Exists(Path.Combine(workspace,"NovaTouchUpdater.slnx")), "tests run from the project workspace");
if (args.Length==2 && args[0] is "--create-shared-probe" or "--read-shared-probe")
{
    // A synthetic machine-protected config can be written and read by two
    // separate Windows execution identities. Never accept real config paths.
    string probe=Path.GetFullPath(args[1]);
    string probeDirectory=TestWorkspace.Artifacts;
    Assert(Path.GetDirectoryName(probe)!.Equals(probeDirectory,StringComparison.OrdinalIgnoreCase) &&
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(probe),"^cfg-share-[a-f0-9]{8}\\.json$"), "shared probe stays in the workspace test directory");
    string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value)));
    if (args[0]=="--create-shared-probe")
    {
        Directory.CreateDirectory(probeDirectory);
        AppConfigStore.Save(probe,new UpdaterConfig { Hosts=["SHARED-TEST"], Username="test-only-user", UseSsl=true, HttpsPort=443 },"TestOnlySharedPassword");
        File.WriteAllText(probe+".identity",identity);
        Console.WriteLine("PASS shared machine-protected probe created with synthetic credentials");
    }
    else
    {
        try
        {
            var shared=AppConfigStore.Load(probe);
            Assert(shared.Password=="TestOnlySharedPassword" && shared.PasswordWarning is null && shared.Settings.PasswordProtectionScope=="LocalMachine",
                "another Windows execution context can decrypt the saved machine-protected password");
            Console.WriteLine("Different Windows identity: " + (File.ReadAllText(probe+".identity")!=identity));
        }
        finally { File.Delete(probe); File.Delete(probe+".identity"); }
    }
    return;
}
string fixture=Path.GetFullPath(Path.Combine(TestWorkspace.Artifacts,"cfg-"+Guid.NewGuid().ToString("N")[..8]));
Assert(fixture.StartsWith(TestWorkspace.Artifacts+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase), "fixture stays within workspace artifacts/test-results");
Directory.CreateDirectory(fixture);
try
{
    const string password="TestOnlySecretMarker_ä\"$[]\\long-password";
    string path=Path.Combine(fixture,"config.json");
    var settings=new UpdaterConfig
    {
        Hosts=[" KASSE-01 ","kasse-01","KASSE-02","# comment",";comment"," ","invalid host","192.0.2.1"],
        RemotePath=@"D:\upload\Novacom", Username=@"DOMAIN\support", UseSsl=true, HttpsPort=443,
        SoftwarePath=@"D:\software packages", ExecutionMode="LocalAndRemote",
        LegacyPassword="A legacy field must never be saved"
    };
    AppConfigStore.Save(path,settings,password);
    string json=File.ReadAllText(path);
    using (var document=JsonDocument.Parse(json))
    {
        Assert(!document.RootElement.TryGetProperty("Password",out _) && !json.Contains("TestOnlySecretMarker") && !json.Contains("legacy field"),
            "saved JSON never includes a plaintext Password field or either plaintext password");
        string encrypted=document.RootElement.GetProperty("EncryptedPassword").GetString()!;
        Assert(document.RootElement.GetProperty("PasswordProtectionScope").GetString()=="LocalMachine","new configs explicitly use machine-wide password protection");
        byte[] decoded=ProtectedData.Unprotect(Convert.FromBase64String(encrypted),null,DataProtectionScope.LocalMachine);
        try { Assert(Encoding.UTF8.GetString(decoded)==password,"the saved value is real Windows LocalMachine DPAPI encryption, including Unicode and punctuation"); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }
    var loaded=AppConfigStore.Load(path);
    Assert(loaded.Settings.SoftwarePath == settings.SoftwarePath && loaded.Settings.ExecutionMode == "LocalAndRemote",
        "software directory and local/remote execution mode survive configuration round trips");
    Assert(loaded.Password==password && loaded.PasswordWarning is null && loaded.Settings.Hosts!.SequenceEqual(new[] {"KASSE-01","KASSE-02","192.0.2.1"}) &&
        loaded.Settings.Username==settings.Username && loaded.Settings.UseSsl && loaded.Settings.HttpsPort==443 && loaded.Settings.RemotePath==settings.RemotePath,
        "hosts, user, password, HTTPS, custom port and client path survive saving and loading");

    string legacy=Path.Combine(fixture,"legacy.json");
    File.WriteAllText(legacy,JsonSerializer.Serialize(new { Hosts=new[] {"OLD-HOST"}, Username="support", Password=password }));
    var old=AppConfigStore.Load(legacy);
    Assert(old.Password==password && old.Settings.HttpsPort==5986 && !old.Settings.UseSsl && old.Settings.ExecutionMode == "Remote","legacy plaintext configs remain readable and missing settings keep remote-mode defaults");
    AppConfigStore.Save(legacy,old.Settings,old.Password);
    using (var migrated=JsonDocument.Parse(File.ReadAllText(legacy)))
        Assert(!migrated.RootElement.TryGetProperty("Password",out _) && migrated.RootElement.TryGetProperty("EncryptedPassword",out _) && AppConfigStore.Load(legacy).Password==password,
            "saving a legacy config migrates it to encrypted-only storage");

    string userBound=Path.Combine(fixture,"user-bound.json");
    byte[] plain=Encoding.UTF8.GetBytes(password);
    string userEncrypted;
    try { userEncrypted=Convert.ToBase64String(ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser)); }
    finally { CryptographicOperations.ZeroMemory(plain); }
    File.WriteAllText(userBound,JsonSerializer.Serialize(new { Hosts=new[] {"LEGACY-USER"}, EncryptedPassword=userEncrypted }));
    var userConfig=AppConfigStore.Load(userBound);
    Assert(userConfig.Password==password && userConfig.PasswordWarning is null,"old DPAPI configs without a scope field remain readable by their original user");
    AppConfigStore.Save(userBound,userConfig.Settings,userConfig.Password);
    var machineConfig=AppConfigStore.Load(userBound);
    Assert(machineConfig.Password==password && machineConfig.Settings.PasswordProtectionScope=="LocalMachine",
        "saving an old CurrentUser config migrates it to shared LocalMachine protection");

    foreach (string invalid in new[] {"invalid base64!",Convert.ToBase64String(new byte[64])})
    {
        string broken=Path.Combine(fixture,"broken.json");
        File.WriteAllText(broken,JsonSerializer.Serialize(new { Hosts=new[] {"KEEP-HOST"}, Username="keep-user", UseSsl=true, HttpsPort=443, PasswordProtectionScope="LocalMachine", EncryptedPassword=invalid, Password="unsafe fallback" }));
        var result=AppConfigStore.Load(broken);
        Assert(result.Password=="" && result.PasswordWarning is not null && result.Settings.Hosts![0]=="KEEP-HOST" && result.Settings.Username=="keep-user" && result.Settings.HttpsPort==443 && result.Settings.UseSsl,
            "invalid or undecryptable passwords keep other settings and never fall back to a stale plaintext value");
    }
    string unknownScope=Path.Combine(fixture,"unknown-scope.json");
    File.WriteAllText(unknownScope,JsonSerializer.Serialize(new { Hosts=new[] {"KEEP-HOST"}, PasswordProtectionScope="Unknown", EncryptedPassword=userEncrypted }));
    Assert(AppConfigStore.Load(unknownScope) is { Password:"", PasswordWarning:not null },"unknown protection formats require password re-entry instead of a silent fallback");

    AppConfigStore.Save(path,loaded.Settings,"");
    var empty=AppConfigStore.Load(path);
    Assert(empty.Password=="" && empty.PasswordWarning is null,"saving an empty password clears a previously encrypted password");
    AppConfigStore.Save(path,new UpdaterConfig { Hosts=[], Username="", UseSsl=false, HttpsPort=443 },"");
    Assert(AppConfigStore.Load(path) is { Password:"", Settings: { Hosts.Length:0, Username:"", UseSsl:false, HttpsPort:443 } },
        "an empty host list and current-identity credentials are saved without stale values");

    AppConfigStore.Save(path,settings,password);
    byte[] original=File.ReadAllBytes(path);
    foreach (int invalid in new[] {0,65536})
    {
        Rejects(()=>AppConfigStore.Save(path,new UpdaterConfig { HttpsPort=invalid },password),"invalid HTTPS ports are rejected before writing");
        Assert(File.ReadAllBytes(path).SequenceEqual(original),"validation errors preserve the existing config");
    }
    using (var locked=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        Rejects(()=>AppConfigStore.Save(path,new UpdaterConfig { Hosts=["replacement"] },password),"a locked config produces a save error");
    Assert(File.ReadAllBytes(path).SequenceEqual(original) && Directory.GetFiles(fixture,"*.tmp").Length==0,
        "failed atomic replacement preserves the original file and removes its temporary encrypted file");
    Rejects(()=>AppConfigStore.Save(Path.Combine(fixture,"missing-directory","config.json"),settings,password),"write errors do not report a successful save");
    string malformed=Path.Combine(fixture,"malformed.json");
    File.WriteAllText(malformed,"{broken json");
    Rejects(()=>AppConfigStore.Load(malformed),"malformed JSON is reported rather than silently discarded");
}
finally
{
    if (!fixture.StartsWith(TestWorkspace.Artifacts+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
        !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(fixture),"^cfg-[a-f0-9]{8}$"))
        throw new InvalidOperationException("Invalid cleanup target");
    Directory.Delete(fixture,recursive:true);
}
