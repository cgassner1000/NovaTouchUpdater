. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
<<<<<<< HEAD
function ExpectFailure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true; Assert (-not $_.Exception.Message.Contains('synthetic-secret')) 'error messages exclude connection credentials' }
    Assert $failed $Message
}
=======
>>>>>>> 6ac4f29e551324452a0ab63e15ea7189e805adec
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'SoftwareDatabase.ps1'))))
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('database-discovery-' + [guid]::NewGuid().ToString('N'))))
Assert ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'discovery fixtures stay in test artifacts'
[void][IO.Directory]::CreateDirectory($fixture)
<<<<<<< HEAD
$script:services = @(); $script:cimError = $false
$previousFixtureVariable = $env:NOVATOUCH_DISCOVERY_FIXTURE
$env:NOVATOUCH_DISCOVERY_FIXTURE = $fixture
function Get-CimInstance {
    param($ClassName,$Filter,$ErrorAction,$ComputerName)
    Assert ($ClassName -eq 'Win32_Service' -and -not $ComputerName -and $Filter.Contains('novacom ntcontrol updateservice') -and $Filter.Contains('novacom pos updateservice')) 'only the two local update services are queried without remote CIM'
    if ($script:cimError) { throw 'Simulated service configuration access denied' }
    $script:services
}
function Get-ItemProperty { throw 'Discovery must not read uninstall or MSI registry entries' }
function New-PSSession { throw 'Discovery must not use WinRM' }
function New-CimSession { throw 'Discovery must not create a remote CIM session' }
function Start-Service { throw 'Discovery must not start services' }
function Stop-Service { throw 'Discovery must not stop services' }
function Start-Process { throw 'Discovery must not run service executables' }
function Service([string]$Name,[string]$Command,[string]$Display='') {
    [pscustomobject]@{ Name=$Name; DisplayName=$Display; PathName=$Command; State='Stopped'; StartMode='Disabled' }
}
function EncryptFixture([string]$Value) {
    $aes = [Security.Cryptography.Aes]::Create(); $encoder = $null
    try {
        $aes.Key = [Text.Encoding]::UTF8.GetBytes('ViBaGeneralRijndaelKeyA!B$%OP98#'); $aes.IV = [Text.Encoding]::UTF8.GetBytes('VectViBaRijndael')
        $encoder = $aes.CreateEncryptor(); $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        [BitConverter]::ToString($encoder.TransformFinalBlock($bytes,0,$bytes.Length)).Replace('-','').ToLowerInvariant()
    } finally { if ($null -ne $encoder) { $encoder.Dispose() }; $aes.Dispose() }
}
try {
    $ntcRoot = Join-Path $fixture 'Custom Control'; $posRoot = Join-Path $fixture 'Custom POS'
    foreach ($root in @($ntcRoot,$posRoot)) { [void][IO.Directory]::CreateDirectory((Join-Path $root 'bin')) }
    [void][IO.Directory]::CreateDirectory((Join-Path $ntcRoot 'Einstellungen'))
    $ntcExe = Join-Path $ntcRoot 'bin\Control.UpdateService.exe'; $posExe = Join-Path $posRoot 'bin\POS.UpdateService.exe'
    [IO.File]::WriteAllText($ntcExe, 'Fixture only, never executed'); [IO.File]::WriteAllText($posExe, 'Fixture only, never executed')
    $xmlPath = Join-Path $ntcRoot 'Einstellungen\einstellung-Cache.xml'; $iniPath = Join-Path $posRoot 'novacom.ini'
    $user = EncryptFixture 'test'; $password = EncryptFixture 'synthetic-secret'
    $xml = '<?xml version="1.0" encoding="Unicode"?><einstellungen><DB><C1><Server>fixture.invalid</Server><DatabasePort>1972</DatabasePort><Database>USER</Database><SQLUser2>' + $user + '</SQLUser2><SQLPasswort2>' + $password + '</SQLPasswort2></C1></DB></einstellungen>'
    [IO.File]::WriteAllText($xmlPath, $xml, [Text.Encoding]::Unicode)
    $ini = "[OTHER]`r`nSERVER=must-be-ignored`r`n[NOVACOM]`r`nSYSTEM=CACHE`r`nSERVER=cn_iptcp:fixture.invalid[1972]:USER`r`nUSER=$user`r`nPW=$password`r`nSSL=0`r`n"
    [IO.File]::WriteAllText($iniPath, $ini)
    $ntc = Service 'novacom ntcontrol updateservice' ('"' + $ntcExe + '" --service --ignored="C:\unrelated\bin"')
    $pos = Service 'novacom pos updateservice' ($posExe + ' --service')

    $script:services = @($ntc)
    $found = FindSoftwareDatabaseConfiguration
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq $xmlPath -and $found.Directories -contains (Join-Path $ntcRoot 'bin')) 'a stopped NTC service resolves settings and provider directory without POS or uninstall registration'
    Assert ($found.Services[0].ExePath -eq $ntcExe -and $found.CheckedPaths -contains $xmlPath) 'quoted EXE paths with arguments retain the exact executable and checked XML path'
    Assert (@(ReadSoftwareConnections $found.Paths).Count -eq 1) 'the actual Unicode NTC XML is read from the service-derived directory'
    $script:services = @($pos)
    $found = FindSoftwareDatabaseConfiguration
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq $iniPath -and -not [IO.Directory]::Exists((Join-Path $posRoot 'Einstellungen'))) 'POS uses novacom.ini above bin without requiring an Einstellungen directory'
    $connection = @(ReadSoftwareConnections $found.Paths)[0]
    Assert ($connection.Server -eq 'fixture.invalid' -and $connection.Port -eq 1972 -and $connection.Namespace -eq 'USER' -and $connection.User -eq 'test' -and $connection.Password -eq 'synthetic-secret' -and -not $connection.Ssl) 'POS SERVER, namespace, encrypted USER/PW and SSL match the original INI contract'
    $script:services = @($ntc,$pos)
    $found = FindSoftwareDatabaseConfiguration
    Assert ($found.Paths.Count -eq 2 -and @(ReadSoftwareConnections $found.Paths).Count -eq 1) 'matching NTC XML and POS INI connections are deduplicated'
    $ntc.PathName = '%NOVATOUCH_DISCOVERY_FIXTURE%\Custom Control\bin\Control.UpdateService.exe --service'
    $script:services = @($ntc)
    Assert ((FindSoftwareDatabaseConfiguration).Paths[0] -eq $xmlPath) 'unquoted service paths with spaces and environment variables resolve correctly'
    $alias = Service 'InternalServiceName' ('"' + $posExe + '"') 'NOVACOM POS UPDATESERVICE'
    $script:services = @($alias)
    Assert ((FindSoftwareDatabaseConfiguration).Paths[0] -eq $iniPath) 'the known display name is also recognized case-insensitively'
    $script:services = @((Service 'unrelated' ('"' + $posExe + '"')))
    Assert ((FindSoftwareDatabaseConfiguration).Paths.Count -eq 0) 'unrelated services do not supply database settings'

    [IO.File]::WriteAllText($iniPath, $ini.Replace('SSL=0','SSL=1'), [Text.Encoding]::Unicode)
    Assert (@(ReadSoftwareConnections @($iniPath))[0].Ssl) 'Unicode POS INI and enabled SSL are supported'
    foreach ($bad in @(
        $ini.Replace('[1972]','[0]'), $ini.Replace('[1972]','[65536]'),
        $ini.Replace('cn_iptcp:fixture.invalid[1972]:USER','invalid'),
        $ini.Replace(('PW=' + $password),'PW='), $ini.Replace('SSL=0','SSL=invalid'),
        ($ini + "SERVER=cn_iptcp:other.invalid[1972]:USER`r`n")
    )) {
        [IO.File]::WriteAllText($iniPath, $bad)
        ExpectFailure { ReadSoftwareConnections @($iniPath) } 'invalid or ambiguous POS connection definitions stop safely'
    }
    [IO.File]::WriteAllText($iniPath, "[NOVACOM]`nSERVER=cn_iptcp:fixture.invalid[51773]:TENANT`nUSER=test`nPW=synthetic=secret`nSSL=false`n")
    $connection = @(ReadSoftwareConnections @($iniPath))[0]
    Assert ($connection.Port -eq 51773 -and $connection.Namespace -eq 'TENANT' -and $connection.Password -eq 'synthetic=secret') 'plain legacy credentials preserve equals signs and nondefault server ports'
    [IO.File]::WriteAllText($iniPath, $ini)
    [IO.File]::Delete($xmlPath)
    $script:services = @($ntc,$pos)
    $found = FindSoftwareDatabaseConfiguration
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq $iniPath -and $found.CheckedPaths -contains $xmlPath) 'an available POS configuration can supply the connection while the missing NTC path stays visible'
    [IO.File]::Delete($iniPath)
    $found = FindSoftwareDatabaseConfiguration
    Assert ($found.Paths.Count -eq 0 -and $found.CheckedPaths.Count -eq 2) 'missing files retain exact service-derived paths for diagnostics'
    $script:services = @((Service 'novacom pos updateservice' 'not-an-executable'))
    ExpectFailure { FindSoftwareDatabaseConfiguration } 'an unreadable service command is reported without guessing a directory'
    $script:services = @((Service 'novacom pos updateservice' 'relative.exe'))
    ExpectFailure { FindSoftwareDatabaseConfiguration } 'relative service executable paths are rejected'
    $script:services = @()
    Assert ((FindSoftwareDatabaseConfiguration).Services.Count -eq 0) 'an absent POS service is not a mandatory installation requirement'
    $script:cimError = $true
    ExpectFailure { FindSoftwareDatabaseConfiguration } 'service access failures are not reported as missing installations'
    Write-Host 'PASS service-based discovery and INI tests completed without changing services or connecting to a database'
} finally {
    $env:NOVATOUCH_DISCOVERY_FIXTURE = $previousFixtureVariable
=======
$script:defaultRoots = @{ ntc=@(); pos=@() }
function GetSoftwareDefaultInstallDirectories([string]$Kind) { $script:defaultRoots[$Kind] }
function Layout([string]$Relative, [string]$Exe='Nov.WaWi.exe') {
    $root = Join-Path $fixture $Relative
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'bin'))
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Einstellungen'))
    [IO.File]::WriteAllText((Join-Path $root 'bin\InterSystems.Data.IRISClient.dll'), 'Fixture only, never loaded')
    [IO.File]::WriteAllText((Join-Path $root ('bin\' + $Exe)), 'Fixture only, never executed')
    [IO.File]::WriteAllText((Join-Path $root 'Einstellungen\einstellung-Cache.xml'), '<einstellungen />')
    return $root
}
$script:installer = New-Object -ComObject WindowsInstaller.Installer
try {
    $standard = Layout 'Program Files (x86)\Novacom\Lagerverwaltung'
    $custom = Layout 'Custom Installation\Control'
    $pos = Layout 'Program Files\NovaTouch' 'KassaPreh.exe'
    $script:defaultRoots.ntc = @($standard); $script:defaultRoots.pos = @($pos)
    $program = [pscustomobject]@{ Kind='ntc'; InstallLocation=''; ExecutablePath=''; ProductCode='' }
    $found = FindSoftwareDatabaseConfiguration @($program)
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq (Join-Path $standard 'Einstellungen\einstellung-Cache.xml')) 'empty InstallLocation and an ICO-only DisplayIcon find the reported Program Files (x86) layout'
    Assert ($found.Directories -contains (Join-Path $standard 'bin')) 'the settings fallback also retains the bin directory for loading the InterSystems provider'
    Assert ($found.CheckedPaths -contains $found.Paths[0]) 'the exact checked settings path is available for diagnostics'
    $program.InstallLocation = $custom
    $found = FindSoftwareDatabaseConfiguration @($program)
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq (Join-Path $custom 'Einstellungen\einstellung-Cache.xml')) 'a registered custom installation takes precedence over an unrelated standard installation'
    $program.InstallLocation = ''
    $program.ExecutablePath = Join-Path $custom 'bin\Nov.WaWi.exe'
    $found = FindSoftwareDatabaseConfiguration @($program)
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq (Join-Path $custom 'Einstellungen\einstellung-Cache.xml')) 'an EXE DisplayIcon resolves a custom bin path without a standard-path fallback'
    $program.ExecutablePath = ''

    # The cached MSI is a synthetic, read-only database; ComponentPath and
    # ProductInfo are simulated so no real installed product is queried.
    $script:cached = Join-Path $fixture 'cached.msi'
    $db = $script:installer.OpenDatabase($script:cached, 3)
    try {
        foreach ($sql in @(
            'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38) PRIMARY KEY `Component`)',
            'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL PRIMARY KEY `File`)',
            'INSERT INTO `Component` (`Component`, `ComponentId`) VALUES (''MainExe'', ''{97D27278-113E-4427-B838-C7457FE18D03}'')',
            'INSERT INTO `File` (`File`, `Component_`, `FileName`) VALUES (''MainFile'', ''MainExe'', ''NOVWAWI.EXE|Nov.WaWi.exe'')'
        )) {
            $view = $db.OpenView($sql)
            try { [void]$view.Execute() } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
        [void]$db.Commit()
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) }
    $script:componentPath = Join-Path $custom 'bin\Nov.WaWi.exe'; $script:componentReads = 0; $script:cacheBroken = $false; $script:locationError = $false
    $script:fakeInstaller = [pscustomobject]@{}
    $script:fakeInstaller | Add-Member ScriptMethod ProductInfo {
        param($ProductCode,$Attribute)
        Assert ($ProductCode -eq '{52D1D4EB-E29A-466A-A413-BE6B080D330E}') 'MSI lookup uses only the detected product code'
        if ($Attribute -eq 'InstallLocation') { if ($script:locationError) { throw 'Simulated missing InstallLocation property' }; return '' }
        if ($Attribute -eq 'LocalPackage') { if ($script:cacheBroken) { throw 'Simulated missing cached MSI' }; return $script:cached }
        throw 'Unexpected product property'
    }
    $script:fakeInstaller | Add-Member ScriptMethod OpenDatabase {
        param($Path,$Mode)
        Assert ($Path -eq $script:cached -and $Mode -eq 0) 'the cached MSI is opened read-only'
        return $script:installer.OpenDatabase($Path,$Mode)
    }
    $script:fakeInstaller | Add-Member ScriptMethod ComponentPath {
        param($ProductCode,$Component)
        Assert ($Component -eq '{97D27278-113E-4427-B838-C7457FE18D03}') 'the executable component is obtained from the cached MSI File and Component tables'
        $script:componentReads++
        return $script:componentPath
    }
    function New-Object {
        param([string]$ComObject)
        if ($ComObject -ne 'WindowsInstaller.Installer') { throw 'Unexpected COM access' }
        return $script:fakeInstaller
    }
    $program.ProductCode = '{52D1D4EB-E29A-466A-A413-BE6B080D330E}'
    $found = FindSoftwareDatabaseConfiguration @($program)
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq (Join-Path $custom 'Einstellungen\einstellung-Cache.xml') -and $script:componentReads -eq 1) 'Windows Installer resolves a custom installation even without either ARP path'
    $script:locationError = $true
    $found = FindSoftwareDatabaseConfiguration @($program)
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq (Join-Path $custom 'Einstellungen\einstellung-Cache.xml')) 'an unavailable MSI InstallLocation property does not prevent component-path resolution'
    $script:cacheBroken = $true
    $found = FindSoftwareDatabaseConfiguration @($program)
    Assert ($found.Paths.Count -eq 1 -and $found.Paths[0] -eq (Join-Path $standard 'Einstellungen\einstellung-Cache.xml')) 'a missing MSI cache still permits the known standard installation'
    $program.InstallLocation = $custom
    $posProgram = [pscustomobject]@{ Kind='pos'; InstallLocation=''; ExecutablePath='' }
    $found = FindSoftwareDatabaseConfiguration @($program,$posProgram)
    Assert ($found.Paths.Count -eq 2 -and $found.Paths -contains (Join-Path $custom 'Einstellungen\einstellung-Cache.xml') -and $found.Paths -contains (Join-Path $pos 'Einstellungen\einstellung-Cache.xml')) 'each installed product resolves its own settings when only POS needs a fallback'
    $found = FindSoftwareDatabaseConfiguration @()
    Assert ($found.Paths.Count -eq 2) 'a standard installation with a real application EXE can be found even if uninstall registration is missing'
    [IO.File]::Delete((Join-Path $standard 'bin\Nov.WaWi.exe')); [IO.File]::Delete((Join-Path $pos 'bin\KassaPreh.exe'))
    $found = FindSoftwareDatabaseConfiguration @()
    Assert ($found.Paths.Count -eq 0) 'unregistered settings directories without an application EXE are not treated as installed products'
    $program.InstallLocation = ''; $program.ProductCode = ''; $script:defaultRoots.ntc = @((Join-Path $fixture 'Missing')); $script:defaultRoots.pos = @()
    $found = FindSoftwareDatabaseConfiguration @($program)
    Assert ($found.Paths.Count -eq 0 -and $found.CheckedPaths.Count -gt 0) 'a failed search retains its attempted paths for the log'
    Write-Host 'PASS database discovery tests completed without querying actual products or connecting to a database'
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($script:installer)
>>>>>>> 6ac4f29e551324452a0ab63e15ea7189e805adec
    if ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
