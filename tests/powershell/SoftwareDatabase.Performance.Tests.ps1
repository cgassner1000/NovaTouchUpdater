param([string]$BaselineScript='', [string]$BaselineRemoteScript='', [ValidateRange(1,10)][int]$Iterations=5)
. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
foreach ($name in @('SoftwareDatabase','DatabaseStreaming','RemoteSoftwareDatabase')) {
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot ($name+'.ps1')))))
}
Add-Type -Path (Join-Path $PSScriptRoot '..\Shared\SoftwareDatabaseFake.cs') -ReferencedAssemblies System.Data, System.Core
$script:uploadMessages=[Collections.Generic.List[object]]::new()
function Progress { param($Phase,$Message,$Percent) $script:uploadMessages.Add([pscustomobject]@{Phase=$Phase;Message=$Message;Percent=$Percent}) }
function LoadBaselineFunction([string]$Path, [string]$Name) {
    $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($Path),[ref]$null,[ref]$errors)
    if ($errors.Count -gt 0) { throw 'Baseline contains syntax errors' }
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name},$true)
    if ($null -eq $definition) { throw ('Missing baseline function '+$Name) }
    return $definition.Extent.Text
}
function Median([double[]]$Values) {
    $ordered=@($Values | Sort-Object); $middle=[int][Math]::Floor($ordered.Count/2.0)
    if ($ordered.Count % 2) { return [Math]::Round($ordered[$middle],2) }
    return [Math]::Round(($ordered[$middle-1]+$ordered[$middle])/2.0,2)
}
function HashBytes([byte[]]$Bytes) {
    $hasher=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($Bytes)).Replace('-','') }
    finally { $hasher.Dispose() }
}
$fixture=[IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('database-performance-'+[guid]::NewGuid().ToString('N'))))
[void][IO.Directory]::CreateDirectory($fixture)
$path=Join-Path $fixture 'payload.bin'
$bytes=[byte[]]::new(8*1024*1024+123)
[Random]::new(42).NextBytes($bytes)
[IO.File]::WriteAllBytes($path,$bytes)
$expectedHash=HashBytes $bytes
$provider=[pscustomobject]@{ConnectionType=[SoftwareDatabaseFixture.Connection];BuilderType=[SoftwareDatabaseFixture.Builder];ObjectType=[SoftwareDatabaseFixture.DynamicObject];ClientType=[SoftwareDatabaseFixture.ClientType];BinaryStreamType=[SoftwareDatabaseFixture.BinaryStream]}
$definition=[pscustomobject]@{Server='127.0.0.1';Port=1972;User='fixture';Password='synthetic-secret';Ssl=$false}
$package=[pscustomobject]@{Kind='ntc';ProductName='NovaTouch Control';ProductVersion='2026.4.1';Length=$bytes.Length}
$resolved=[pscustomobject]@{Package=$package;Path=$path;Sha256=$expectedHash;Application='Nov.WaWi';StreamSource=$null;OnlyDestinations=$null}

