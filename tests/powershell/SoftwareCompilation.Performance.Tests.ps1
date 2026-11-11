param([string]$BaselineScript='', [ValidateRange(1,20)][int]$Iterations=5)
. (Join-Path $PSScriptRoot 'SoftwareCompilation.Tests.ps1')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function MeasureCompilationSample([string]$Name) {
    [CompilationFixture.State]::Reset()
    [CompilationFixture.State]::GeneratedClasses=1000; [CompilationFixture.State]::GeneratedRoutines=200
    [CompilationFixture.State]::CallDelayMilliseconds=2
    $runtime=InitializeSoftwareCompilationRuntime $bin
    $connection=[CompilationFixture.Connection]::new('BENCHMARK')
    [CompilationFixture.Cache]::Connection=$connection
    $script:messages.Clear()
    $script:progressEvents.Clear()
    try {
        $timer=[Diagnostics.Stopwatch]::StartNew()
        $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/BENCHMARK'
        $timer.Stop()
        Assert ($result.Imported -and [CompilationFixture.State]::CompiledItems.Count -eq 1200 -and -not [CompilationFixture.State]::Locked) ($Name+': all 1200 objects are compiled and the lock is released')
        if ($Name -eq 'After') {
            $updates=@($script:progressEvents | Where-Object Phase -eq 'Softwarepaket kompilieren')
            Assert ($updates.Count -eq 6 -and $updates[0].Message.Contains('Klasse 1 von 1000') -and $updates[-1].Message.Contains('Routine 200 von 200') -and
                $updates[0].Percent -eq 0 -and $updates[-1].Percent -eq 100 -and @($updates | Where-Object { $_.Percent -gt 0 -and $_.Percent -lt 100 }).Count -ge 3) ($Name+': XML progress remains visible across larger class and routine batches')
            for($index=1; $index -lt $updates.Count; $index++) {
                if($updates[$index].Percent -lt $updates[$index-1].Percent) { throw 'XML progress must not fall when switching from classes to routines' }
            }
            Assert (@($script:messages | Where-Object { $_ -match 'Softwarepaket-Zeiten:.*Compileraufrufe 3.*Fehlerteilungen 0.*Deployed-Wiederholungen 0' }).Count -eq 1) 'actual compiler call counts are retained in the timing log'
        }
        [pscustomobject]@{Name=$Name;Milliseconds=$timer.Elapsed.TotalMilliseconds;CompileCalls=[CompilationFixture.State]::CompileCalls;CurrencyCalls=[CompilationFixture.State]::CurrencyCalls;ProgressMessages=$script:messages.Count}
    } finally { [CompilationFixture.Cache]::Connection=$null; $connection.Dispose(); $runtime.Resolver.Dispose() }
}
function MeasureCompilation([string]$Name) {
    $samples=@(for($iteration=0; $iteration -lt $Iterations; $iteration++) { MeasureCompilationSample $Name })
    $ordered=@($samples.Milliseconds | Sort-Object); $middle=[int][Math]::Floor($ordered.Count/2.0)
    $median=if ($ordered.Count % 2) { $ordered[$middle] } else { ($ordered[$middle-1]+$ordered[$middle])/2.0 }
    [pscustomobject]@{Name=$Name;Milliseconds=[Math]::Round($median,2);Iterations=$Iterations;SamplesMilliseconds=@($samples.Milliseconds);
        CompileCalls=$samples[-1].CompileCalls;CurrencyCalls=$samples[-1].CurrencyCalls;ProgressMessages=$samples[-1].ProgressMessages}
}
function MeasureCurrentCompilation([string]$Name) {
    [CompilationFixture.State]::Reset(); [CompilationFixture.State]::AlreadyCurrent=$true; [CompilationFixture.State]::AllClassesCurrent=$true
    [CompilationFixture.State]::GeneratedClasses=1000; [CompilationFixture.State]::GeneratedRoutines=200
    [CompilationFixture.State]::CompileItemDelayMilliseconds=1
    $runtime=InitializeSoftwareCompilationRuntime $bin; $connection=[CompilationFixture.Connection]::new('CURRENT')
    [CompilationFixture.Cache]::Connection=$connection
    try {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/CURRENT'
        $clock.Stop()
        $compiled=[CompilationFixture.State]::CompiledItems.Count
        Assert ($result.Imported -and $compiled -eq $(if($Name -eq 'Before'){1200}else{200}) -and [CompilationFixture.State]::CurrencyCalls -eq 0) ($Name+': current classes follow NTC currency checks while all 200 routines still compile, without per-class queries')
        return [pscustomobject]@{Name=$Name;Milliseconds=[Math]::Round($clock.Elapsed.TotalMilliseconds,2);CompiledObjects=$compiled;CompilerCalls=[CompilationFixture.State]::CompileCalls}
    } finally {[CompilationFixture.Cache]::Connection=$null;$connection.Dispose();$runtime.Resolver.Dispose()}
}
$baseline=$null
$currentBefore=$null
if ($BaselineScript) {
    $optimized=@{}
    # Compare batching and fallback as well as the importer. Older baselines may
    # predate the separate batch helper.
    foreach($name in @('InvokeSoftwareCompilationMethod','ImportSoftwareCompilationResource','CompileSoftwareCompilationItems','CompileSoftwareCompilationBatch','CompleteSoftwareCompilationStatus')) { $optimized[$name]=(Get-Item ('Function:\'+$name)).ScriptBlock.ToString() }
    try {
        $parseErrors=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($BaselineScript),[ref]$null,[ref]$parseErrors)
        if($parseErrors.Count -gt 0) { throw 'Baseline script has parse errors' }
        foreach($name in $optimized.Keys) {
            $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
            if ($null -ne $definition) { . ([scriptblock]::Create($definition.Extent.Text)) }
            elseif ($name -eq 'ImportSoftwareCompilationResource') { throw 'Baseline importer is missing' }
        }
        $baseline=MeasureCompilation 'Before'
        $currentBefore=MeasureCurrentCompilation 'Before'
    } finally {
        foreach($name in $optimized.Keys) { . ([scriptblock]::Create('function '+$name+' {'+$optimized[$name]+'}')) }
    }
}
$after=MeasureCompilation 'After'
Assert ($after.CompileCalls -eq 3 -and $after.CurrencyCalls -eq 0 -and $after.ProgressMessages -le 8) '1000 classes and 200 routines need three bounded compiler calls, no individual currency queries and at most eight progress messages'
$measurements=@($baseline,$after | Where-Object { $null -ne $_ })
$measurements | Select-Object Name,Milliseconds,Iterations,CompileCalls,CurrencyCalls,ProgressMessages | Format-Table -AutoSize
$measurements | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TestArtifactsRoot 'software-compilation-performance.json') -Encoding UTF8
$currentAfter=MeasureCurrentCompilation 'After'
@($currentBefore,$currentAfter | Where-Object {$null -ne $_}) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TestArtifactsRoot 'software-compilation-current-performance.json') -Encoding UTF8

