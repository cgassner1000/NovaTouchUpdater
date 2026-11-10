param([string]$BaselineScript='')
. (Join-Path $PSScriptRoot 'SoftwareCompilation.Tests.ps1')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function MeasureCompilation([string]$Name) {
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
            Assert ($updates[0].Message.Contains('Klasse 1 von 1000') -and $updates[-1].Message.Contains('Routine 200 von 200') -and
                $updates[0].Percent -eq 0 -and $updates[-1].Percent -eq 100 -and @($updates | Where-Object { $_.Percent -gt 0 -and $_.Percent -lt 100 }).Count -gt 10) ($Name+': large XML files report real intermediate progress')
            for($index=1; $index -lt $updates.Count; $index++) {
                if($updates[$index].Percent -lt $updates[$index-1].Percent) { throw 'XML progress must not fall when switching from classes to routines' }
            }
        }
        [pscustomobject]@{Name=$Name;Milliseconds=$timer.ElapsedMilliseconds;CompileCalls=[CompilationFixture.State]::CompileCalls;CurrencyCalls=[CompilationFixture.State]::CurrencyCalls;ProgressMessages=$script:messages.Count}
    } finally { [CompilationFixture.Cache]::Connection=$null; $connection.Dispose(); $runtime.Resolver.Dispose() }
}
$baseline=$null
if ($BaselineScript) {
    $optimized=@{}
    foreach($name in @('InvokeSoftwareCompilationMethod','ImportSoftwareCompilationResource')) { $optimized[$name]=(Get-Item ('Function:\'+$name)).ScriptBlock.ToString() }
    try {
        $parseErrors=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($BaselineScript),[ref]$null,[ref]$parseErrors)
        if($parseErrors.Count -gt 0) { throw 'Baseline script has parse errors' }
        foreach($name in $optimized.Keys) {
            $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
            . ([scriptblock]::Create($definition.Extent.Text))
        }
        $baseline=MeasureCompilation 'Before'
    } finally {
        foreach($name in $optimized.Keys) { . ([scriptblock]::Create('function '+$name+' {'+$optimized[$name]+'}')) }
    }
}
$after=MeasureCompilation 'After'
Assert ($after.CompileCalls -le 24 -and $after.CurrencyCalls -eq 0 -and $after.ProgressMessages -le 50) '1000 classes and 200 routines retain batched compilation with at most 24 calls, no individual currency queries and bounded progress messages'
$measurements=@($baseline,$after | Where-Object { $null -ne $_ })
$measurements | Format-Table -AutoSize
$measurements | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TestArtifactsRoot 'software-compilation-performance.json') -Encoding UTF8

# Exercise many progress batches, without fake latency.
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::GeneratedClasses=3000
$runtime=InitializeSoftwareCompilationRuntime $bin; $connection=[CompilationFixture.Connection]::new('LARGE')
[CompilationFixture.Cache]::Connection=$connection
try {
    $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/LARGE'
    Assert ($result.Imported -and [CompilationFixture.State]::CompiledItems.Count -eq 3000 -and [CompilationFixture.State]::CompileCalls -eq 60) 'large class lists report progress every 50 objects without losing objects'
    [CompilationFixture.State]::Reset()
    $longNames=@(1..51 | ForEach-Object { ([string][char]0xC4)*600+$_.ToString()+'.cls' })
    CompileSoftwareCompilationItems $runtime $connection $runtime.Resources[0] $longNames 'IRIS:1972/LARGE' $true ([Collections.Generic.List[object]]::new()) @()
    Assert ([CompilationFixture.State]::CompiledItems.Count -eq 51 -and [CompilationFixture.State]::CompileCalls -eq 3) 'UTF-8 compiler lists still split at the protocol byte limit before the progress batch limit'
} finally { [CompilationFixture.Cache]::Connection=$null; $connection.Dispose(); $runtime.Resolver.Dispose() }
Write-Host 'Performance tests used a simulated 2 ms provider latency. No real database was opened.'
