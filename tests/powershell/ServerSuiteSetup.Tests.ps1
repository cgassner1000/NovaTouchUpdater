param([string]$SuiteRepository = 'C:\development\novacom-software-gmbh\nt3-server-suite')
. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message }; Write-Host "PASS $message" }
$fixture = Join-Path $TestArtifactsRoot ('suite-setup-' + [guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory($fixture)
$script:fixture = $fixture
# Keep the setup-log directory and compiler temporary files inside this fixture.
$env:TEMP = Join-Path $fixture 'temp'
$env:TMP = $env:TEMP
[void][IO.Directory]::CreateDirectory($env:TEMP)
. ([scriptblock]::Create((Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'ServerSuiteSetup.ps1') -Raw -Encoding UTF8)))

# Compile the actual installer code against tiny fixtures, never execute it.
$iss = Get-Content -LiteralPath (Join-Path $SuiteRepository 'installer\nt3-server-suite-setup.iss') -Raw -Encoding UTF8
$installer = Join-Path $SuiteRepository 'installer'
$versionFile = Join-Path $SuiteRepository 'apps\nt3-server-suite\version.txt'
# Exercise the real default version path as well as CI's /DVersion overrides.
$iss = $iss.Replace('Local[0] = "..\apps\nt3-server-suite\version.txt",', 'Local[0] = "' + $versionFile + '",')
$sources = @{
    '..\apps\nt3-server-suite\*' = (Join-Path $fixture 'payload\apps\*')
    'python\portable_python\*' = (Join-Path $fixture 'payload\python\*')
    'python\portable_python\nt3_server_suite*.exe' = (Join-Path $fixture 'payload\python\nt3_server_suite*.exe')
    'configs\nt3_server_suite_config.yaml' = (Join-Path $installer 'configs\nt3_server_suite_config.yaml')
    'certs\*' = (Join-Path $installer 'certs\*')
}
foreach ($source in $sources.Keys) { $iss = $iss.Replace('Source: "' + $source + '"', 'Source: "' + $sources[$source] + '"') }
Assert ($iss -notmatch '(?im)^\s*#include\s') 'installer is self-contained without additional ISS sources'
foreach ($dir in @('payload\apps','payload\python')) { [void][IO.Directory]::CreateDirectory((Join-Path $fixture $dir)) }
[IO.File]::WriteAllText((Join-Path $fixture 'payload\apps\version.txt'), '2026.4.9999')
[IO.File]::WriteAllText((Join-Path $fixture 'payload\python\python.exe'), 'fixture, never run')
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'payload\python\nested'))
[IO.File]::WriteAllText((Join-Path $fixture 'payload\python\nt3_server_suite.exe'), 'fixture wrapper, never run')
[IO.File]::WriteAllText((Join-Path $fixture 'payload\python\nested\nt3_server_suite_123.exe'), 'nested fixture wrapper, never run')
[IO.File]::WriteAllText((Join-Path $fixture 'fixture.iss'), $iss, [Text.UTF8Encoding]::new($true))
$compiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
function CompileFixture([string]$name, [string]$versionOverride, [string]$expectedVersion) {
    $compilerArgs = @('/Qp', ('/O' + $fixture), ('/F' + $name))
    if ($versionOverride) { $compilerArgs += '/DVersion=' + $versionOverride }
    $compilerArgs += Join-Path $fixture 'fixture.iss'
    $ErrorActionPreference = 'Continue'
    & $compiler @compilerArgs > (Join-Path $fixture ($name + '.log')) 2>&1
    $compilerExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($compilerExit -ne 0) { throw (Get-Content (Join-Path $fixture ($name + '.log')) -Raw) }
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $fixture ($name + '.exe')))
    Assert (([string]$metadata.ProductVersion).Trim() -ceq $expectedVersion) "ProductVersion preserves '$expectedVersion' (override: '$versionOverride')"
    Assert (([string]$metadata.FileDescription).Trim() -ceq 'NovaTouch Server Suite Updater v1') 'protocol marker is preserved for every version format'
}
$defaultVersion = ([IO.File]::ReadAllLines($versionFile))[0]
CompileFixture 'default-from-version-file' '' $defaultVersion
CompileFixture 'three-part-ci' '2026.4.9999' '2026.4.9999'
CompileFixture 'four-part-ci' '2026.4.9999.123' '2026.4.9999.123'
CompileFixture 'date-format' '2025-03-17-0001' '2025-03-17-0001'
CompileFixture 'suffix-format' '2026.4.9999-beta' '2026.4.9999-beta'
$ErrorActionPreference = 'Continue'
& $compiler '/Qp' '/DVersion=2026.4.9999' ('/O' + $fixture) '/Fnt.server.suite-2026.4.9999' (Join-Path $fixture 'fixture.iss') > (Join-Path $fixture 'compile.log') 2>&1
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0) { throw (Get-Content (Join-Path $fixture 'compile.log') -Raw) }
$script:setup = Join-Path $fixture 'nt.server.suite-2026.4.9999.exe'
$script:hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
AssertServerSuiteSetup $setup $hash '2026.4.9999'
Assert ($true) 'Real Inno script compiles and exposes protocol/version metadata'
[IO.File]::WriteAllText((Join-Path $TestArtifactsRoot 'suite-setup-fixture.json'), ($setup | ConvertTo-Json))

