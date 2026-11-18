. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message }; Write-Host "PASS $message" }
$indexPath = Join-Path $TestArtifactsRoot 'local-msi-fixtures.json'
if (-not (Test-Path -LiteralPath $indexPath)) { throw 'Zuerst MainForm.Tests --local ausführen, um die ausschließlich lesbaren MSI-Testdateien zu erzeugen.' }
$msis = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('msi-worker-' + [guid]::NewGuid().ToString('N'))))
Assert ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'MSI test writes stay within the workspace test-artifact directory'
[void][IO.Directory]::CreateDirectory($fixture)
$copyTestTemp = Join-Path $fixture 'temporary'
[void][IO.Directory]::CreateDirectory($copyTestTemp)
$copyTestOriginalTmp = $env:TMP; $copyTestOriginalTemp = $env:TEMP
$env:TMP=$copyTestTemp; $env:TEMP=$copyTestTemp
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'MsiUpdates.ps1'))))
function FixtureProductCode([string]$path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($path, 0)
    $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')
    $null = $view.Execute(); $record = $view.Fetch(); $value = [string]$record.StringData(1)
    foreach ($com in @($record, $view, $database, $installer)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) }
    $value
}
$code = FixtureProductCode ([string]$msis.ntc)
$package = [pscustomobject]@{ Kind='ntc'; ProductName='NovaTouch Control'; ProductVersion='2.0.0'; ProductCode=$code; UpgradeCode='';
    PayloadPath=[string]$msis.ntc; RelativeMsiPath='ntc.msi'; Sha256=(Get-FileHash -LiteralPath $msis.ntc -Algorithm SHA256).Hash; CompanionFiles=@() }

foreach ($version in @('2.0.0','3.0.0','2.0.0.99')) {
    $decision = GetMsiUpdateDecision @([pscustomobject]@{ Kind='ntc'; DisplayName='NovaTouch Control'; Version=$version; ProductCode=$code }) $package
    Assert (-not $decision.Install -and -not $decision.Skipped) 'same/newer Windows MSI versions are skipped using the first three fields'
}
foreach ($kind in @('pos','ntc','ntv','oman')) {
    $names=@{ pos='NovaTouch POS'; ntc='NovaTouch Control'; ntv='NovaTouch Voucher'; oman='NovaTouch Orderman SOL' }
    $fourPartPackage=[pscustomobject]@{ Kind=$kind; ProductName=$names[$kind]; ProductVersion='2026.4.9800.12345' }
    foreach ($test in @(
        @{ Version='2026.4.9800.12344'; Install=$false },
        @{ Version='2026.4.9800.9999'; Install=$false },
        @{ Version='2026.4.9800.12345'; Install=$false },
        @{ Version='2026.4.9800.12346'; Install=$false },
        @{ Version='2026.4.9800'; Install=$false },
        @{ Version='2026.4.9799'; Install=$true },
        @{ Version='2026.4.9799.65535'; Install=$true },
        @{ Version='2026.4.9801.0'; Install=$false }
    )) {
        $decision=GetMsiUpdateDecision @([pscustomobject]@{ Kind=$kind; DisplayName=$names[$kind]; Version=$test.Version }) $fourPartPackage
        Assert ($decision.Install -eq $test.Install) "$kind compares the three-part Windows version for installation: $($test.Version) -> 2026.4.9800.12345"
    }
}
Assert ((GetMsiUpdateDecision @() $package).Skipped) 'absent products are never installed'
Assert ((GetMsiUpdateDecision @([pscustomobject]@{ Kind='ntc'; DisplayName='NovaTouch Control'; Version='1.9.9'; ProductCode=$code }) $package).Install) 'older installed products are eligible for an MSI upgrade'
$unknown = $false
try { GetMsiUpdateDecision @([pscustomobject]@{ Kind='ntc'; DisplayName='NovaTouch Control'; Version='unknown'; ProductCode=$code }) $package | Out-Null } catch { $unknown = $true }
Assert $unknown 'an unreadable installed MSI version is an error rather than a speculative reinstall'

