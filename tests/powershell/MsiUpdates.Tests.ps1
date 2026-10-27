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
    Assert (-not $decision.Install -and -not $decision.Skipped) 'same/newer MSI versions are skipped, including Windows Installer fourth-field semantics'
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
function Get-Service {
    [CmdletBinding()] param([string]$Name)
    if (-not $global:fixtureServices.ContainsKey($Name)) { throw "Unexpected service access: $Name" }
    $global:fixtureServices[$Name]
}
function Stop-Service {
    [CmdletBinding()] param([string]$Name, [switch]$Force)
    (Get-Service $Name).Status='Stopped'; $global:serviceEvents.Add('stop:' + $Name)
}
function Start-Service {
    [CmdletBinding()] param([string]$Name)
    (Get-Service $Name).Status='Running'; $global:serviceEvents.Add('start:' + $Name)
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
    Assert ($FilePath.EndsWith('\msiexec.exe') -and $ArgumentList.Contains('/qn /norestart REBOOT=ReallySuppress') -and $WindowStyle -eq 'Hidden') 'MSI execution is silent and suppresses automatic Windows restarts'
    Assert ($ArgumentList.Contains('REINSTALL=ALL REINSTALLMODE=vomus')) 'same-product-code minor upgrades use MSI reinstall properties'
    if ($global:fixtureServices.Count -gt 0) { Assert (@($global:fixtureServices.Values | Where-Object Status -ne 'Stopped').Count -eq 0) 'all MSI-associated services stop before installation' }
    if ($global:expectCab) {
        $msiArgument = [regex]::Match($ArgumentList, '^/i "([^"]+)"').Groups[1].Value
        $cabArgument = Join-Path ([IO.Path]::GetDirectoryName($msiArgument)) 'data.cab'
        Assert (Test-Path -LiteralPath $cabArgument) ('an external MSI cabinet is present beside the staged MSI: ' + $cabArgument)
    }
    $global:installerCalls++
    if ($global:installerExit -in @(0,3010,1641)) { $global:installedVersion = '2.0.0' }
    [pscustomobject]@{ ExitCode=$global:installerExit }
}