function NewState {
    $script:trace = [Collections.Generic.List[string]]::new()
    $script:exitCodes = @{}; $script:stopFailure=''; $script:startFailure=''
    $script:records = @(); $script:states = @{}
    foreach ($name in @('nt3_server_suite','nt3_server_suite_ABC','nt3_server_suite_OFF')) {
        $root = Join-Path $fixture ('Instanz ' + $name)
        [void][IO.Directory]::CreateDirectory((Join-Path $root 'portable_python'))
        $script:records += [pscustomobject]@{ Name=$name; Kind='serversuite'; ExePath=(Join-Path $root 'portable_python\nt3_server_suite.exe'); InstallDir=(Join-Path $root 'apps\nt3-server-suite'); StartType=$(if ($name -like '*OFF') { 'Disabled' } else { 'Automatic' }); State='Running'; ProductVersion='1.0.0' }
        $state = [pscustomobject]@{ Name=$name; Status='Running' }
        if ($name -like '*OFF') { $state.Status='Stopped' }
        $state | Add-Member ScriptMethod WaitForStatus { param($state,$timeout) if ([string]$this.Status -ne [string]$state) { throw 'State mismatch' } }
        $script:states[$name] = $state
    }
    $script:eventLog = [pscustomobject]@{ Name='EventLog'; Status='Running' }
    $eventLog | Add-Member ScriptMethod WaitForStatus { param($state,$timeout) if ([string]$this.Status -ne [string]$state) { throw 'State mismatch' } }
    $script:copyCount=0
}
function Get-Service {
    [CmdletBinding()]param([string]$Name)
    if (-not $Name) { return @($script:states.Values) }
    if ($Name -eq 'EventLog') { return $script:eventLog }
    $script:states[$Name]
}
function Get-ItemProperty {
    [CmdletBinding()]param([string]$LiteralPath,[string]$Name)
    $serviceName = Split-Path -Path $LiteralPath -Leaf
    $record = @($script:records | Where-Object Name -eq $serviceName)[0]
    [pscustomobject]@{ ImagePath=('"' + $record.ExePath + '" --service-name "' + $record.Name + '"') }
}
function Stop-Service {
    [CmdletBinding()]param([string]$Name,[switch]$Force)
    $script:trace.Add('stop:' + $Name)
    if ($Name -eq $script:stopFailure) { throw 'simulated stop error' }
    if ($Name -eq 'EventLog') { Assert (@($states.Values | Where-Object Status -ne 'Stopped').Count -eq 0) 'all suite services stop before EventLog' }
    (Get-Service -Name $Name).Status = 'Stopped'
}
function Start-Service {
    [CmdletBinding()]param([string]$Name)
    $script:trace.Add('start:' + $Name)
    if ($Name -eq $script:startFailure) { throw 'simulated start error' }
    if ($Name -ne 'EventLog') { Assert ($script:eventLog.Status -eq 'Running') 'EventLog runs before suite restart' }
    (Get-Service -Name $Name).Status = 'Running'
}
function Start-Process {
    [CmdletBinding()]param([string]$FilePath,[string]$ArgumentList,[switch]$Wait,[switch]$PassThru,[string]$WindowStyle)
    Assert ($FilePath -eq $script:setup -or $FilePath -eq $script:copiedSetup) 'only verified fixture is passed to simulated setup'
    Assert ($Wait -and $PassThru -and $WindowStyle -eq 'Hidden') 'setup is synchronous and hidden'
    Assert ($script:eventLog.Status -eq 'Stopped' -and @($states.Values | Where-Object Status -ne 'Stopped').Count -eq 0) 'setup runs with all suite services and EventLog stopped'
    Assert ($ArgumentList -match '/VERYSILENT' -and $ArgumentList -match '/NORESTART ' -and $ArgumentList -match '/UPDATER=1') 'silent setup prevents reboot and opts into safe instance protocol'
    Assert ($ArgumentList -match '/SERVICENAME="([^"]+)"') 'exact service name is explicit'
    $name=$Matches[1]; $record=@($script:records | Where-Object Name -eq $name)[0]
    $root=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($record.InstallDir))
    Assert ($ArgumentList.Contains('/DIR="' + $root + '"')) 'each instance receives its own exact root, including spaces'
    $script:trace.Add('setup:' + $name)
    $code=0; if ($script:exitCodes.ContainsKey($name)) { $code=$script:exitCodes[$name] }
    [pscustomobject]@{ ExitCode=$code }
}
function RunInstall($selected = $script:records) {
    @(InstallServerSuiteSetup $script:setup $script:hash '2026.4.9999' (ConvertTo-Json -InputObject @($selected) -Compress) (ConvertTo-Json -InputObject @($script:records) -Compress) | Where-Object Event -eq 'setup-result')[0]
}
NewState
$result=RunInstall
Assert ($result.Errors.Count -eq 0 -and $result.InstalledNames.Count -eq 3) 'every installed instance gets its own setup invocation'
Assert ('start:nt3_server_suite_OFF' -notin $trace -and $states.nt3_server_suite_OFF.Status -eq 'Stopped') 'disabled service remains stopped'
NewState; $script:exitCodes.nt3_server_suite_ABC=4
$result=RunInstall
Assert ($result.Errors.Count -eq 1 -and $result.Errors[0] -match 'Exitcode 4' -and $result.Errors[0] -match 'Setup-Log:' -and $result.InstalledNames.Count -eq 2) 'failed instance keeps log and does not block other instances'
$script:exitCodes.Clear(); $script:trace.Clear()
$result=RunInstall @($script:records | Where-Object Name -eq 'nt3_server_suite_ABC')
Assert (@($trace | Where-Object { $_ -like 'setup:*' }).Count -eq 1 -and $result.InstalledNames[0] -eq 'nt3_server_suite_ABC') 'retry installs only failed instance while stopping all suite services for EventLog'
NewState; $script:exitCodes.nt3_server_suite=3010
$result=RunInstall
Assert ($result.RebootRequired -and $result.Errors.Count -eq 0) '3010 records pending reboot without restarting Windows'
NewState; $script:stopFailure='EventLog'
$result=RunInstall
Assert ($result.Errors.Count -gt 0 -and @($trace | Where-Object { $_ -like 'setup:*' }).Count -eq 0 -and $eventLog.Status -eq 'Running') 'failed EventLog stop prevents setup and restores services'
NewState; $script:startFailure='EventLog'
$result=RunInstall
Assert ($result.InstalledNames.Count -eq 0 -and @($trace | Where-Object { $_ -like 'start:nt3*' }).Count -eq 0) 'failed EventLog recovery leaves suite stopped and no instance checkpointed'
NewState; $script:startFailure='nt3_server_suite_ABC'
$result=RunInstall
Assert ($result.InstalledNames.Count -eq 2 -and $result.Errors.Count -eq 1) 'one failed service start preserves successful instance checkpoints'
NewState; $records[0].InstallDir = Join-Path $fixture 'wrong\apps\nt3-server-suite'
$rejected=$false; try { RunInstall | Out-Null } catch { $rejected=$true }
Assert ($rejected -and $trace.Count -eq 0) 'mismatching instance path rejected before any stop'
NewState
$rejected=$false; try { AssertServerSuiteSetup $setup ('0' * 64) '2026.4.9999' } catch { $rejected=$true }
Assert ($rejected -and $trace.Count -eq 0) 'SHA mismatch rejected before any service operation'
$rejected=$false; try { AssertServerSuiteSetup $PSHOME\powershell.exe (Get-FileHash $PSHOME\powershell.exe -Algorithm SHA256).Hash '2026.4.9999' } catch { $rejected=$true }
Assert ($rejected) 'EXE without protocol marker cannot be silently executed'