# Transfer binary data with short reads and a partial final block. Exercise the
# exact source writer and verify every byte, not just an XML prefix or length.
$payload=[byte[]]::new(8*1024*1024+123); [Random]::new(42).NextBytes($payload)
$payloadFile=Join-Path $fixture 'large-transfer.bin'; [IO.File]::WriteAllBytes($payloadFile,$payload)
$runtime=InitializeSoftwareCompilationRuntime $bin; $connection=[CompilationFixture.Connection]::new('TRANSFER')
$hash=[Security.Cryptography.SHA256]::Create()
try {
    $expectedHash=[Convert]::ToBase64String($hash.ComputeHash($payload))
    foreach($mode in @('Blocks','File','DeniedFile')) {
        [CompilationFixture.State]::Reset(); [CompilationFixture.State]::DenyFileCopy=$mode -eq 'DeniedFile'
        $source=[CompilationFixture.ShortReadStream]::new($payload);$binary=[CompilationFixture.Binary]::new($connection)
        try {
            $path=if($mode -eq 'Blocks'){''}else{$payloadFile}
            $stats=WriteSoftwareCompilationSource $runtime $connection $binary $source $path
            Assert ($stats.Bytes -eq $payload.Length -and [Convert]::ToBase64String($hash.ComputeHash($binary.ToArray())) -ceq $expectedHash -and
                [CompilationFixture.State]::LegacyWrites -eq 0) ($mode+': every source byte survives transfer and fallback after a partial failed copy')
            if($mode -eq 'File') {
                Assert ($stats.Writes -eq 0 -and [CompilationFixture.State]::FileCopies -eq 1 -and [CompilationFixture.State]::ClosedFiles -eq 1 -and [IO.File]::Exists($payloadFile)) 'server-side file copy uses no binary upload calls and retains the source file'
            } else {
                Assert ($stats.Writes -eq 17 -and [CompilationFixture.State]::MaximumWrite -eq 512*1024) ($mode+': short reads coalesce into 17 bounded writes instead of 274 small writes')
            }
        } finally {$binary.Close();$source.Dispose()}
    }
    foreach($fault in @('Write','Truncate')) {
        [CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailNativeWrite=$fault -eq 'Write';[CompilationFixture.State]::TruncateNativeWrite=$fault -eq 'Truncate'
        $source=[IO.MemoryStream]::new($payload,$false);$binary=[CompilationFixture.Binary]::new($connection);$rejected=$false
        try {try {$null=WriteSoftwareCompilationSource $runtime $connection $binary $source} catch {$rejected=$true}}
        finally {$binary.Close();$source.Dispose()}
        Assert $rejected ($fault+': failed or incomplete writes are rejected before XML import')
    }
} finally {$hash.Dispose();$connection.Dispose();$runtime.Resolver.Dispose()}

# Exercise large lists and failures, without fake latency.
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::GeneratedClasses=3000
$runtime=InitializeSoftwareCompilationRuntime $bin; $connection=[CompilationFixture.Connection]::new('LARGE')
[CompilationFixture.Cache]::Connection=$connection
try {
    $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/LARGE'
    Assert ($result.Imported -and [CompilationFixture.State]::CompiledItems.Count -eq 3000 -and [CompilationFixture.State]::CompileCalls -eq 6) '3000 classes compile in six progress batches without losing objects'
    [CompilationFixture.State]::Reset(); [CompilationFixture.Cache]::Connection=$connection
    $longNames=@(1..51 | ForEach-Object { ([string][char]0xC4)*600+$_.ToString()+'.cls' })
    $stats=CompileSoftwareCompilationItems $runtime $connection $runtime.Resources[0] $longNames 'IRIS:1972/LARGE' $true ([Collections.Generic.List[object]]::new()) @()
    Assert ([CompilationFixture.State]::CompiledItems.Count -eq 51 -and $stats.Calls -eq 3 -and [CompilationFixture.State]::CompileCalls -eq 3) 'UTF-8 compiler lists still split at the protocol byte limit before the object limit'

    [CompilationFixture.State]::Reset(); [CompilationFixture.Cache]::Connection=$connection
    [CompilationFixture.State]::GeneratedClasses=1000; [CompilationFixture.State]::GeneratedRoutines=200; [CompilationFixture.State]::Fallback=$true
    $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/LARGE'
    Assert ($result.CompilationErrors.Count -eq 0 -and [CompilationFixture.State]::CompiledItems.Count -eq 1200 -and [CompilationFixture.State]::CompileCalls -eq 6) 'deployed classes retry as whole lists with six calls instead of individual retries'

    [CompilationFixture.State]::Reset(); [CompilationFixture.Cache]::Connection=$connection
    [CompilationFixture.State]::GeneratedClasses=1000; [CompilationFixture.State]::GeneratedRoutines=200; [CompilationFixture.State]::FailItem='Fixture.Class00249.cls'
    $script:messages.Clear(); $script:progressEvents.Clear()
    $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/LARGE'
    Assert ($result.CompilationErrors.Count -eq 1 -and $result.CompilationErrors[0].Item -eq 'Fixture.Class00249' -and
        [CompilationFixture.State]::CompiledItems.Count -eq 1199 -and [CompilationFixture.State]::CompileCalls -lt 30 -and
        [CompilationFixture.State]::CurrencyCalls -eq 0 -and [CompilationFixture.State]::Versions.Count -eq 0 -and -not [CompilationFixture.State]::Locked) 'one failure among 1200 objects is isolated in fewer than 30 calls while every other object finishes without a success stamp'
    $updates=@($script:progressEvents | Where-Object Phase -eq 'Softwarepaket kompilieren')
    for($index=1; $index -lt $updates.Count; $index++) {
        if($updates[$index].Percent -lt $updates[$index-1].Percent) { throw 'Failure isolation must preserve monotonic XML progress' }
    }
    Assert ($updates[-1].Percent -eq 100 -and @($script:messages | Where-Object { $_ -match 'Softwarepaket-Zeiten:.*Fehlerteilungen [1-9]' }).Count -eq 1) 'failure splits retain XML progress and are counted in the timing log'

    [CompilationFixture.State]::Reset(); [CompilationFixture.Cache]::Connection=$connection
    [CompilationFixture.State]::GeneratedClasses=1000; [CompilationFixture.State]::FailItem='Fixture.Class00249.cls'
    [CompilationFixture.State]::CompileErrorMessage="ERROR #5030: An error occurred while compiling class 'Fixture.ExistingDescendant'"
    $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/LARGE'
    Assert ($result.CompilationErrors.Count -eq 1 -and $result.CompilationErrors[0].Item -eq 'Fixture.ExistingDescendant' -and
        [CompilationFixture.State]::CompiledItems.Count -eq 999 -and [CompilationFixture.State]::Versions.Count -eq 0) 'splitting a large batch preserves and deduplicates named descendant errors'

    [CompilationFixture.State]::Reset(); [CompilationFixture.Cache]::Connection=$connection
    [CompilationFixture.State]::GeneratedClasses=500; [CompilationFixture.State]::FailBatches=$true
    $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/LARGE'
    Assert ($result.CompilationErrors.Count -eq 0 -and [CompilationFixture.State]::CompiledItems.Count -eq 500 -and
        [CompilationFixture.State]::CompileCalls -lt 600 -and [CompilationFixture.State]::Versions.Count -eq 1) 'a provider rejecting every multi-item list falls back to individuals without a full binary call tree'
} finally { [CompilationFixture.Cache]::Connection=$null; $connection.Dispose(); $runtime.Resolver.Dispose() }
Write-Host 'Performance tests used a simulated 2 ms provider latency. No real database was opened.'