function RunCase([string]$name, [string]$version, [bool]$local, [int]$exitCode, [bool]$expectedSuccess, [bool]$expectedInstall, [bool]$preparedFailure=$false, [string]$action='update') {
    $script:localMode=$local
    $global:fixtureKind=$package.Kind; $global:fixtureName=$package.ProductName; $global:fixtureProductCode=$package.ProductCode
    $global:expectCab=@($package.CompanionFiles).Count -gt 0
    $global:installedVersion=$version; $global:installerExit=$exitCode
    $global:installerCalls=0; $global:copies=0; $global:sessions=0; $global:closed=0; $global:adminChecks=0
    $base = Join-Path $fixture $name
    $global:currentBase=$base
    $request = @{ Action=$action; Host=$(if ($local) { 'LOCALHOST' } else { 'fixture.invalid' }); LocalExecution=$local; Username=''; Password='';
        UseSsl=$false; ForceUpdate=$true; RetryUpdate=$true; SkipZipTransfer=$false; PaymentZip=''; FiscalZip=''; ServerSuiteZip=''; RemoteInstallPath=$base; MsiPackage=$package; MsiPackages=@($package); CheckUpdateUpload=$true }
    $script:requestJson = $request | ConvertTo-Json -Depth 8 -Compress
    $original = [Console]::Out; $writer = [IO.StringWriter]::new()
    try { [Console]::SetOut($writer); & $worker } finally { [Console]::SetOut($original) }
    $messages = @($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $result = @($messages | Where-Object Event -eq 'result')[-1]
    Assert ($result.Success -eq $expectedSuccess) ("$name reports expected outcome: " + ($result | ConvertTo-Json -Depth 4 -Compress))
    Assert ($global:installerCalls -eq [int]$expectedInstall) "$name starts an installer only for an older installed product"
    if (-not $expectedInstall -and -not $preparedFailure) { Assert ($global:copies -eq 0 -and -not (Test-Path -LiteralPath $base)) "$name performs no transfer or extraction" }
    if ($preparedFailure) { Assert ($result.Error -like '*MSI-ProductName*' -and $global:copies -gt 0) "$name rejects inconsistent client MSI metadata before installation" }
    if ($action -eq 'check-upload-directory') { Assert (-not $result.UploadRequired -and @($messages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'MSI-Vorprüfung' }).Count -eq 1) "$name defers an unreadable MSI version to its component without blocking unrelated updates" }
    if ($local) { Assert ($global:sessions -eq 0 -and $global:closed -eq 0) "$name uses no WinRM calls" }
    else { Assert ($global:sessions -eq 1 -and $global:closed -eq 1) "$name closes the simulated remote session" }
    if ($expectedSuccess -and $expectedInstall) { Assert ($result.RebootRequired -eq ($exitCode -in @(3010,1641)) -and @($result.InstalledPrograms | Where-Object { $_.Kind -eq $package.Kind -and $_.Version -eq '2.0.0' }).Count -eq 1) "$name verifies the installed MSI version and preserves reboot-required status" }
    if (Test-Path -LiteralPath $base) { Assert (@(Get-ChildItem -LiteralPath $base -Directory -Filter '.novatouch-updater-msi-*').Count -eq 0) "$name removes its temporary MSI staging directory" }
}
try {
    RunCase 'local-absent' '' $true 0 $true $false
    RunCase 'local-current' '2.0.0' $true 0 $true $false
    RunCase 'local-newer' '3.0.0' $true 0 $true $false
    RunCase 'local-upgrade' '1.0.0' $true 0 $true $true
    RunCase 'local-reboot-required' '1.0.0' $true 3010 $true $true
    RunCase 'local-installer-failure' '1.0.0' $true 1603 $false $true
    RunCase 'remote-upgrade' '1.0.0' $false 0 $true $true
    RunCase 'remote-current-force' '2.0.0' $false 0 $true $false
    RunCase 'remote-unknown-preflight' 'unknown' $false 0 $true $false $false 'check-upload-directory'

    $package.PayloadPath = Join-Path ([IO.Path]::GetDirectoryName([string]$msis.ntc)) 'arbitrary-control-name.zip'
    $package.RelativeMsiPath = 'installer/Control.msi'
    $package.Sha256 = (Get-FileHash -LiteralPath $package.PayloadPath -Algorithm SHA256).Hash
    RunCase 'local-msi-in-zip' '1.0.0' $true 0 $true $true
    RunCase 'remote-msi-in-zip' '1.0.0' $false 0 $true $true

    $package.PayloadPath = [string]$msis.pos; $package.RelativeMsiPath = 'pos.msi'
    $package.Sha256 = (Get-FileHash -LiteralPath $package.PayloadPath -Algorithm SHA256).Hash
    RunCase 'wrong-msi-payload' '1.0.0' $true 0 $false $false $true

    $cab = Join-Path ([IO.Path]::GetDirectoryName([string]$msis.ntv)) 'data.cab'
    $package = [pscustomobject]@{ Kind='ntv'; ProductName='NovaTouch Voucher'; ProductVersion='2.0.0'; ProductCode=(FixtureProductCode ([string]$msis.ntv)); UpgradeCode='';
        PayloadPath=[string]$msis.ntv; RelativeMsiPath='ntv.msi'; Sha256=(Get-FileHash -LiteralPath $msis.ntv -Algorithm SHA256).Hash;
        CompanionFiles=@([pscustomobject]@{ LocalPath=$cab; RelativePath='data.cab'; Sha256=(Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash }) }
    RunCase 'local-external-cabinet' '1.0.0' $true 0 $true $true
    Assert ($global:copies -eq 2) 'both MSI and external cabinet are transferred and verified'
    RunCase 'remote-external-cabinet' '1.0.0' $false 0 $true $true

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
} finally {
    $checked = [IO.Path]::GetFullPath($fixture)
    if ($checked.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $checked -Recurse -Force }
}