function MeasureUpload([string]$Name) {
    $samples=[Collections.Generic.List[double]]::new()
    for ($iteration=0; $iteration -lt $Iterations; $iteration++) {
        [SoftwareDatabaseFixture.Database]::Entries.Clear(); [SoftwareDatabaseFixture.Database]::Events.Clear()
        [SoftwareDatabaseFixture.Database]::NativeWriteCalls=0; [SoftwareDatabaseFixture.Database]::LegacyWriteCalls=0; [SoftwareDatabaseFixture.Database]::MaximumWriteBytes=0
        [SoftwareDatabaseFixture.Database]::WriteDelayMilliseconds=2
        $connection=OpenSoftwareDatabase $provider $definition 'PERF'; $transaction=$connection.BeginTransaction()
        $script:uploadMessages.Clear()
        try {
            $timer=[Diagnostics.Stopwatch]::StartNew()
            WriteSoftwareDatabaseMsi $provider $connection $resolved $transaction 'PERF:1972/PERF'
            $transaction.Commit(); $timer.Stop(); $samples.Add($timer.Elapsed.TotalMilliseconds)
            Assert ([SoftwareDatabaseFixture.Database]::Entries.Count -eq 1 -and (HashBytes ([SoftwareDatabaseFixture.Database]::Entries[0].Bytes)) -eq $expectedHash) ($Name+': every binary byte including the partial last block is committed intact')
            if ($Name -eq 'After') {
                Assert ([SoftwareDatabaseFixture.Database]::NativeWriteCalls -eq 17 -and [SoftwareDatabaseFixture.Database]::LegacyWriteCalls -eq 0 -and
                    [SoftwareDatabaseFixture.Database]::MaximumWriteBytes -eq 512*1024) 'native writes bypass the 32000-byte binding buffer while keeping a bounded block size'
                Assert (@($script:uploadMessages | Where-Object { $_.Phase -eq 'zzzSoftware-Zeiten' -and $_.Message -match '17 Schreibaufrufe' }).Count -eq 1) 'the upload timing log records actual write calls'
            }
        } finally { $transaction.Dispose(); $connection.Dispose(); [SoftwareDatabaseFixture.Database]::WriteDelayMilliseconds=0 }
    }
    return [pscustomobject]@{Name=$Name;Milliseconds=(Median $samples.ToArray());Iterations=$Iterations;SamplesMilliseconds=$samples.ToArray();Bytes=$bytes.Length;
        DatabaseWrites=[SoftwareDatabaseFixture.Database]::NativeWriteCalls;MaximumWriteBytes=[SoftwareDatabaseFixture.Database]::MaximumWriteBytes}
}
$uploadBefore=$null
$optimizedWriter=${function:WriteSoftwareDatabaseMsi}.ToString()
try {
    if ($BaselineScript) { . ([scriptblock]::Create((LoadBaselineFunction $BaselineScript 'WriteSoftwareDatabaseMsi'))); $uploadBefore=MeasureUpload 'Before' }
} finally { . ([scriptblock]::Create('function WriteSoftwareDatabaseMsi {'+$optimizedWriter+'}')) }
$uploadAfter=MeasureUpload 'After'

# Exercise failures after real native writes. Old setups may only be replaced
# after the complete hash/length check and a successful namespace transaction.
foreach ($fault in @('Write','Checksum','Truncate')) {
    $destination=[pscustomobject]@{Key='PERF:1972/PERF';Definition=$definition;Namespace='PERF'}
    [SoftwareDatabaseFixture.Database]::Entries[0].Version='0.0.0'
    [SoftwareDatabaseFixture.Database]::FailStreamOperation=$(if ($fault -eq 'Write') { 'Write' } else { '' })
    [SoftwareDatabaseFixture.Database]::TruncateNativeWrites=$fault -eq 'Truncate'
    $resolved.Sha256=if ($fault -eq 'Checksum') { '0'*64 } else { $expectedHash }
    try {
        $outcome=InvokeSoftwareDatabaseNamespaceUpload $provider $destination @($resolved)
        Assert (-not $outcome.Success -and [SoftwareDatabaseFixture.Database]::Entries.Count -eq 1 -and
            [SoftwareDatabaseFixture.Database]::Entries[0].Version -eq '0.0.0' -and
            (HashBytes ([SoftwareDatabaseFixture.Database]::Entries[0].Bytes)) -eq $expectedHash -and
            [SoftwareDatabaseFixture.Database]::ActiveTransactions -eq 0 -and -not $outcome.Error.Contains('synthetic-secret')) ($fault+': the previous full setup survives rollback without credentials in the error')
    } finally { [SoftwareDatabaseFixture.Database]::FailStreamOperation=''; [SoftwareDatabaseFixture.Database]::TruncateNativeWrites=$false; $resolved.Sha256=$expectedHash }
}

