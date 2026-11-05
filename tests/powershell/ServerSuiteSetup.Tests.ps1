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
    Assert (([string]$metadata.ProductName).Trim() -ceq 'NovaTouch Server Suite') 'package identity is preserved independently of description markers'
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
$v2Iss = $iss.Replace('VersionInfoDescription=NovaTouch Server Suite Updater v1', 'VersionInfoDescription=NovaTouch Server Suite Silent Update v2')
[IO.File]::WriteAllText((Join-Path $fixture 'compat-v2.iss'), $v2Iss, [Text.UTF8Encoding]::new($true))
& $compiler '/Qp' '/DVersion=2026.4.9999' ('/O' + $fixture) '/Fcompat-v2' (Join-Path $fixture 'compat-v2.iss') > (Join-Path $fixture 'compat-v2.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw (Get-Content (Join-Path $fixture 'compat-v2.log') -Raw) }
$script:v2Setup = Join-Path $fixture 'compat-v2.exe'
# Compile the historical markerless installer without changing its source repo.
$legacyIss = (& git -C $SuiteRepository show 'b9113012^:installer/nt3-server-suite-setup.iss') -join "`r`n"
if ($LASTEXITCODE -ne 0 -or $legacyIss -match 'VersionInfoDescription=NovaTouch Server Suite Updater') { throw 'Historical markerless installer missing' }
$legacyIss = $legacyIss.Replace('Local[0] = "..\apps\nt3-server-suite\version.txt",', 'Local[0] = "' + $versionFile + '",')
foreach ($source in $sources.Keys) { $legacyIss = $legacyIss.Replace('Source: "' + $source + '"', 'Source: "' + $sources[$source] + '"') }
[IO.File]::WriteAllText((Join-Path $fixture 'legacy.iss'), $legacyIss, [Text.UTF8Encoding]::new($true))
& $compiler '/Qp' '/DVersion=2026.4.9999' ('/O' + $fixture) '/Flegacy-no-marker' (Join-Path $fixture 'legacy.iss') > (Join-Path $fixture 'legacy-compile.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw (Get-Content (Join-Path $fixture 'legacy-compile.log') -Raw) }
$script:legacySetup = Join-Path $fixture 'legacy-no-marker.exe'
$script:approvedSetupHashes = @($setup,$v2Setup | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
foreach ($package in @($setup,$v2Setup)) {
    AssertServerSuiteSetup $package (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash '2026.4.9999'
}
Assert ($true) 'existing v1 and v2 installers pass package validation without changes to their source'
$rejected=$false; try { AssertServerSuiteSetup $legacySetup (Get-FileHash -LiteralPath $legacySetup -Algorithm SHA256).Hash '2026.4.9999' } catch { $rejected=$_.Exception.Message -match 'ab v1' -and $_.Exception.Message -match 'ZIP' }
Assert ($rejected) 'historical markerless setup is rejected with a warning requiring the matching ZIP'

function NewState([string[]]$Names=@('nt3_server_suite','nt3_server_suite_ABC','nt3_server_suite_OFF')) {
    $script:trace = [Collections.Generic.List[string]]::new()
    $script:exitCodes = @{}; $script:launchFailure=''; $script:stoppedInstance=''; $script:pendingInstance=''
    $script:waitedServices = [Collections.Generic.List[string]]::new()
    $script:activeSetups = [Collections.Generic.List[object]]::new()
    $script:setupProcesses = [Collections.Generic.List[object]]::new()
    $script:peakSetups = 0; $script:expectedLimit = 1
    $script:records = @(); $script:states = @{}
    foreach ($name in $Names) {
        $root = Join-Path $fixture ('Instanz ' + $name)
        [void][IO.Directory]::CreateDirectory((Join-Path $root 'portable_python'))
        $script:records += [pscustomobject]@{ Name=$name; Kind='serversuite'; ExePath=(Join-Path $root 'portable_python\nt3_server_suite.exe'); InstallDir=(Join-Path $root 'apps\nt3-server-suite'); StartType=$(if ($name -like '*OFF') { 'Disabled' } else { 'Automatic' }); State='Running'; ProductVersion='1.0.0' }
        $state = [pscustomobject]@{ Name=$name; Status='Running'; StartType=$script:records[-1].StartType }
        if ($name -like '*OFF') { $state.Status='Stopped' }
        $state | Add-Member ScriptMethod WaitForStatus {
            param($state,$timeout)
            $script:waitedServices.Add($this.Name)
            if ($this.Status -eq 'StartPending' -and $state -eq 'Running') { $this.Status='Running' }
            if ([string]$this.Status -ne [string]$state) { throw 'simulated status timeout' }
        }
        $script:states[$name] = $state
    }
    $script:eventLog = [pscustomobject]@{ Name='EventLog'; Status='Running' }
    $eventLog | Add-Member ScriptMethod WaitForStatus { param($state,$timeout) if ([string]$this.Status -ne [string]$state) { throw 'State mismatch' } }
    $script:copyCount=0; $script:staleInstance=''
}
function Get-Service {
    [CmdletBinding()]param([string]$Name)
    if (-not $Name) { return @($script:states.Values) }
    if ($Name -eq 'EventLog') { throw 'Windows EventLog must not be accessed by the installer' }
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
    throw 'The EXE updater must not stop services; Inno Setup owns the update'
}
function Start-Service {
    [CmdletBinding()]param([string]$Name)
    throw 'The EXE updater must not start services; Inno Setup owns the update'
}
function Get-Process { throw 'The EXE updater must not inspect installation processes' }
function StopServerSuiteProcesses { throw 'The EXE updater must not terminate installation processes' }
function Start-Sleep { param([int]$Milliseconds) }
function Start-Process {
    [CmdletBinding()]param([string]$FilePath,[string]$ArgumentList,[switch]$Wait,[switch]$PassThru,[string]$WindowStyle)
    Assert ($FilePath.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase) -and
        (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash -in $script:approvedSetupHashes) 'only verified fixture is passed to simulated setup'
    Assert (-not $Wait -and $PassThru -and $WindowStyle -eq 'Hidden') 'setup starts hidden with a process handle for bounded parallel execution'
    Assert ($ArgumentList -match '/VERYSILENT' -and $ArgumentList -match '/NORESTART ') 'silent setup prevents a Windows reboot'
    Assert ($ArgumentList -notmatch '/UPDATER|/NOCLOSEAPPLICATIONS|/NORESTARTAPPLICATIONS') 'normal Inno update flow is used without overriding application close/restart behavior'
    Assert ($ArgumentList -match '/SERVICENAME="([^"]+)"') 'exact service name is explicit'
    $name=$Matches[1]; $record=@($script:records | Where-Object Name -eq $name)[0]
    $root=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($record.InstallDir))
    Assert ($ArgumentList.Contains('/DIR="' + $root + '"')) 'each instance receives its own exact root, including spaces'
    $script:trace.Add('setup:' + $name)
    if ($script:launchFailure -eq $name) { throw 'simulated launch error' }
    Assert ($script:activeSetups.Count -lt $script:expectedLimit) 'setup concurrency stays within the selected limit'
    foreach ($other in $script:activeSetups) {
        Assert (-not ($root.Equals($other.Root, [StringComparison]::OrdinalIgnoreCase) -or
            $root.StartsWith($other.Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $other.Root.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase))) 'equal or nested installation directories never run concurrently'
    }
    $code=0; if ($script:exitCodes.ContainsKey($name)) { $code=$script:exitCodes[$name] }
    $process = [pscustomobject]@{ Name=$name; Root=$root; ExitCode=$code; Handle=1; Polls=2; Completed=$false; Disposed=$false }
    $process | Add-Member ScriptProperty HasExited {
        if ($this.Completed) { return $true }
        if (--$this.Polls -gt 0) { return $false }
        # Only the simulated installer changes its service after completion.
        $script:states[$this.Name].Status = if ($this.ExitCode -in @(0,3010) -and $script:stoppedInstance -ne $this.Name) { 'Running' } else { 'Stopped' }
        if ($this.ExitCode -in @(0,3010)) {
            @($script:records | Where-Object Name -eq $this.Name)[0].StartType='Automatic'
            $script:states[$this.Name].StartType='Automatic'
            if ($script:pendingInstance -eq $this.Name) { $script:states[$this.Name].Status='StartPending' }
        }
        $this.Completed=$true
        [void]$script:activeSetups.Remove($this)
        return $true
    }
    $process | Add-Member ScriptMethod WaitForExit { while (-not $this.HasExited) { } }
    $process | Add-Member ScriptMethod Dispose { $this.Disposed=$true }
    $script:activeSetups.Add($process); $script:setupProcesses.Add($process)
    $script:peakSetups=[Math]::Max($script:peakSetups, $script:activeSetups.Count)
    return $process
}
function RunInstall($selected = $script:records, [string]$Package = $script:setup, [int]$Parallelism = 1) {
    $script:expectedLimit=[Math]::Max(1,[Math]::Min(100,$Parallelism))
    @(InstallServerSuiteSetup $Package (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash '2026.4.9999' (ConvertTo-Json -InputObject @($selected) -Compress) (ConvertTo-Json -InputObject @($script:records) -Compress) $Parallelism | Where-Object Event -eq 'setup-result')[0]
}
NewState
$result=RunInstall
Assert ($result.Errors.Count -eq 0 -and $result.InstalledNames.Count -eq 3) 'every installed instance gets its own setup invocation'
Assert (@($trace | Where-Object { $_ -like 'setup:*' }).Count -eq 3 -and $eventLog.Status -eq 'Running') 'all three instance setups run without stopping EventLog'
Assert (@($trace | Where-Object { $_ -notlike 'setup:*' }).Count -eq 0) 'the wrapper performs no additional service or process actions'
Assert ($states.nt3_server_suite_OFF.Status -eq 'Running' -and $records[2].StartType -eq 'Automatic') 'service registration and startup configuration are determined by the installer'
Assert ($peakSetups -eq 1 -and @($setupProcesses | Where-Object { -not $_.Disposed }).Count -eq 0) 'parallelism one remains sequential and every process handle is released'
NewState
$result=RunInstall @($script:records | Where-Object Name -eq 'nt3_server_suite_ABC')
Assert ($trace.Count -eq 1 -and $trace[0] -eq 'setup:nt3_server_suite_ABC' -and $states.nt3_server_suite.Status -eq 'Running' -and
    $states.nt3_server_suite_OFF.Status -eq 'Stopped') 'updating one instance neither stops nor starts any other instance'
foreach ($package in @($setup,$v2Setup)) {
    NewState -Names @(1..10 | ForEach-Object { 'nt3_server_suite_TEST' + $_ })
    $result=RunInstall -Package $package -Parallelism 3
    Assert ($result.Errors.Count -eq 0 -and $result.InstalledNames.Count -eq 10 -and $trace.Count -eq 10 -and
        @($trace | Sort-Object -Unique).Count -eq 10 -and $peakSetups -eq 3 -and $activeSetups.Count -eq 0 -and
        @($setupProcesses | Where-Object { -not $_.Disposed }).Count -eq 0) ('ten instances update with exactly three concurrent setups, each once: ' + [IO.Path]::GetFileName($package))
}
NewState
$rejected=$false; try { RunInstall -Package $legacySetup | Out-Null } catch { $rejected=$_.Exception.Message -match 'ZIP' }
Assert ($rejected -and $trace.Count -eq 0) 'an older setup is blocked before any local installer invocation, even with distinct instance directories'
foreach ($package in @($setup,$v2Setup)) {
    NewState
    $records[1].InstallDir=$records[0].InstallDir; $records[1].ExePath=$records[0].ExePath
    $result=RunInstall -Package $package -Parallelism 3
    Assert ($result.Errors.Count -eq 0 -and $result.InstalledNames.Count -eq 3 -and $peakSetups -eq 2 -and
        $trace[0] -eq 'setup:nt3_server_suite' -and $trace[1] -eq 'setup:nt3_server_suite_OFF') ('shared directories are serialized while unrelated instances run in parallel: ' + [IO.Path]::GetFileName($package))
}
NewState
$nestedRoot=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($records[0].InstallDir))) 'nested'
$records[1].InstallDir=Join-Path $nestedRoot 'apps\nt3-server-suite'; $records[1].ExePath=Join-Path $nestedRoot 'portable_python\nt3_server_suite.exe'
$result=RunInstall -Parallelism 3
Assert ($result.Errors.Count -eq 0 -and $peakSetups -eq 2 -and $trace[1] -eq 'setup:nt3_server_suite_OFF') 'nested installation roots are also serialized without blocking an independent queued instance'
NewState; $script:exitCodes.nt3_server_suite_ABC=4; $script:exitCodes.nt3_server_suite=3010
$result=RunInstall -Parallelism 2
Assert ($result.Errors.Count -eq 1 -and $result.InstalledNames.Count -eq 2 -and $result.RebootRequired -and
    $result.Changed -and $peakSetups -eq 2 -and $activeSetups.Count -eq 0) 'parallel outcomes preserve per-instance failures, successful instances and reboot requirements'
NewState; $script:pendingInstance='nt3_server_suite_ABC'
$result=RunInstall @($records | Where-Object Name -eq 'nt3_server_suite_ABC')
Assert ($result.Errors.Count -eq 0 -and $states.nt3_server_suite_ABC.Status -eq 'Running' -and
    $script:waitedServices.Count -eq 1 -and $script:waitedServices[0] -eq 'nt3_server_suite_ABC') 'a transient StartPending state is waited out using only read-only service status checks'
NewState; $script:exitCodes.nt3_server_suite_ABC=4
$result=RunInstall
Assert ($result.Errors.Count -eq 1 -and $result.Errors[0] -match 'Exitcode 4' -and $result.Errors[0] -match 'Setup-Log:' -and $result.InstalledNames.Count -eq 2) 'failed instance keeps log and does not block other instances'
Assert ($states.nt3_server_suite_ABC.Status -eq 'Stopped') 'the wrapper does not attempt to repair a failed installer with service commands'
$script:exitCodes.Clear(); $script:trace.Clear()
$result=RunInstall @($script:records | Where-Object Name -eq 'nt3_server_suite_ABC')
Assert (@($trace | Where-Object { $_ -like 'setup:*' }).Count -eq 1 -and $result.InstalledNames[0] -eq 'nt3_server_suite_ABC') 'retry launches only the failed instance through the normal setup flow'
NewState; $script:exitCodes.nt3_server_suite=3010
$result=RunInstall
Assert ($result.RebootRequired -and $result.Errors.Count -eq 0) '3010 records pending reboot without restarting Windows'
NewState; $script:eventLog.Status='Stopped'
$result=RunInstall
Assert ($result.Errors.Count -eq 0 -and $result.InstalledNames.Count -eq 3 -and $eventLog.Status -eq 'Stopped') 'an initially stopped EventLog is left unchanged'
NewState; $script:launchFailure='nt3_server_suite_ABC'
$result=RunInstall
Assert ($result.Errors.Count -eq 1 -and $result.Errors[0] -match 'simulated launch error.*Setup-Log:' -and $result.InstalledNames.Count -eq 2) 'one setup launch failure preserves successful instance outcomes without service recovery actions'
NewState; $records[0].InstallDir = Join-Path $fixture 'wrong\apps\nt3-server-suite'
$rejected=$false; try { RunInstall | Out-Null } catch { $rejected=$true }
Assert ($rejected -and $trace.Count -eq 0) 'mismatching instance path rejected before any stop'
NewState
$rejected=$false; try { AssertServerSuiteSetup $setup ('0' * 64) '2026.4.9999' } catch { $rejected=$true }
Assert ($rejected -and $trace.Count -eq 0) 'SHA mismatch rejected before any service operation'
$rejected=$false; try { AssertServerSuiteSetup $PSHOME\powershell.exe (Get-FileHash $PSHOME\powershell.exe -Algorithm SHA256).Hash '2026.4.9999' } catch { $rejected=$true }
Assert ($rejected) 'an unrelated product cannot be silently executed as a Server Suite setup'

function Progress { param($phase,$message,$percent) }
function TestLocalSession($Session) { $Session -eq 'local' }
function InvokeWorkerCommand {
    [CmdletBinding()]param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@())
    if ($ScriptBlock.ToString().Contains('function InstallServerSuiteSetup')) {
        $script:expectedLimit=[Math]::Max(1,[Math]::Min(100,[int]$ArgumentList[5]))
        Assert ($ScriptBlock.ToString() -notmatch 'StopServerSuiteProcesses|Stop-Service|Start-Service|taskkill') 'remote setup script contains only validation and setup launch, without service/process mutation helpers'
    }
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
        if ($copy.Name -ne $script:staleInstance) { $copy.ProductVersion='2026.4.9999' }
        $copy.State=[string]$script:states[$copy.Name].Status
        $copy
    }
}
function IsTargetVersion($service,$target) { $service.ProductVersion -eq $target }
NewState
$request=[pscustomobject]@{ ServerSuiteSetup=$setup; ServerSuiteSha256=$hash; ServerSuiteVersion='2026.4.9999'; Parallelism=2; SkipLocalPackageCopy=$true; SkipZipTransfer=$false; RemoteInstallPath='' }
$result=InvokeServerSuiteSetupUpdate $request 'local' $records $records
Assert ($result.Success -and $result.CompletedServiceNames.Count -eq 3 -and $copyCount -eq 0 -and $peakSetups -eq 2) 'local direct-source mode uses requested parallelism and verifies every instance without copying setup'
NewState
$request.SkipLocalPackageCopy=$false; $request.RemoteInstallPath=Join-Path $fixture 'remote target'
$script:copiedSetup=Join-Path $request.RemoteInstallPath ([IO.Path]::GetFileName($setup))
$result=InvokeServerSuiteSetupUpdate $request 'remote' $records $records
Assert ($result.Success -and $copyCount -eq 1 -and $peakSetups -eq 2) 'remote setup uses requested parallelism after one copy and target validation'
Assert (@($trace | Where-Object { $_ -like 'setup:*' }).Count -eq 3) 'remote update invokes the silent setup separately for every installed instance'
NewState
$request.ServerSuiteSetup=$legacySetup; $request.ServerSuiteSha256=(Get-FileHash -LiteralPath $legacySetup -Algorithm SHA256).Hash
$rejected=$false; try { InvokeServerSuiteSetupUpdate $request 'remote' $records $records | Out-Null } catch { $rejected=$_.Exception.Message -match 'ZIP' }
Assert ($rejected -and $copyCount -eq 0 -and $trace.Count -eq 0) 'an older setup is blocked with a ZIP warning before remote upload or installer invocation'
$request.ServerSuiteSetup=$setup; $request.ServerSuiteSha256=$hash
NewState; $script:staleInstance='nt3_server_suite_ABC'
$result=InvokeServerSuiteSetupUpdate $request 'remote' $records $records
Assert (-not $result.Success -and $result.CompletedServiceNames.Count -eq 2 -and $result.Error -match 'Instanzversion nicht best.tigt: nt3_server_suite_ABC') 'setup exit zero cannot mark an unchanged remote instance as updated'
NewState; $script:stoppedInstance='nt3_server_suite_ABC'
$result=InvokeServerSuiteSetupUpdate $request 'remote' $records $records
Assert (-not $result.Success -and $result.Changed -and $result.CompletedServiceNames.Count -eq 2 -and $result.Error -match 'Dienst l.uft nicht: nt3_server_suite_ABC') 'a stopped service after setup is reported by read-only verification, retaining partial changes without a PowerShell start attempt'
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
