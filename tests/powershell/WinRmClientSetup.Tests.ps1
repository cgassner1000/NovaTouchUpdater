. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
# Every service, listener, firewall and certificate-store mutation is mocked.
# The real certificate/key files and Windows certificate stores are never used.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
public sealed class SetupTestCertificate : IDisposable {
    public static bool Invalid;
    public string Thumbprint { get; private set; }
    public bool HasPrivateKey { get; private set; }
    public DateTime NotAfter { get { return DateTime.Now.AddDays(10); } }
    public SetupTestCertificate(byte[] data) { Thumbprint = "TEST-CA"; }
    public SetupTestCertificate(byte[] data, string password, X509KeyStorageFlags flags) { Thumbprint = "TEST-CLIENT"; HasPrivateKey = !Invalid; }
    public string GetNameInfo(X509NameType type, bool issuer) { return "TEST-CN"; }
    public void Dispose() { }
}
public sealed class SetupTestCertificates : List<SetupTestCertificate> {
    public SetupTestCertificate[] Find(string kind, string thumbprint, bool valid) { return this.Where(c => c.Thumbprint == thumbprint).ToArray(); }
}
public sealed class SetupTestKeyName { public string UniqueName { get { return "SYNTHETIC-KEY"; } } }
public sealed class SetupTestPrivateKey : IDisposable {
    public SetupTestKeyName Key { get { return new SetupTestKeyName(); } }
    public void Dispose() { }
}
public sealed class SetupTestStore {
    public static Dictionary<string, SetupTestCertificates> Data = new Dictionary<string, SetupTestCertificates>();
    public static List<string> Writes = new List<string>();
    private readonly string name;
    public SetupTestCertificates Certificates { get { return Data[name]; } }
    public SetupTestStore(string name, string location) {
        if (location != "LocalMachine" || (name != "My" && name != "Root")) throw new Exception("Unexpected store");
        this.name = name;
        if (!Data.ContainsKey(name)) Data[name] = new SetupTestCertificates();
    }
    public void Open(string mode) { if (mode != "ReadWrite") throw new Exception("Unexpected store mode"); }
    public void Add(SetupTestCertificate certificate) { Certificates.Add(certificate); Writes.Add(name); }
    public void Close() { }
}
'@
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
function Set-Service {
    param($Name, $StartupType)
    Assert ($Name -eq 'WinRM' -and $StartupType -eq 'Automatic') 'Only WinRM may be configured'
    $script:calls.Add('service')
}
function Start-Service { param($Name); Assert ($Name -eq 'WinRM') 'Only WinRM may be started'; $script:calls.Add('start') }
function Get-ChildItem {
    param($Path)
    Assert ($Path -eq 'WSMan:\localhost\Listener') 'Unexpected provider read'
    $script:listeners.ToArray()
}
function Enable-PSRemoting {
    param([switch]$SkipNetworkProfileCheck, [switch]$Force)
    Assert ($SkipNetworkProfileCheck -and $Force) 'Client endpoint registration must be noninteractive'
    $script:calls.Add('endpoints')
    foreach ($rule in $script:builtInRules) { $rule.Enabled = 'True' }
    if (-not @($script:listeners | Where-Object { $_.Keys -contains 'Transport=HTTP' }).Count -and $script:scenario -ne 'no-auto-http') {
        $script:listeners.Add([pscustomobject]@{ Keys = @('Address=*','Transport=HTTP') })
    }
}
function Set-WSManInstance {
    param($ResourceURI, $SelectorSet, $ValueSet)
    Assert ($ResourceURI -eq 'winrm/config/Listener' -and $SelectorSet.Address -eq '*' -and $SelectorSet.Transport -eq $script:transport) 'Only the selected listener may be updated'
    Assert ($ValueSet.Port -eq $script:expectedPort -and $ValueSet.Enabled -is [string] -and [Xml.XmlConvert]::ToBoolean($ValueSet.Enabled)) 'Listener Enabled must be a valid lowercase XML boolean, not .NET True'
    if ($script:transport -eq 'HTTPS') { Assert ($ValueSet.CertificateThumbprint -eq 'TEST-CLIENT' -and $ValueSet.Hostname -eq 'TEST-CN') 'WinRM listener hostname must match the certificate CN; aliases remain in the certificate SAN' }
    $script:calls.Add('set-listener')
}
function New-WSManInstance {
    param($ResourceURI, $SelectorSet, $ValueSet)
    Set-WSManInstance $ResourceURI $SelectorSet $ValueSet
    $script:listeners.Add([pscustomobject]@{ Keys = @('Address=*', ('Transport=' + $SelectorSet.Transport)) })
    $script:calls.Add('new-listener')
}
function Remove-WSManInstance {
    param($ResourceURI, $SelectorSet)
    Assert ($script:transport -eq 'HTTPS' -and -not $script:hadHttp -and $SelectorSet.Transport -eq 'HTTP' -and $SelectorSet.Address -eq '*') 'Only a newly created temporary HTTP listener may be removed'
    $http = @($script:listeners | Where-Object { $_.Keys -contains 'Transport=HTTP' })
    foreach ($listener in $http) { $null = $script:listeners.Remove($listener) }
    $script:calls.Add('remove-temporary-http')
}
function Get-NetFirewallRule {
    param($Name, $ErrorAction)
    if ($Name -eq 'WINRM-HTTP-In-TCP*') { return $script:builtInRules }
    $baseRuleName = 'NovaTouchUpdater-WinRM-HTTPS-' + $script:expectedPort
    Assert ($Name -eq $baseRuleName -or $Name -eq ($baseRuleName + '-Public')) 'Only dedicated application firewall rules may be inspected'
    if ($script:scenario -eq 'existing-listener') { [pscustomobject]@{ Name = $Name } }
}
function Disable-NetFirewallRule {
    param([Parameter(ValueFromPipeline)]$InputObject)
    process {
        Assert ($InputObject.Name -eq 'WINRM-HTTP-In-TCP-PUBLIC') 'A previously enabled built-in firewall rule must remain enabled'
        $InputObject.Enabled = 'False'
        $script:calls.Add('restore-firewall')
    }
}
function Remove-NetFirewallRule {
    param($Name)
    $baseRuleName = 'NovaTouchUpdater-WinRM-HTTPS-' + $script:expectedPort
    Assert ($Name -eq $baseRuleName -or $Name -eq ($baseRuleName + '-Public')) 'Unrelated firewall rules must not be removed'
    $script:calls.Add('replace-rule')
}
function New-NetFirewallRule {
    param($Name, $DisplayName, $Direction, $Action, $Protocol, $LocalPort, $Profile, $RemoteAddress)
    Assert ($LocalPort -eq $script:expectedPort -and $Direction -eq 'Inbound' -and $Action -eq 'Allow' -and $Protocol -eq 'TCP') 'Incorrect firewall protocol or port'
    if ($Name.EndsWith('-Public')) { Assert ($Profile -eq 'Public' -and $RemoteAddress -eq 'LocalSubnet') 'Public HTTPS access must remain restricted to the local subnet' }
    else { Assert (($Profile -join ',') -eq 'Domain,Private' -and $RemoteAddress -eq 'Any') 'Domain/private HTTPS must allow routed and NAT connections using Windows standard scope' }
    $script:calls.Add('firewall')
}
function Test-WSMan {
    param($ComputerName, [switch]$UseSSL, $Port, $ErrorAction)
    Assert ($ComputerName -eq 'localhost' -and $Port -eq $script:expectedPort -and $UseSSL.IsPresent -eq ($script:transport -eq 'HTTPS')) 'Final local test must use the selected protocol and port'
    $script:calls.Add('test')
}
function Invoke-TestCertutil {
    param($File)
    Assert ([IO.File]::ReadAllBytes($File)[0] -eq 3) 'Only the public signed CA revocation list may be imported'
    $script:crlFile = $File
    $global:LASTEXITCODE = 0
    $script:calls.Add('crl')
}
function Get-Acl {
    param($LiteralPath)
    Assert ($LiteralPath -eq (Join-Path $env:ProgramData 'Microsoft\Crypto\Keys\SYNTHETIC-KEY')) 'Only this client certificate private key may be inspected'
    $acl = [pscustomobject]@{ Synthetic = $true }
    $acl | Add-Member ScriptMethod AddAccessRule {
        param($rule)
        Assert ($rule.IdentityReference.Value -eq 'S-1-5-20' -and ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -ne 0 -and $rule.AccessControlType -eq 'Allow') 'Only Network Service read access may be added'
    }
    $acl
}
function Set-Acl {
    param($LiteralPath, $AclObject)
    Assert ($LiteralPath -eq (Join-Path $env:ProgramData 'Microsoft\Crypto\Keys\SYNTHETIC-KEY') -and $AclObject.Synthetic) 'Only the client key ACL may be changed'
    $script:calls.Add('key-access')
}
foreach ($transport in @('HTTP','HTTPS')) {
    $original = Get-Content -LiteralPath (Join-Path $TestScriptsRoot ('WinRmClient' + $(if ($transport -eq 'HTTP') { 'Http' } else { 'Https' }) + '.ps1')) -Raw -Encoding UTF8
    if ($transport -eq 'HTTP') {
        $original = (($original -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }) -join ' ')
        $parseTokens = $null; $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseInput($original, [ref]$parseTokens, [ref]$parseErrors)
        Assert ($parseErrors.Count -eq 0) 'The readable HTTP one-liner must parse in Windows PowerShell 5.1'
        Assert ($original -eq 'Enable-PSRemoting -SkipNetworkProfileCheck -Force') 'HTTP must remain the single standard setup command'
        $script:calls = [Collections.Generic.List[string]]::new()
        $script:listeners = [Collections.Generic.List[object]]::new()
        $script:builtInRules = @([pscustomobject]@{ Name = 'WINRM-HTTP-In-TCP'; Enabled = 'False' })
        $script:scenario = 'simple-http'
        [SetupTestStore]::Writes.Clear()
        & ([scriptblock]::Create($original))
        Assert ($script:calls.Count -eq 1 -and $script:calls[0] -eq 'endpoints' -and [SetupTestStore]::Writes.Count -eq 0) 'HTTP delegates service, endpoints and firewall setup to Windows; no certificate or custom setup operations'
        Write-Output 'PASS simple HTTP setup (all system mutations mocked)'
        continue
    }
    $adminCheck = 'if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {'
    Assert ($original.Contains($adminCheck) -and $original -notmatch '\[Net.Dns\]|GetAllNetworkInterfaces') 'HTTPS must require administration without blocking aliases or NAT addresses through local name lookup'
    foreach ($scenario in @('new-listener','existing-listener','no-auto-http','not-admin','address-alias','invalid-certificate')) {
        if ($transport -eq 'HTTP' -and $scenario -eq 'invalid-certificate') { continue }
        $script:scenario = $scenario; $script:transport = $transport
        $script:expectedPort = $(if ($transport -eq 'HTTP') { 5985 } else { 443 })
        $script:hadHttp = $scenario -eq 'existing-listener'
        $script:calls = [Collections.Generic.List[string]]::new()
        $script:listeners = [Collections.Generic.List[object]]::new()
        if ($script:hadHttp) {
            $script:listeners.Add([pscustomobject]@{ Keys = @('Address=*','Transport=HTTP') })
            if ($transport -eq 'HTTPS') { $script:listeners.Add([pscustomobject]@{ Keys = @('Address=*','Transport=HTTPS') }) }
        }
        $script:builtInRules = @([pscustomobject]@{ Name = 'WINRM-HTTP-In-TCP'; Enabled = 'True' }, [pscustomobject]@{ Name = 'WINRM-HTTP-In-TCP-PUBLIC'; Enabled = 'False' })
        [SetupTestStore]::Data.Clear(); [SetupTestStore]::Writes.Clear(); [SetupTestCertificate]::Invalid = $scenario -eq 'invalid-certificate'
        $source = $original.Replace($adminCheck, $(if ($scenario -eq 'not-admin') { 'if ($true) {' } else { 'if ($false) {' }))
        $source = $source.Replace('[Security.Cryptography.X509Certificates.X509Certificate2]', '[SetupTestCertificate]').Replace('[Security.Cryptography.X509Certificates.X509Store]', '[SetupTestStore]')
        $source = $source.Replace('& "$env:SystemRoot\System32\certutil.exe" -f -addstore CA $crlFile', 'Invoke-TestCertutil $crlFile')
        $source = $source.Replace('[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)', '[SetupTestPrivateKey]::new()').Replace('[Security.Cryptography.RSACng]', '[SetupTestPrivateKey]')
        Assert (-not $source.Contains('certutil.exe')) 'The real certificate utility must be replaced at the test boundary'
        $ExpectedHost = $(if ($scenario -eq 'address-alias') { '203.0.113.254' } else { $env:COMPUTERNAME })
        $Port = $script:expectedPort
        $CertificateData = 'AQ=='; $CertificatePassword = 'synthetic'; $IssuerData = 'Ag=='; $ExpectedThumbprint = 'TEST-CLIENT'; $RevocationData = 'Aw=='
        $failure = $null
        try { & ([scriptblock]::Create($source)) 6>$null }
        catch { $failure = $_ }
        if ($scenario -in @('not-admin','invalid-certificate')) {
            Assert ($null -ne $failure -and $script:calls.Count -eq 0 -and [SetupTestStore]::Writes.Count -eq 0) "Guards must fail before changing the client ($transport/$scenario)"
        }
        else {
            if ($failure) { throw $failure }
            Assert (@($script:calls | Where-Object { $_ -eq 'firewall' }).Count -eq 2 -and $script:calls[$script:calls.Count - 1] -eq 'test') "Both network-profile firewall rules must be configured before the final test ($transport/$scenario)"
            Assert ($script:builtInRules[0].Enabled -eq 'True' -and $script:builtInRules[1].Enabled -eq 'False') 'Endpoint registration must retain previous built-in firewall enablement'
            if ($transport -eq 'HTTPS') {
                Assert ([SetupTestStore]::Writes.Contains('Root') -and [SetupTestStore]::Writes.Contains('My')) 'HTTPS must install public CA and private client certificate in machine stores'
                Assert ($script:calls.Contains('crl') -and -not [IO.File]::Exists($script:crlFile)) 'HTTPS must import the public signed CRL and remove its temporary file'
                Assert ($script:calls.Contains('key-access')) 'HTTPS must grant WinRM read access to the client private key'
                Assert (@($script:listeners | Where-Object { $_.Keys -contains 'Transport=HTTP' }).Count -eq $(if ($script:hadHttp) { 1 } else { 0 })) 'HTTPS preserves pre-existing HTTP and removes only its temporary HTTP listener'
            }
            else { Assert ([SetupTestStore]::Writes.Count -eq 0) 'HTTP must not access certificate stores' }
        }
        Write-Output "PASS client setup $transport / $scenario (all system mutations mocked)"
    }
}
