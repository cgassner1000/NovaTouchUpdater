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
    try {
        $timer=[Diagnostics.Stopwatch]::StartNew()
        $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/BENCHMARK'
        $timer.Stop()
        Assert ($result.Imported -and [CompilationFixture.State]::CompiledItems.Count -eq 1200 -and -not [CompilationFixture.State]::Locked) ($Name+': all 1200 objects are compiled and the lock is released')
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
Assert ($after.CompileCalls -le 3 -and $after.CurrencyCalls -eq 0 -and $after.ProgressMessages -le 5) '1000 classes and 200 routines need at most three compile calls, no individual currency queries and bounded progress messages'
$measurements=@($baseline,$after | Where-Object { $null -ne $_ })
$measurements | Format-Table -AutoSize
$measurements | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TestArtifactsRoot 'software-compilation-performance.json') -Encoding UTF8

# Exercise a list larger than one legacy protocol string, without fake latency.
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::GeneratedClasses=3000
$runtime=InitializeSoftwareCompilationRuntime $bin; $connection=[CompilationFixture.Connection]::new('LARGE')
[CompilationFixture.Cache]::Connection=$connection
try {
    $result=ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/LARGE'
    Assert ($result.Imported -and [CompilationFixture.State]::CompiledItems.Count -eq 3000 -and [CompilationFixture.State]::CompileCalls -ge 2 -and
        [CompilationFixture.State]::CompileCalls -le 4) 'large class lists split at the provider byte limit without losing objects'
} finally { [CompilationFixture.Cache]::Connection=$null; $connection.Dispose(); $runtime.Resolver.Dispose() }
Write-Host 'Performance tests used a simulated 2 ms provider latency. No real database was opened.'