# Model only the fixed WinRM call latency. Each payload frame is decoded and
# hashed through the production orchestration; no remote session is opened.
function InvokeWorkerCommand {
    [CmdletBinding()]param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@())
    if ($ScriptBlock.ToString().Contains('param([string]$Script,')) { return }
    if ($ArgumentList.Count -gt 2 -and $ArgumentList[2]) { return }
    if ($ArgumentList.Count -gt 1 -and $ArgumentList[1]) {
        return [pscustomobject]@{Event='result';Success=($script:relayWaves -eq 2 -and $script:relayOffset -eq 0);Changed=$true;Error='';DatabaseUploadedKinds=@('ntc')}
    }
    if ($ArgumentList.Count -ne 1) { throw 'A WinRM payload batch must remain one array argument' }
    $blocks=[string[]]$ArgumentList[0]
    if ($blocks.Count -gt 8) { throw 'Unbounded WinRM payload batch' }
    $script:relayCalls++; $script:relayMaxFrames=[Math]::Max($script:relayMaxFrames,$blocks.Count)
    [Threading.Thread]::Sleep(2)
    foreach ($block in $blocks) {
        $data=[Convert]::FromBase64String($block)
        if ($data.Length -gt 512*1024) { throw 'Unbounded WinRM payload frame' }
        [void]$script:relayHash.TransformBlock($data,0,$data.Length,$null,0); $script:relayOffset+=$data.Length
        if ($script:relayOffset -gt $bytes.Length) { throw 'MSI wave received too many bytes' }
        if ($script:relayOffset -eq $bytes.Length) {
            [void]$script:relayHash.TransformFinalBlock([byte[]]@(),0,0)
            if ([BitConverter]::ToString($script:relayHash.Hash).Replace('-','') -ne $expectedHash) { throw 'WinRM payload was corrupted' }
            $script:relayHash.Initialize(); $script:relayOffset=0; $script:relayWaves++
        }
    }
}
function MeasureRelay([string]$Name) {
    $samples=[Collections.Generic.List[double]]::new()
    for ($iteration=0; $iteration -lt $Iterations; $iteration++) {
        $script:relayCalls=0; $script:relayMaxFrames=0; $script:relayWaves=0; $script:relayOffset=0L
        $script:relayHash=[Security.Cryptography.SHA256]::Create()
        $plan=@(1..3 | ForEach-Object { [pscustomobject]@{Key=('APP'+$_);Error='';Needed=@('ntc')} })
        try {
            $timer=[Diagnostics.Stopwatch]::StartNew()
            $result=InvokeSoftwareDatabaseChildOperation $null 'unused-fixture-worker.ps1' @{Action='publish-msi-stream';MsiPackages=@($package);DatabaseUploadParallelism=2} @($resolved) $plan
            $timer.Stop(); $samples.Add($timer.Elapsed.TotalMilliseconds)
            Assert ($result.Success -and $script:relayWaves -eq 2) ($Name+': each parallel upload wave receives the complete original file')
            if ($Name -eq 'After') { Assert ($script:relayCalls -eq 6 -and $script:relayMaxFrames -eq 8) 'two waves use six bounded WinRM invocations instead of 34' }
        } finally { $script:relayHash.Dispose() }
    }
    return [pscustomobject]@{Name=$Name;Milliseconds=(Median $samples.ToArray());Iterations=$Iterations;SamplesMilliseconds=$samples.ToArray();BytesPerWave=$bytes.Length;Waves=2;WinRMCalls=$script:relayCalls;MaximumFramesPerCall=$script:relayMaxFrames}
}
$relayBefore=$null
$optimizedRelay=${function:InvokeSoftwareDatabaseChildOperation}.ToString()
try {
    if ($BaselineRemoteScript) { . ([scriptblock]::Create((LoadBaselineFunction $BaselineRemoteScript 'InvokeSoftwareDatabaseChildOperation'))); $relayBefore=MeasureRelay 'Before' }
} finally { . ([scriptblock]::Create('function InvokeSoftwareDatabaseChildOperation {'+$optimizedRelay+'}')) }
$relayAfter=MeasureRelay 'After'
$measurements=[pscustomobject]@{Database=@($uploadBefore,$uploadAfter | Where-Object { $null -ne $_ });WinRM=@($relayBefore,$relayAfter | Where-Object { $null -ne $_ });SimulatedCallLatencyMilliseconds=2}
$measurements | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $TestArtifactsRoot 'zzzsoftware-upload-performance.json') -Encoding UTF8
$measurements.Database | Select-Object Name,Milliseconds,DatabaseWrites,MaximumWriteBytes | Format-Table -AutoSize
$measurements.WinRM | Select-Object Name,Milliseconds,WinRMCalls,MaximumFramesPerCall | Format-Table -AutoSize
Write-Host 'Only a simulated provider and WinRM latency were measured. No real database, remote host, service or installer was used.'