$source = [IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'RemoteWorker.ps1'))
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$null, [ref]$null)
$readers = @{
    ReadInventory = 'function ReadInventory { return @() }'
    AssertLocalUpdateAccess = 'function AssertLocalUpdateAccess { $global:adminChecks++ }'
    ReadSoftwareInventory = @'
function ReadSoftwareInventory {
    $programs = @()
    if ($global:installedVersion) { $programs = @([pscustomobject]@{ Kind=$global:fixtureKind; DisplayName=$global:fixtureName; Version=$global:installedVersion; ProductCode=$global:fixtureProductCode; InstallLocation='' }) }
    [pscustomobject]@{ DotNet48Installed=$true; PosVersion='Nicht installiert'; NtcVersion=$(if ($global:fixtureKind -eq 'ntc') { $global:installedVersion } else { 'Nicht installiert' }); InstalledPrograms=$programs }
}
'@
    CopyRemoteFileWithProgress = @'
function CopyRemoteFileWithProgress($session, [string]$localFile, [string]$remoteFile, [string]$label) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($remoteFile))
    [IO.File]::Copy($localFile, $remoteFile, $true)
    $global:copies++
}
'@
}
foreach ($node in @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $readers.ContainsKey($node.Name) }, $true) | Sort-Object { $_.Extent.StartOffset } -Descending)) {
    $source = $source.Remove($node.Extent.StartOffset, $node.Extent.EndOffset - $node.Extent.StartOffset).Insert($node.Extent.StartOffset, $readers[$node.Name])
}
$source = $source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 0','return').Replace('exit 1','return')
$worker = [scriptblock]::Create($source)
$global:fixtureProductCode = $code
$global:fixtureServices = @{}
$global:serviceEvents = [Collections.Generic.List[string]]::new()
$global:operationEvents = [Collections.Generic.List[string]]::new()
$global:fixtureProcesses = @{}
$global:processReads = 0
$global:stopFailure = ''; $global:stopRace = ''; $global:stopTimeout = ''; $global:respawnProcess = ''
function FixtureProcess([string]$name) {
    $item = [pscustomobject]@{ ProcessName=$name; Exited=$false; Disposed=$false }
    $item | Add-Member ScriptProperty HasExited { $this.Exited }
    $item | Add-Member ScriptMethod WaitForExit { param($milliseconds) Assert ($milliseconds -eq 10000) 'process exit wait is bounded'; $this.Exited }
    $item | Add-Member ScriptMethod Dispose { $this.Disposed=$true }
    $global:fixtureProcesses[$name]=$item
}
function Get-Process {
    [CmdletBinding()]param([string[]]$Name)
    Assert ($Name.Count -eq 3 -and @($Name | Where-Object { $_ -notin @('Nov.WaWi','Nov.WaWi.ReportingService','Nov.WaWi.UpdateService') }).Count -eq 0) 'only the three exact Control image names are queried'
    $global:processReads++
    if ($global:respawnProcess -and $global:processReads -eq 2) { FixtureProcess $global:respawnProcess }
    @($global:fixtureProcesses.Values | Where-Object { $_.ProcessName -in $Name -and -not $_.Exited })
}
function Stop-Process {
    [CmdletBinding()]param($InputObject,[switch]$Force)
    Assert ($Force -and $InputObject.ProcessName -in @('Nov.WaWi','Nov.WaWi.ReportingService','Nov.WaWi.UpdateService')) 'only an explicitly selected Control process is force-stopped'
    $global:operationEvents.Add('kill:' + $InputObject.ProcessName)
    if ($InputObject.ProcessName -eq $global:stopFailure) { throw 'Synthetic access denied' }
    if ($InputObject.ProcessName -ne $global:stopTimeout) { $InputObject.Exited=$true }
    if ($InputObject.ProcessName -eq $global:stopRace) { throw 'Synthetic process exited concurrently' }
}
function Get-Service {
    [CmdletBinding()] param([string]$Name)
    if (-not $global:fixtureServices.ContainsKey($Name)) { throw "Unexpected service access: $Name" }
    $global:fixtureServices[$Name]
}
function Stop-Service {
    [CmdletBinding()] param([string]$Name, [switch]$Force)
    (Get-Service $Name).Status='Stopped'; $global:serviceEvents.Add('stop:' + $Name)
    $global:operationEvents.Add('stop:' + $Name)
}
function Start-Service {
    [CmdletBinding()] param([string]$Name)
    (Get-Service $Name).Status='Running'; $global:serviceEvents.Add('start:' + $Name)
    $global:operationEvents.Add('start:' + $Name)
}
function Set-Service {
    [CmdletBinding()] param([string]$Name, [string]$StartupType)
    (Get-Service $Name).StartType=$StartupType; $global:serviceEvents.Add('type:' + $Name + ':' + $StartupType)
}
function New-PSSessionOption { param($OpenTimeout, $OperationTimeout) if ($script:localMode) { throw 'Local execution must not create WinRM options' }; return @{} }
function New-PSSession { param($ComputerName, $Port, $SessionOption, [switch]$UseSSL, $ErrorAction) if ($script:localMode) { throw 'Local execution must not create a WinRM session' }; $global:sessions++; return [pscustomobject]@{ Id=1 } }
function Invoke-Command { param($Session, $ScriptBlock, [object[]]$ArgumentList, $ErrorAction) if ($script:localMode) { throw 'Local execution must not invoke a remote command' }; & $ScriptBlock @ArgumentList }
function Remove-PSSession { param($Session, $ErrorAction) if ($script:localMode) { throw 'Local execution must not remove a WinRM session' }; $global:closed++ }
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$PassThru, [switch]$Wait, $WindowStyle)
    Assert ($FilePath.EndsWith('\msiexec.exe') -and $ArgumentList.Contains('/qn /norestart REBOOT=ReallySuppress') -and $WindowStyle -eq 'Hidden' -and $Wait -and $PassThru) 'MSI execution waits for installer exit, remains silent and suppresses automatic Windows restarts'
    Assert ($ArgumentList.Contains('REINSTALL=ALL REINSTALLMODE=vomus')) 'same-product-code minor upgrades use MSI reinstall properties'
    if ($global:fixtureServices.Count -gt 0) { Assert (@($global:fixtureServices.Values | Where-Object Status -ne 'Stopped').Count -eq 0) 'all MSI-associated services stop before installation' }
    if ($global:fixtureKind -eq 'ntc') {
        Assert ($global:processReads -eq 2 -and @($global:fixtureProcesses.Values | Where-Object { $_.ProcessName -in @('Nov.WaWi','Nov.WaWi.ReportingService','Nov.WaWi.UpdateService') -and -not $_.Exited }).Count -eq 0) 'NTC msiexec starts only after all targeted processes have exited and a fresh check confirms their absence'
    } else { Assert ($global:processReads -eq 0) 'other MSI products do not query or stop Control processes' }
    $global:operationEvents.Add('msi')
    if ($global:expectCab) {
        $msiArgument = [regex]::Match($ArgumentList, '^/i "([^"]+)"').Groups[1].Value
        $cabArgument = Join-Path ([IO.Path]::GetDirectoryName($msiArgument)) 'data.cab'
        Assert (Test-Path -LiteralPath $cabArgument) ('an external MSI cabinet is present beside the staged MSI: ' + $cabArgument)
    }
    $global:installerCalls++
    if ($global:directSource -and [IO.Path]::GetExtension([string]$package.PayloadPath) -ieq '.msi') {
        $actualMsi = [regex]::Match($ArgumentList, '^/i "([^"]+)"').Groups[1].Value
        Assert ($actualMsi -ieq [string]$package.PayloadPath) 'direct local MSI installation uses the original file without staging another MSI copy'
    }
    if ($global:installerExit -in @(0,3010,1641)) { $global:installedVersion = $(if ($global:installedAfter) { $global:installedAfter } else { ([Version]$package.ProductVersion).ToString(3) }) }
    [pscustomobject]@{ ExitCode=$global:installerExit }
}