function Progress { param($phase,$message,$percent) }
function TestLocalSession($Session) { $Session -eq 'local' }
function InvokeWorkerCommand {
    [CmdletBinding()]param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@())
    & $ScriptBlock @ArgumentList
}
function CopyRemoteFileWithProgress($session,$source,$target,$label) {
    Assert ($target.StartsWith($script:fixture + '\', [StringComparison]::OrdinalIgnoreCase)) 'copy remains inside fixture'
    $script:copyCount++
    Copy-Item -LiteralPath $source -Destination $target -Force
}
function ReadInventory {
    foreach ($record in $script:records) {
        $copy=$record | Select-Object *
        $copy.ProductVersion='2026.4.9999'; $copy.State=[string]$script:states[$copy.Name].Status
        $copy
    }
}
function IsTargetVersion($service,$target) { $service.ProductVersion -eq $target }
NewState
$request=[pscustomobject]@{ ServerSuiteSetup=$setup; ServerSuiteSha256=$hash; ServerSuiteVersion='2026.4.9999'; SkipLocalPackageCopy=$true; SkipZipTransfer=$false; RemoteInstallPath='' }
$result=InvokeServerSuiteSetupUpdate $request 'local' $records $records
Assert ($result.Success -and $result.CompletedServiceNames.Count -eq 3 -and $copyCount -eq 0) 'local direct-source mode verifies every instance without copying setup'
NewState
$request.SkipLocalPackageCopy=$false; $request.RemoteInstallPath=Join-Path $fixture 'remote target'
$script:copiedSetup=Join-Path $request.RemoteInstallPath ([IO.Path]::GetFileName($setup))
$result=InvokeServerSuiteSetupUpdate $request 'remote' $records $records
Assert ($result.Success -and $copyCount -eq 1) 'remote setup is copied once for all instances, then revalidated on target'
NewState; $request.SkipZipTransfer=$true
$result=InvokeServerSuiteSetupUpdate $request 'remote' $records $records
Assert ($result.Success -and $copyCount -eq 0) 'remote no-upload mode uses matching existing setup'
NewState; $request.RemoteInstallPath=Join-Path $fixture 'missing target'
$rejected=$false; try { InvokeServerSuiteSetupUpdate $request 'remote' $records $records | Out-Null } catch { $rejected=$true }
Assert ($rejected -and $copyCount -eq 0 -and $trace.Count -eq 0) 'remote no-upload missing package fails before stops'

$workerAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $TestScriptsRoot 'RemoteWorker.ps1'), [ref]$null, [ref]$null)
foreach ($functionName in @('NewRemotePackageStage','RemoveRemotePackageStage')) {
    $definition=$workerAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName}, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
NewState
$configuredTarget=Join-Path $fixture 'configured-target'
[void][IO.Directory]::CreateDirectory($configuredTarget)
[IO.File]::WriteAllText((Join-Path $configuredTarget 'keep.txt'), 'preserve configured target')
$temporary=NewRemotePackageStage 'remote'
try {
    $request.RemoteInstallPath=$temporary.Path; $request.SkipZipTransfer=$false
    $script:copiedSetup=Join-Path $temporary.Path ([IO.Path]::GetFileName($setup))
    $result=InvokeServerSuiteSetupUpdate $request 'remote' $records $records
    Assert ($result.Success -and $copyCount -eq 1) 'temporary remote setup updates every instance from one temporary copy'
}
finally { RemoveRemotePackageStage 'remote' $temporary }
Assert (-not [IO.Directory]::Exists($temporary.Path)) 'temporary remote setup is removed after installation'
Assert (@(Get-ChildItem -LiteralPath $configuredTarget).Count -eq 1) 'temporary remote setup leaves the configured target unchanged'
Write-Host 'Server Suite setup tests passed. No real services, installers or remoting were executed.'
