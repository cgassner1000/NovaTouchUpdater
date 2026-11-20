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
function New-TestListener($transport, $port, $address = '*') {
    [pscustomobject]@{ Keys = @("Address=$address","Transport=$transport"); Address=$address; Transport=$transport; Port=$port; Enabled='true' }
}
function Get-WSManInstance {
    param($ResourceURI,[switch]$Enumerate)
    if ($ResourceURI -eq 'winrm/config/service') {
        return ([xml]"<Service><EnableCompatibilityHttpListener Source='GPO'>$($script:compat.EnableCompatibilityHttpListener)</EnableCompatibilityHttpListener><EnableCompatibilityHttpsListener>$($script:compat.EnableCompatibilityHttpsListener)</EnableCompatibilityHttpsListener></Service>").DocumentElement
    }
    Assert ($ResourceURI -eq 'winrm/config/Listener' -and $Enumerate) 'Only enumerate the local WinRM listener configuration'
    foreach ($item in $script:listeners) {
        ([xml]"<Listener xmlns='http://schemas.microsoft.com/wbem/wsman/1/config/listener'><Address>$($item.Address)</Address><Transport>$($item.Transport)</Transport><Port Source='GPO'>$($item.Port)</Port><Enabled Source='GPO'>$($item.Enabled)</Enabled></Listener>").DocumentElement
    }
}
function Assert-SetupExclusive {
    $others = @($script:listeners | Where-Object { $_.Enabled -eq 'true' -and ($_.Transport -ne $script:transport -or $_.Port -ne $script:expectedPort) })
    Assert ($others.Count -eq 0 -and $script:compat.Values -notcontains 'true') 'Disable conflicting listeners and compatibility ports before configuring the endpoint, selected listener, firewall or probe'
}
function Invoke-TestEndpointRegistration {
    Assert-SetupExclusive
    if ($script:scenario -in @('endpoint-restart','endpoint-failure')) {
        ($script:listeners | Where-Object Transport -eq $other)[0].Enabled = 'true'
        $script:rules["WINRM-$other-In-TCP"].Enabled = 'True'
        if ($script:scenario -eq 'endpoint-failure') { throw 'Simulated PowerShell endpoint registration failure' }
    }
}
function Enable-PSRemoting {
    param([switch]$SkipNetworkProfileCheck, [switch]$Force)
    Assert ($script:transport -eq 'HTTP') 'HTTPS must never run HTTP quick configuration, even temporarily'
    Assert ($SkipNetworkProfileCheck -and $Force) 'Client endpoint registration must be noninteractive'
    $script:calls.Add('endpoints')
    Invoke-TestEndpointRegistration
    $script:rules['WINRM-HTTP-In-TCP'].Enabled = 'True'
    if (-not @($script:listeners | Where-Object { $_.Keys -contains 'Transport=HTTP' }).Count) {
        $script:listeners.Add((New-TestListener HTTP 5985))
    }
}
function Set-WSManQuickConfig { throw 'Unexpected transport quick configuration' }
function Enable-PSSessionConfiguration { throw 'This command can also enable the HTTP listener' }
function Test-Path {
    param($LiteralPath)
    Assert ($LiteralPath -eq 'WSMan:\localhost\Plugin\Microsoft.PowerShell') 'Only inspect the Windows PowerShell endpoint'
    return $script:scenario -eq 'existing-listener'
}
function Set-PSSessionConfiguration {
    param($Name, $AccessMode, [switch]$Force)
    Assert ($Name -eq 'Microsoft.PowerShell' -and $AccessMode -eq 'Remote' -and $Force -and $script:scenario -eq 'existing-listener') 'Configure the existing default session endpoint'
    $script:calls.Add('configure-endpoint')
    Invoke-TestEndpointRegistration
}
function Register-PSSessionConfiguration {
    param($Name, $AccessMode, [switch]$Force)
    Assert ($Name -eq 'Microsoft.PowerShell' -and $AccessMode -eq 'Remote' -and $Force -and $script:scenario -ne 'existing-listener') 'Register the missing default session endpoint'
    $script:calls.Add('register-endpoint')
    Invoke-TestEndpointRegistration
}
function Set-WSManInstance {
    param($ResourceURI, $SelectorSet, $ValueSet)
    if ($ResourceURI -eq 'winrm/config/service') {
        $key = @($ValueSet.Keys)[0]
        Assert ($key -in @('EnableCompatibilityHttpListener','EnableCompatibilityHttpsListener') -and $ValueSet[$key] -ceq 'false') 'Only disable compatibility listeners'
        $script:compat[$key] = 'false'
        return
    }
    $item = $script:listeners | Where-Object { $_.Transport -eq $SelectorSet.Transport -and $_.Address -eq $SelectorSet.Address }
    if ($ValueSet.Enabled -ceq 'false') {
        Assert ($item -and ($item.Port -ne $script:expectedPort -or $item.Transport -ne $script:transport)) 'Disable only conflicting listeners'
        if ($script:scenario -ne 'enforced-policy') { $item.Enabled = 'false' }
        return
    }
    Assert-SetupExclusive
    Assert ($ResourceURI -eq 'winrm/config/Listener' -and $SelectorSet.Address -eq '*' -and $SelectorSet.Transport -eq $script:transport) 'Only the selected listener may be updated'
    Assert ($ValueSet.Port -eq $script:expectedPort -and $ValueSet.Enabled -is [string] -and [Xml.XmlConvert]::ToBoolean($ValueSet.Enabled)) 'Listener Enabled must be a valid lowercase XML boolean, not .NET True'
    if ($script:transport -eq 'HTTPS') { Assert ($ValueSet.CertificateThumbprint -eq 'TEST-CLIENT' -and $ValueSet.Hostname -eq 'TEST-CN') 'WinRM listener hostname must match the certificate CN; aliases remain in the certificate SAN' }
    $item.Port = $ValueSet.Port
    $item.Enabled = $ValueSet.Enabled
    $script:calls.Add('set-listener')
}
function New-WSManInstance {
    param($ResourceURI, $SelectorSet, $ValueSet)
    $script:listeners.Add((New-TestListener $SelectorSet.Transport $ValueSet.Port))
    Set-WSManInstance $ResourceURI $SelectorSet $ValueSet
    $script:calls.Add('new-listener')
}
function Remove-WSManInstance { throw 'The setup must not delete existing listeners' }
function Get-NetFirewallRule {
    param($Name, $ErrorAction, $PolicyStore)
    if ($PolicyStore -eq 'ActiveStore' -and $script:scenario -eq 'firewall-policy') { return [pscustomobject]@{ Name=$Name; Enabled='False' } }
    foreach ($pattern in $Name) {
        foreach ($rule in $script:rules.Values) { if ($rule.Name -like $pattern) { $rule } }
    }
}
function Disable-NetFirewallRule {
    param([Parameter(ValueFromPipeline)]$InputObject)
    process {
        Assert ($InputObject.Name -ne 'AnotherService' -and $InputObject.Name -notin @($ruleBase, "$ruleBase-Public")) 'Protect unrelated rules and the selected transport'
        $InputObject.Enabled = 'False'
    }
}
function Write-TestRule($Name,$DisplayName,$Direction,$Action,$Protocol,$LocalPort,$Profile,$RemoteAddress,$Enabled) {
    Assert-SetupExclusive
    Assert ($DisplayName.Contains("TCP $LocalPort") -and $DisplayName.Contains($script:transport) -and $Enabled -eq 'True') 'Selected protocol, port and enabled state must be explicit'
    Assert ($LocalPort -eq $script:expectedPort -and $Direction -eq 'Inbound' -and $Action -eq 'Allow' -and $Protocol -eq 'TCP') 'Incorrect firewall protocol or port'
    if ($Name.EndsWith('-Public')) { Assert ($Profile -eq 'Public' -and $RemoteAddress -eq 'LocalSubnet') 'Public HTTPS access must remain restricted to the local subnet' }
    else { Assert (($Profile -join ',') -eq 'Domain,Private' -and $RemoteAddress -eq 'Any') 'Domain/private HTTPS must allow routed and NAT connections using Windows standard scope' }
    $script:calls.Add('firewall')
    $script:rules[$Name] = [pscustomobject]@{ Name=$Name; Enabled=$Enabled; Direction=$Direction; Action=$Action }
}
function New-NetFirewallRule {
    param($Name,$DisplayName,$Direction,$Action,$Protocol,$LocalPort,$Profile,$RemoteAddress,$Enabled)
    Assert (!$script:rules.ContainsKey($Name)) 'Do not duplicate existing rules'
    Write-TestRule @PSBoundParameters
}
function Set-NetFirewallRule {
    param($Name,$NewDisplayName,$Direction,$Action,$Protocol,$LocalPort,$Profile,$RemoteAddress,$Enabled)
    Assert ($script:rules.ContainsKey($Name)) 'Only update existing rules'
    Write-TestRule $Name $NewDisplayName $Direction $Action $Protocol $LocalPort $Profile $RemoteAddress $Enabled
}
function Test-WSMan {
    param($ComputerName, [switch]$UseSSL, $Port, $ErrorAction)
    Assert-SetupExclusive
    Assert ($ComputerName -eq 'localhost' -and $Port -eq $script:expectedPort -and $UseSSL.IsPresent -eq ($script:transport -eq 'HTTPS')) 'Final local test must use the selected protocol and port'
    $script:calls.Add('test')
    if ($script:scenario -eq 'probe-failure') { throw 'Simulated connection failure' }
    if ($script:scenario -eq 'late-listener-change') {
        ($script:listeners | Where-Object Transport -eq $other)[0].Enabled = 'true'
        ($script:listeners | Where-Object { $_.Transport -eq $script:transport -and $_.Address -eq '*' }).Enabled = 'false'
    }
}
function Invoke-TestCertutil {
    param($File)
    Assert ([IO.File]::ReadAllBytes($File)[0] -eq 3) 'Only the public signed CA revocation list may be imported'
    $script:crlFile = $File
    $global:LASTEXITCODE = if ($script:scenario -eq 'certificate-install-failure') { 1 } else { 0 }
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
    $original += [Environment]::NewLine + (Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'WinRmClientTransport.ps1') -Raw -Encoding UTF8)
    if ($transport -eq 'HTTP') {
        $original = (($original -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }) -join ' ')
        $parseTokens = $null; $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseInput($original, [ref]$parseTokens, [ref]$parseErrors)
        Assert ($parseErrors.Count -eq 0) 'The readable HTTP one-liner must parse in Windows PowerShell 5.1'
    }
    $adminCheck = 'if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {'
    if ($transport -eq 'HTTPS') { Assert ($original.Contains($adminCheck) -and $original -notmatch '\[Net.Dns\]|GetAllNetworkInterfaces') 'HTTPS must require administration without blocking aliases or NAT addresses through local name lookup' }
    foreach ($scenario in @('new-listener','existing-listener','default-port','not-admin','address-alias','invalid-certificate','certificate-install-failure','firewall-policy','probe-failure','endpoint-restart','endpoint-failure','late-listener-change','enforced-policy','port-collision')) {
        if ($transport -eq 'HTTP' -and $scenario -in @('not-admin','invalid-certificate','certificate-install-failure','address-alias','default-port')) { continue }
        $script:scenario = $scenario; $script:transport = $transport
        $script:expectedPort = $(if ($transport -eq 'HTTP') { 5985 } elseif ($scenario -in @('default-port','probe-failure','existing-listener')) { 5986 } else { 443 })
        $other = if ($transport -eq 'HTTP') { 'HTTPS' } else { 'HTTP' }
        $script:calls = [Collections.Generic.List[string]]::new()
        $script:listeners = [Collections.Generic.List[object]]::new()
        if ($scenario -ne 'new-listener') {
            $previousPort = if ($scenario -eq 'port-collision') { $script:expectedPort } elseif ($other -eq 'HTTP') { 5985 } else { 5986 }
            $script:listeners.Add((New-TestListener $other $previousPort))
            $script:listeners.Add((New-TestListener $other 8444 'IP:192.0.2.3'))
            $script:listeners.Add((New-TestListener $transport 9999 'IP:192.0.2.4'))
        }
        if ($scenario -in @('existing-listener','probe-failure')) { $script:listeners.Add((New-TestListener $transport $script:expectedPort)) }
        $script:compat = @{ EnableCompatibilityHttpListener='true'; EnableCompatibilityHttpsListener='true' }
        $script:rules = @{}
        $ruleBase = "NovaTouchUpdater-WinRM-$transport-$script:expectedPort"
        $ruleNames = @('AnotherService','WINRM-HTTP-In-TCP','WINRM-HTTPS-In-TCP','NovaTouchUpdater-WinRM-HTTPS-8443')
        if ($scenario -eq 'existing-listener') { $ruleNames += $ruleBase }
        foreach ($name in $ruleNames) { $script:rules[$name] = [pscustomobject]@{ Name=$name; Enabled='True'; Direction='Inbound'; Action='Allow' } }
        [SetupTestStore]::Data.Clear(); [SetupTestStore]::Writes.Clear(); [SetupTestCertificate]::Invalid = $scenario -eq 'invalid-certificate'
        $source = $original.Replace($adminCheck, $(if ($scenario -eq 'not-admin') { 'if ($true) {' } else { 'if ($false) {' }))
        $source = $source.Replace('[Security.Cryptography.X509Certificates.X509Certificate2]', '[SetupTestCertificate]').Replace('[Security.Cryptography.X509Certificates.X509Store]', '[SetupTestStore]')
        $source = $source.Replace('& "$env:SystemRoot\System32\certutil.exe" -f -addstore CA $crlFile', 'Invoke-TestCertutil $crlFile')
        $source = $source.Replace('[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)', '[SetupTestPrivateKey]::new()').Replace('[Security.Cryptography.RSACng]', '[SetupTestPrivateKey]')
        Assert (-not $source.Contains('certutil.exe')) 'The real certificate utility must be replaced at the test boundary'
        $source = $source.Replace('[Console]::ReadLine()', '$script:setupInput')
        $expectedHost = if ($scenario -eq 'address-alias') { '203.0.113.254' } else { $env:COMPUTERNAME }
        $script:setupInput = @{ ExpectedHost=$expectedHost; Port=$script:expectedPort; CertificateData='AQ=='; CertificatePassword='synthetic'; IssuerData='Ag=='; ExpectedThumbprint='TEST-CLIENT'; RevocationData='Aw==' } | ConvertTo-Json -Compress
        $failure = $null
        try { & ([scriptblock]::Create($source)) 6>$null }
        catch { $failure = $_ }
        if ($scenario -in @('not-admin','invalid-certificate')) {
            Assert ($null -ne $failure -and $script:calls.Count -eq 0 -and [SetupTestStore]::Writes.Count -eq 0) "Guards must fail before changing the client ($transport/$scenario)"
        }
        elseif ($scenario -in @('probe-failure','endpoint-failure','late-listener-change','certificate-install-failure')) {
            $expectedMessage = @{ 'probe-failure'='Simulated connection failure'; 'endpoint-failure'='Simulated PowerShell endpoint'; 'late-listener-change'='ist nicht aktiv'; 'certificate-install-failure'='Sperrliste' }[$scenario]
            Assert ($failure -and $failure.ToString().Contains($expectedMessage) -and $script:rules["WINRM-$other-In-TCP"].Enabled -eq 'False') "Failure must be reported and the opposite firewall rule must remain disabled ($scenario)"
            Assert-SetupExclusive
        }
        elseif ($scenario -eq 'firewall-policy') {
            Assert ($failure -and $failure.ToString().Contains('Firewallregel') -and !$script:calls.Contains('test') -and $script:rules["WINRM-$other-In-TCP"].Enabled -eq 'False') 'An inactive selected firewall rule must not restore the opposite protocol'
            Assert-SetupExclusive
        }
        elseif ($scenario -eq 'enforced-policy') {
            Assert ($failure -and $failure.ToString().Contains("WinRM-Listener $other/") -and !$script:calls.Contains('new-listener') -and !$script:calls.Contains('endpoints') -and !$script:calls.Contains('register-endpoint') -and !$script:calls.Contains('firewall')) 'A policy-enforced conflicting listener must abort before enabling the selected protocol'
        }
        else {
            if ($failure) { throw $failure }
            Assert (@($script:calls | Where-Object { $_ -eq 'firewall' }).Count -eq 2 -and $script:calls[$script:calls.Count - 1] -eq 'test') "Both network-profile firewall rules must be configured before the final test ($transport/$scenario)"
            $active = @($script:listeners | Where-Object Enabled -eq 'true')
            Assert ($active.Count -eq 1 -and $active[0].Transport -eq $transport -and $active[0].Port -eq $script:expectedPort) 'Exactly the selected protocol and port may listen'
            Assert ($script:compat.Values -notcontains 'true') 'Both legacy compatibility listeners are disabled'
            Assert ($script:rules[$ruleBase].Enabled -eq 'True' -and $script:rules["$ruleBase-Public"].Enabled -eq 'True' -and $script:rules["WINRM-$other-In-TCP"].Enabled -eq 'False' -and $script:rules['NovaTouchUpdater-WinRM-HTTPS-8443'].Enabled -eq 'False' -and $script:rules['AnotherService'].Enabled -eq 'True') 'Explicit firewall rules protect the selected port and only obsolete WinRM rules are disabled'
            if ($transport -eq 'HTTPS') {
                Assert ([SetupTestStore]::Writes.Contains('Root') -and [SetupTestStore]::Writes.Contains('My')) 'HTTPS must install public CA and private client certificate in machine stores'
                Assert ($script:calls.Contains('crl') -and -not [IO.File]::Exists($script:crlFile)) 'HTTPS must import the public signed CRL and remove its temporary file'
                Assert ($script:calls.Contains('key-access')) 'HTTPS must grant WinRM read access to the client private key'
                $endpointCall = if ($scenario -eq 'existing-listener') { 'configure-endpoint' } else { 'register-endpoint' }
                Assert ($script:calls.Contains($endpointCall)) 'HTTPS prepares the PowerShell endpoint independently of transport'
            }
            else { Assert ([SetupTestStore]::Writes.Count -eq 0) 'HTTP must not access certificate stores' }
        }
        Write-Output "PASS client setup $transport / $scenario (all system mutations mocked)"
    }
}
