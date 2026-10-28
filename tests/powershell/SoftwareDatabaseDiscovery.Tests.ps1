. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'SoftwareDatabase.ps1'))))
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('database-discovery-' + [guid]::NewGuid().ToString('N'))))
Assert ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'discovery fixtures stay in test artifacts'
[void][IO.Directory]::CreateDirectory($fixture)
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
    if ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