function RunCase([string]$name, [string]$version, [bool]$local, [int]$exitCode, [bool]$expectedSuccess, [bool]$expectedInstall, [bool]$preparedFailure=$false, [string]$action='update', [bool]$direct=$false, [bool]$emptyPath=$false, [string]$expectedError='', [string]$packageShape='both', [string]$installedAfter='', [bool]$temporary=$false, [bool]$reuse=$false, [bool]$skipTransfer=$false) {
    $script:localMode=$local
    $global:fixtureKind=$package.Kind; $global:fixtureName=$package.ProductName; $global:fixtureProductCode=$package.ProductCode
    $global:expectCab=@($package.CompanionFiles).Count -gt 0
    $global:installedVersion=$version; $global:installerExit=$exitCode
    $global:installedAfter=$installedAfter
    $global:installerCalls=0; $global:copies=0; $global:sessions=0; $global:closed=0; $global:adminChecks=0
    $global:processReads=0; $global:operationEvents.Clear()
    $global:directSource=$local -and ($direct -or $emptyPath)
    $base = Join-Path $fixture $name
    $global:currentBase=$base
    $request = @{ Action=$action; Host=$(if ($local) { 'LOCALHOST' } else { 'fixture.invalid' }); LocalExecution=$local; Username=''; Password='';
        UseSsl=$false; ForceUpdate=$true; RetryUpdate=$true; SkipZipTransfer=($reuse -or $skipTransfer); SkipRemotePackageCopy=$temporary; SkipLocalPackageCopy=$direct; PaymentZip=''; FiscalZip=''; ServerSuiteZip=''; RemoteInstallPath=$(if ($emptyPath) { '' } else { $base }); MsiPackage=$package; MsiPackages=@($package); CheckUpdateUpload=$true }
    if ($reuse) {
        [void][IO.Directory]::CreateDirectory($base)
        foreach ($file in @([pscustomobject]@{LocalPath=$package.PayloadPath;RelativePath=[IO.Path]::GetFileName($package.PayloadPath)}) + @($package.CompanionFiles)) {
            $path=Join-Path $base $file.RelativePath
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            [IO.File]::Copy($file.LocalPath,$path,$true)
        }
    }
    if ($packageShape -eq 'single') { $request.Remove('MsiPackages') }
    if ($packageShape -eq 'list') { $request.Remove('MsiPackage') }
    $script:requestJson = $request | ConvertTo-Json -Depth 8 -Compress
    $original = [Console]::Out; $writer = [IO.StringWriter]::new()
    try { [Console]::SetOut($writer); & $worker } finally { [Console]::SetOut($original) }
    $messages = @($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $result = @($messages | Where-Object Event -eq 'result')[-1]
    Assert ($result.Success -eq $expectedSuccess) ("$name reports expected outcome: " + ($result | ConvertTo-Json -Depth 4 -Compress))
    if ($expectedError) { Assert ($result.Error -like ('*' + $expectedError + '*')) "$name reports the source validation error" }
    Assert ($global:installerCalls -eq [int]$expectedInstall) "$name starts an installer only for an older installed product"
    if ($package.Kind -ne 'ntc') { Assert ($global:processReads -eq 0) "$name leaves Control processes alone" }
    if (-not $expectedInstall -and -not $preparedFailure) { Assert ($global:copies -eq 0 -and ($reuse -or -not (Test-Path -LiteralPath $base))) "$name performs no transfer or extraction" }
    if ($preparedFailure) { Assert ($result.Error -like '*MSI-ProductName*' -and $global:copies -gt 0) "$name rejects inconsistent client MSI metadata before installation" }
    if ($action -eq 'check-upload-directory') { Assert (-not $result.UploadRequired -and @($messages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'MSI-Vorprüfung' }).Count -eq 1) "$name defers an unreadable MSI version to its component without blocking unrelated updates" }
    if ($local) { Assert ($global:sessions -eq 0 -and $global:closed -eq 0) "$name uses no WinRM calls" }
    elseif ($packageShape -eq 'list') { Assert ($global:sessions -eq 0 -and $global:closed -eq 0 -and $global:copies -eq 0) "$name rejects ambiguous multi-package installation before opening a session" }
    else { Assert ($global:sessions -eq 1 -and $global:closed -eq 1 -and $global:adminChecks -eq 0) "$name installs only on the authenticated remote target and releases the session" }
    if ($temporary) { Assert (-not (Test-Path -LiteralPath $base) -and @(Get-ChildItem -LiteralPath $copyTestTemp -Directory -Filter '.ntu-*').Count -eq 0) "$name cleans temporary packages without creating the configured target directory" }
    if ($reuse) { Assert ($global:copies -eq 0) "$name reuses verified target packages without transferring them" }
    if ($expectedSuccess -and $expectedInstall) { Assert ($result.RebootRequired -eq ($exitCode -in @(3010,1641)) -and @($result.InstalledPrograms | Where-Object { $_.Kind -eq $package.Kind -and $_.Version -eq ([Version]$package.ProductVersion).ToString(3) }).Count -eq 1) "$name verifies the three-part installed Windows version and preserves reboot-required status" }
    if (Test-Path -LiteralPath $base) { Assert (@(Get-ChildItem -LiteralPath $base -Directory -Filter '.novatouch-updater-msi-*').Count -eq 0) "$name removes its temporary MSI staging directory" }
    if ($global:directSource) {
        Assert ($global:copies -eq 0 -and -not (Test-Path -LiteralPath $base)) "$name creates no destination and makes no extra payload copy"
        Assert (Test-Path -LiteralPath $package.PayloadPath) "$name retains the original package"
        Assert (@(Get-ChildItem -LiteralPath $copyTestTemp -Directory -Filter '.novatouch-updater-msi-*').Count -eq 0) "$name cleans its temporary extraction folder"
    }
}
try {
    RunCase 'local-absent' '' $true 0 $true $false
    RunCase 'local-current' '2.0.0' $true 0 $true $false
    RunCase 'local-newer' '3.0.0' $true 0 $true $false
    RunCase 'local-upgrade' '1.0.0' $true 0 $true $true
    RunCase 'local-reboot-required' '1.0.0' $true 3010 $true $true
    RunCase 'local-installer-failure' '1.0.0' $true 1603 $false $true
    RunCase -name 'remote-absent' -version '' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $false
    RunCase -name 'remote-upgrade' -version '1.0.0' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $true
    RunCase -name 'remote-current-force' -version '2.0.0' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $false
    RunCase -name 'remote-unknown-preflight' -version 'unknown' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $false -action 'check-upload-directory'
    RunCase -name 'remote-installer-failure' -version '1.0.0' -local $false -exitCode 1603 -expectedSuccess $false -expectedInstall $true
    RunCase -name 'remote-temporary' -version '1.0.0' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $true -temporary $true -skipTransfer $true
    RunCase -name 'remote-reuse' -version '1.0.0' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $true -reuse $true
    RunCase -name 'local-direct-msi' -version '1.0.0' -local $true -exitCode 0 -expectedSuccess $true -expectedInstall $true -direct $true
    RunCase -name 'local-empty-destination' -version '1.0.0' -local $true -exitCode 0 -expectedSuccess $true -expectedInstall $true -emptyPath $true
    RunCase -name 'remote-local-option-keeps-remote-staging' -version '1.0.0' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $true -direct $true
    $verifiedHash=$package.Sha256; $package.Sha256='0' * 64
    RunCase -name 'local-direct-hash-failure' -version '1.0.0' -local $true -exitCode 0 -expectedSuccess $false -expectedInstall $false -direct $true -expectedError 'SHA256'
    $package.Sha256=$verifiedHash

    $package.PayloadPath = Join-Path ([IO.Path]::GetDirectoryName([string]$msis.ntc)) 'arbitrary-control-name.zip'
    $package.RelativeMsiPath = 'installer/Control.msi'
    $package.Sha256 = (Get-FileHash -LiteralPath $package.PayloadPath -Algorithm SHA256).Hash
    RunCase 'local-msi-in-zip' '1.0.0' $true 0 $true $true
    RunCase -name 'remote-msi-in-zip' -version '1.0.0' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $true
    RunCase -name 'local-direct-msi-zip' -version '1.0.0' -local $true -exitCode 0 -expectedSuccess $true -expectedInstall $true -direct $true

    $package.PayloadPath = [string]$msis.pos; $package.RelativeMsiPath = 'pos.msi'
    $package.Sha256 = (Get-FileHash -LiteralPath $package.PayloadPath -Algorithm SHA256).Hash
    RunCase 'wrong-msi-payload' '1.0.0' $true 0 $false $false $true

    $cab = Join-Path ([IO.Path]::GetDirectoryName([string]$msis.ntv)) 'data.cab'
    $package = [pscustomobject]@{ Kind='ntv'; ProductName='NovaTouch Voucher'; ProductVersion='2.0.0'; ProductCode=(FixtureProductCode ([string]$msis.ntv)); UpgradeCode='';
        PayloadPath=[string]$msis.ntv; RelativeMsiPath='ntv.msi'; Sha256=(Get-FileHash -LiteralPath $msis.ntv -Algorithm SHA256).Hash;
        CompanionFiles=@([pscustomobject]@{ LocalPath=$cab; RelativePath='data.cab'; Sha256=(Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash }) }
    RunCase 'local-external-cabinet' '1.0.0' $true 0 $true $true
    Assert ($global:copies -eq 2) 'both MSI and external cabinet are transferred and verified'
    RunCase -name 'remote-external-cabinet' -version '1.0.0' -local $false -exitCode 0 -expectedSuccess $true -expectedInstall $true
    RunCase -name 'local-direct-external-cabinet' -version '1.0.0' -local $true -exitCode 0 -expectedSuccess $true -expectedInstall $true -direct $true

    foreach ($kind in @('pos','ntc','ntv','oman')) {
        $package=[pscustomobject]@{ Kind=$kind; ProductName=$names[$kind]; ProductVersion='2.0.0'; ProductCode=(FixtureProductCode ([string]$msis.$kind)); UpgradeCode='';
            PayloadPath=[string]$msis.$kind; RelativeMsiPath=($kind + '.msi'); Sha256=(Get-FileHash -LiteralPath $msis.$kind -Algorithm SHA256).Hash; CompanionFiles=@() }
        foreach ($shape in @('single','list')) {
            RunCase -name ("remote-$kind-$shape") -version '1.0.0' -local $false -exitCode 0 -expectedSuccess ($shape -eq 'single') -expectedInstall ($shape -eq 'single') -expectedError $(if($shape -eq 'list'){'einzeln'}else{''}) -packageShape $shape
        }
    }

    $serviceMsi = Join-Path $fixture 'control-with-services.msi'
    [IO.File]::Copy([string]$msis.ntc, $serviceMsi)
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($serviceMsi, 1)
    try {
        foreach ($query in @('CREATE TABLE `ServiceControl` (`Name` CHAR(255) NOT NULL PRIMARY KEY `Name`)',
            'INSERT INTO `ServiceControl` (`Name`) VALUES (''NTUFixtureRunning'')', 'INSERT INTO `ServiceControl` (`Name`) VALUES (''NTUFixtureDisabled'')')) {
            $view = $database.OpenView($query)
            try { $null = $view.Execute() } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
        $null = $database.Commit()
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
    foreach ($name in @('NTUFixtureRunning','NTUFixtureDisabled')) {
        $disabled = $name.EndsWith('Disabled')
        $service = [pscustomobject]@{ Name=$name; Status=$(if ($disabled) { 'Stopped' } else { 'Running' }); StartType=$(if ($disabled) { 'Disabled' } else { 'Automatic' }) }
        $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($status, $timeout) Assert ($this.Status -eq $status) 'MSI service status is verified' }
        $global:fixtureServices[$name]=$service
    }
    $package = [pscustomobject]@{ Kind='ntc'; ProductName='NovaTouch Control'; ProductVersion='2.0.0'; ProductCode=$code; UpgradeCode='';
        PayloadPath=$serviceMsi; RelativeMsiPath='control-with-services.msi'; Sha256=(Get-FileHash -LiteralPath $serviceMsi -Algorithm SHA256).Hash; CompanionFiles=@() }
    RunCase 'local-msi-services' '1.0.0' $true 0 $true $true
    Assert ($global:serviceEvents.Contains('stop:NTUFixtureRunning') -and $global:serviceEvents.Contains('start:NTUFixtureRunning') -and
        $global:fixtureServices.NTUFixtureDisabled.StartType -eq 'Disabled' -and $global:fixtureServices.NTUFixtureDisabled.Status -eq 'Stopped') 'MSI service restarts preserve disabled services'
    foreach ($local in @($true,$false)) {
        $suffix = if ($local) { 'local' } else { 'remote' }
        $global:fixtureProcesses.Clear()
        foreach ($name in @('Nov.WaWi','nOv.WaWi.rEpOrTiNgSeRvIcE','Nov.WaWi.UpdateService','Nov.WaWi.Other','KassaPreh','nt.payment')) { FixtureProcess $name }
        RunCase -name ("ntc-processes-skipped-$suffix") -version '2.0.0' -local $local -exitCode 0 -expectedSuccess $true -expectedInstall $false
        Assert ($global:processReads -eq 0 -and @($global:fixtureProcesses.Values | Where-Object Exited).Count -eq 0) 'an already-current Control installation never touches running processes'
        RunCase -name ("ntc-processes-absent-$suffix") -version '' -local $local -exitCode 0 -expectedSuccess $true -expectedInstall $false
        Assert ($global:processReads -eq 0) 'an absent Control installation never touches running processes'
        RunCase -name ("ntc-processes-stop-$suffix") -version '1.0.0' -local $local -exitCode 0 -expectedSuccess $true -expectedInstall $true
        Assert (@($global:operationEvents | Where-Object { $_ -like 'kill:*' }).Count -eq 3 -and
            $global:operationEvents.IndexOf('stop:NTUFixtureRunning') -lt $global:operationEvents.IndexOf('kill:Nov.WaWi') -and
            $global:operationEvents.IndexOf('kill:Nov.WaWi.UpdateService') -lt $global:operationEvents.IndexOf('msi') -and
            $global:operationEvents.IndexOf('msi') -lt $global:operationEvents.IndexOf('start:NTUFixtureRunning') -and
            -not $global:fixtureProcesses['Nov.WaWi.Other'].Exited -and -not $global:fixtureProcesses.KassaPreh.Exited -and -not $global:fixtureProcesses['nt.payment'].Exited) 'only Control images stop between service shutdown and MSI launch; unrelated products remain running'
        foreach ($failure in @('denied','timeout','respawn','race')) {
            $global:fixtureProcesses.Clear(); FixtureProcess 'Nov.WaWi'
            switch ($failure) {
                'denied' { $global:stopFailure='Nov.WaWi' }
                'timeout' { $global:stopTimeout='Nov.WaWi' }
                'respawn' { $global:respawnProcess='Nov.WaWi' }
                'race' { $global:stopRace='Nov.WaWi' }
            }
            try {
                RunCase -name ("ntc-processes-$failure-$suffix") -version '1.0.0' -local $local -exitCode 0 -expectedSuccess ($failure -eq 'race') -expectedInstall ($failure -eq 'race') -reuse $true -expectedError $(if($failure -eq 'race'){''}elseif($failure -eq 'respawn'){'erneut aktiv'}else{'konnte nicht beendet'})
                Assert ($global:fixtureServices.NTUFixtureRunning.Status -eq 'Running' -and $global:fixtureServices.NTUFixtureDisabled.Status -eq 'Stopped') 'a failed process stop restores associated service states without starting msiexec'
            } finally { $global:stopFailure='';$global:stopTimeout='';$global:respawnProcess='';$global:stopRace='' }
        }
        $global:fixtureProcesses.Clear()
    }
    $global:fixtureServices.Clear()
    $package = [pscustomobject]@{ Kind='ntc'; ProductName='NovaTouch Control'; ProductVersion='2026.4.9800.12345'; ProductCode=(FixtureProductCode ([string]$msis.ntcFourPart)); UpgradeCode='';
        PayloadPath=[string]$msis.ntcFourPart; RelativeMsiPath='control-four-part.msi'; Sha256=(Get-FileHash -LiteralPath $msis.ntcFourPart -Algorithm SHA256).Hash; CompanionFiles=@() }
    RunCase 'local-four-part-same-release' '2026.4.9800.12344' $true 0 $true $false
    RunCase 'local-four-part-current' '2026.4.9800.12345' $true 0 $true $false
    RunCase 'local-four-part-newer' '2026.4.9800.12346' $true 0 $true $false
    RunCase 'local-four-part-missing-revision' '2026.4.9800' $true 0 $true $false
    RunCase -name 'local-four-part-upgrade' -version '2026.4.9799' -local $true -exitCode 0 -expectedSuccess $true -expectedInstall $true -direct $true -installedAfter '2026.4.9800'
    RunCase -name 'local-four-part-verification-failure' -version '2026.4.9799' -local $true -exitCode 0 -expectedSuccess $false -expectedInstall $true -installedAfter '2026.4.9799' -expectedError 'Installierte MSI-Version wurde nicht bestätigt'
    $package = [pscustomobject]@{ Kind='ntc'; ProductName='NovaTouch Control'; ProductVersion='2026.4.9800.12345'; MsiProductVersion='2026.4.9800'; ProductCode=(FixtureProductCode ([string]$msis.ntcRelease)); UpgradeCode='';
        PayloadPath=[string]$msis.ntcRelease; RelativeMsiPath='ntc-release.msi'; Sha256=(Get-FileHash -LiteralPath $msis.ntcRelease -Algorithm SHA256).Hash; CompanionFiles=@() }
    RunCase -name 'local-full-release-three-part-msi' -version '2026.4.9799' -local $true -exitCode 0 -expectedSuccess $true -expectedInstall $true -direct $true
    RunCase 'local-full-release-three-part-msi-current' '2026.4.9800' $true 0 $true $false
    $package.MsiProductVersion='2026.4.9800.0'
    RunCase -name 'local-full-release-invalid-msi-property' -version '2026.4.9799' -local $true -exitCode 0 -expectedSuccess $false -expectedInstall $false -direct $true -expectedError 'MSI-ProductVersion stimmt nicht'
} finally {
    $env:TMP=$copyTestOriginalTmp; $env:TEMP=$copyTestOriginalTemp
    $checked = [IO.Path]::GetFullPath($fixture)
    if ($checked.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $checked -Recurse -Force }
}
