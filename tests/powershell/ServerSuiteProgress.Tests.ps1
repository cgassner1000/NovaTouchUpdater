. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($value, [string]$message) { if (-not $value) { throw $message }; Write-Output ('PASS ' + $message) }
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('suite-progress-' + [guid]::NewGuid().ToString('N'))))
$prefix = [IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\'
if (-not $fixture.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture outside artifacts' }
[void][IO.Directory]::CreateDirectory($fixture)
$savedTemp = $env:TEMP; $savedTmp = $env:TMP
$env:TEMP = $fixture; $env:TMP = $fixture
. ([scriptblock]::Create((Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'ServerSuiteSetup.ps1') -Raw -Encoding UTF8)))
$setup = Join-Path $fixture 'dummy-setup.exe'
[IO.File]::WriteAllText($setup, 'Dummy setup, never executed')
function AssertServerSuiteSetup { param($Path,$Hash,$Version) if ($Path -ne $setup) { throw 'Unexpected setup' } }
function ReadInventory { $script:records }
function Stop-Service { throw 'Setup retains ownership of service control' }
function Start-Service { throw 'Setup retains ownership of service control' }
function Start-Sleep { param($Milliseconds) }
function Get-Service {
    [CmdletBinding()]param($Name)
    $state = [pscustomobject]@{Name=$Name;Status='Running';StartType='Automatic'}
    $state | Add-Member ScriptMethod WaitForStatus { param($status,$timeout) if($status -ne 'Running'){throw 'Unexpected service wait'} }
    $state
}
function Start-Process {
    [CmdletBinding()]param($FilePath,$ArgumentList,[switch]$PassThru,$WindowStyle)
    if ($FilePath -ne $setup -or $WindowStyle -ne 'Hidden' -or -not $PassThru) { throw 'Unexpected installer invocation' }
    if ($ArgumentList -notmatch '/SERVICENAME="([^"]+)"') { throw 'Missing instance selector' }
    $name=$Matches[1]
    if (-not $script:launches.ContainsKey($name)) { $script:launches[$name]=0 }
    $script:launches[$name]++
    $code=0
    if ($name -eq 'nt3_server_suite_B' -and $script:remainingFailures -gt 0) { $script:remainingFailures--; $code=1603 }
    $process=[pscustomobject]@{Handle=1;ExitCode=$code;Polls=1}
    $process | Add-Member ScriptProperty HasExited { if($this.Polls-- -gt 0){return $false}; return $true }
    $process | Add-Member ScriptMethod WaitForExit {}
    $process | Add-Member ScriptMethod Dispose {}
    $process
}
try {
    $script:records=@()
    foreach ($name in @('nt3_server_suite_A','nt3_server_suite_B','nt3_server_suite_C')) {
        $root=Join-Path $fixture $name
        foreach ($relative in @('portable_python\python.exe','apps\nt3-server-suite\src\apps\pos_service\script\pos_service_winservice.py')) {
            $path=Join-Path $root $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            [IO.File]::WriteAllText($path, 'Dummy start file, never executed')
        }
        $script:records += [pscustomobject]@{Name=$name;Kind='serversuite';RegistryInstallDir=$root;
            InstallDir=(Join-Path $root 'apps\nt3-server-suite');StartType='Automatic';State='Running';ProductVersion='2.0.0'}
    }
    $json=ConvertTo-Json -InputObject $script:records -Compress
    foreach ($case in @(@{Parallel=1;Failures=0},@{Parallel=3;Failures=0},@{Parallel=3;Failures=1},@{Parallel=3;Failures=3})) {
        $script:launches=@{}; $script:remainingFailures=$case.Failures
        $events=@(InstallServerSuiteSetup $setup ('a' * 64) '2.0.0' $json $json $case.Parallel)
        $progress=@($events | Where-Object Event -eq 'progress')
        $result=@($events | Where-Object Event -eq 'setup-result')[0]
        $previous=0
        foreach ($event in $progress) {
            if ($null -eq $event.ComponentProgressPercent -or $event.ComponentProgressPercent -lt $previous -or $event.ComponentProgressPercent -ge 100) {
                throw ('Invalid aggregate Suite progress: ' + ($event | ConvertTo-Json -Compress))
            }
            $previous=$event.ComponentProgressPercent
        }
        Assert ($progress[0].ComponentProgressPercent -eq 25 -and $progress[1].ComponentProgressPercent -gt 25) 'Suite setup starts visibly and aggregates every pending instance'
        $firstDone=@($progress | Where-Object Phase -eq 'Setup abgeschlossen')[0]
        Assert ($firstDone.ComponentProgressPercent -lt 95) 'finishing one instance cannot fill the bar while other instances remain'
        if ($case.Failures -lt 3) {
            Assert ($result.InstalledNames.Count -eq 3 -and $progress[-1].ComponentProgressPercent -eq 95 -and $result.Errors.Count -eq 0) 'all setup instances reach 95 percent and leave final version verification outstanding'
        } else {
            Assert ($result.Errors.Count -gt 0 -and $progress[-1].ComponentProgressPercent -lt 95 -and $result.AutomaticRetriesExhausted) 'a failed instance cannot count as completed progress after its retries'
        }
        Assert ($script:launches.nt3_server_suite_A -eq 1 -and $script:launches.nt3_server_suite_C -eq 1 -and
            $script:launches.nt3_server_suite_B -eq [Math]::Min(3,1+$case.Failures)) 'progress reporting preserves per-instance retries without reinstalling successful peers'
    }
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $TestScriptsRoot 'RemoteWorker.ps1'),[ref]$tokens,[ref]$errors)
    foreach($name in @('Emit','Progress')) {
        $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $oldOut=[Console]::Out; $writer=[IO.StringWriter]::new()
    try { [Console]::SetOut($writer); Progress 'Setup installieren' 'Fixture' $null 39 } finally { [Console]::SetOut($oldOut) }
    $message=$writer.ToString() | ConvertFrom-Json
    Assert ($message.ComponentProgressPercent -eq 39 -and $null -eq $message.ProgressPercent) 'the real JSON event protocol transports aggregate progress separately from step percentages'
    function TestLocalSession { param($Session) $true }
    function InvokeWorkerCommand { [CmdletBinding()]param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@()); & $ScriptBlock @ArgumentList }
    function IsTargetVersion { param($Service,$Version) $Service.ProductVersion -eq $Version }
    function CopyRemoteFileWithProgress { throw 'The fixture uses its original local source' }
    $request=[pscustomobject]@{ServerSuiteSetup=$setup;ServerSuiteSha256=(Get-FileHash -LiteralPath $setup).Hash;
        ServerSuiteVersion='2.0.0';SkipLocalPackageCopy=$true;SkipZipTransfer=$false;RemoteInstallPath=$fixture;Parallelism=3}
    $script:launches=@{}; $script:remainingFailures=0
    $writer=[IO.StringWriter]::new(); $oldOut=[Console]::Out
    try { [Console]::SetOut($writer); $result=InvokeServerSuiteSetupUpdate $request $null $script:records $script:records }
    finally { [Console]::SetOut($oldOut) }
    $messages=@($writer.ToString() -split '\r?\n' | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json })
    Assert ($result.Success -and @($messages | Where-Object { $null -ne $_.ComponentProgressPercent }).Count -gt 3) 'the real setup dispatch forwards all aggregate instance percentages into the worker event stream'
    $verification=@($messages | Where-Object Phase -eq 'Instanz geprüft')
    Assert ($verification.Count -eq 3 -and $verification[-1].ProgressPercent -eq 100 -and $null -eq $verification[-1].ComponentProgressPercent) ('final per-instance verification remains a separate stage after setup completion: ' + ($messages[-1] | ConvertTo-Json -Compress))
}
finally {
    $env:TEMP=$savedTemp; $env:TMP=$savedTmp
    if (-not $fixture.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fixture) -notmatch '^suite-progress-[a-f0-9]{32}$') { throw 'Invalid fixture cleanup' }
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction Stop
}
