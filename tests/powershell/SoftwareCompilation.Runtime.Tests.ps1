. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$repository=Split-Path (Split-Path $PSScriptRoot)
$references=Join-Path $repository 'References'
if (-not (Test-Path -LiteralPath (Join-Path $references 'Aprima.Sys.dll'))) { Write-Host 'SKIP: optional Aprima reference DLLs are not available for metadata inspection.'; exit 0 }
$fixtureRoot=Join-Path $TestArtifactsRoot ('compile-runtime-'+[guid]::NewGuid().ToString('N').Substring(0,8))
$fixture=Join-Path $fixtureRoot 'bin'; [void][IO.Directory]::CreateDirectory($fixture)
foreach($name in @('Aprima.Sys.dll','Aprima.SysDB.dll')) { Copy-Item -LiteralPath (Join-Path $references $name) -Destination (Join-Path $fixture $name) }
$packages=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
Copy-Item -LiteralPath (Join-Path $packages 'intersystems.data.irisclient\2.7.0\lib\net462\InterSystems.Data.IRISClient.dll') -Destination $fixture
Copy-Item -LiteralPath (Join-Path $packages 'intersystems.data.bindings\2.6.3\lib\net462\InterSystems.Data.Bindings.dll') -Destination $fixture
$source=Join-Path $fixture 'Data.cs'; [IO.File]::WriteAllText($source,'public class FixtureData { }')
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$compilerArgs=@('/nologo','/target:library','/platform:x86',('/out:'+(Join-Path $fixture 'Nov.NT.Daten.dll')),$source)
foreach($name in @('Novacom-NT-Daten.xml','Novacom-NT-BOH.xml','Novacom-NT-POS.xml','Novacom-NT-POS-API.xml','Novacom-NT-QRY.xml')) {
    $xml=Join-Path $fixture $name
    [IO.File]::WriteAllText($xml,"<?xml version=`"1.0`"?>`n<Export ts=`"2026-11-05 10:00:00`"><Class name=`"Fixture.Only`"/></Export>")
    $compilerArgs+='/resource:'+ $xml + ',Nov.NT.Daten.'+$name
}
& $compiler @compilerArgs
if($LASTEXITCODE -ne 0) { throw 'Could not build the resource-only fixture' }
$systemXml=Join-Path $fixture 'Novacom-System.xml'
[IO.File]::WriteAllText($systemXml,"<?xml version=`"1.0`"?>`n<Export ts=`"2026-11-05 10:00:00`"><Class name=`"Fixture.System`"/></Export>")
& $compiler '/nologo' '/target:library' '/platform:x86' ('/out:'+(Join-Path $fixture 'Nov.SysDB.dll')) $source ('/resource:'+$systemXml+',Nov.SysDB.Novacom-System.xml')
if($LASTEXITCODE -ne 0) { throw 'Could not build the Novacom system resource fixture' }
$dataDirectory=Join-Path $fixtureRoot 'Daten'; [void][IO.Directory]::CreateDirectory($dataDirectory)
foreach($name in @('Aprima-Systemparameter.xml','bls-v301-sp1.xml')) {
    [IO.File]::WriteAllText((Join-Path $dataDirectory $name),"<?xml version=`"1.0`"?>`n<Export ts=`"2026-11-05 10:00:00`"><Global><Node><Sub>^FixtureOnly</Sub></Node></Global></Export>")
}
$child=Join-Path $fixture 'Inspect.ps1'
[IO.File]::WriteAllText($child,@'
param($Scripts,$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
foreach($name in @('SoftwareDatabase','SoftwareCompilation')) { . ([scriptblock]::Create((Get-Content (Join-Path $Scripts ($name+'.ps1')) -Raw -Encoding UTF8))) }
function Progress { param($phase,$message,$percent) }
if([Environment]::Is64BitProcess) { throw 'Expected a 32-bit metadata inspection process' }
$runtime=InitializeSoftwareCompilationRuntime $Directory
try {
    if($runtime.Resources.Count -ne 11 -or $runtime.Resources[0].Name -cne 'Aprima-Datentypen.xml' -or $runtime.Resources[0].Table -cne 'Aprima-Datentypen.xml' -or
        $runtime.Resources[0].Manifest -cne 'AP.SysDB.Aprima-Datentypen.xml' -or $runtime.Resources[6].Name -cne 'Novacom-NT-Daten.xml' -or
        @($runtime.Resources | Where-Object { $_.Kind -eq 'Globals' }).Count -ne 3) { throw 'Incorrect startup resource preflight or order' }
    if($runtime.Resources[3].Application -cne 'Nov.WaWi' -or $runtime.Resources[5].Table -cne '..\Daten\bls-v301-sp1.xml') { throw 'Incorrect file version identity' }
    if($runtime.Cache.GetField('Connection').FieldType -ne $runtime.Provider.ConnectionType) { throw 'Installed provider and Aprima connection types do not match' }
    if($runtime.Provider.BinaryStreamType.GetMethod('Rewind').ReturnType.FullName -cne 'InterSystems.Data.IRISTypes.IRISStatus') { throw 'Rewind must return a checked IRISStatus, matching the regression fixture' }
    if($runtime.Update.GetMethod('CacheKlassenUpdate').GetParameters().Count -ne 12) { throw 'Unexpected Aprima public update contract' }
    $globalMethod=$runtime.GlobalImport.GetMethod('LoadStream')
    if($globalMethod.ReturnType -ne [string] -or $globalMethod.GetParameters().Count -ne 2 -or
        $globalMethod.GetParameters()[1].ParameterType -ne $runtime.Provider.BinaryStreamType) { throw 'Global streaming ABI does not match the installed provider' }
    foreach($method in @('FileWriteImportXML','OBJLoad','OBJIsUpToDate','OBJCompileList','SQLPurge','FileDelete')) {
        if($null -eq $runtime.Proxy.GetMethod($method)) { throw ('Missing Aprima DB proxy: '+$method) }
    }
    'PASS actual Aprima DLLs load in x86; eleven startup sources, native global-stream ABI, by-reference API and connection types match. No database was opened.'
} finally { $runtime.Resolver.Dispose() }
$bls=Join-Path (Split-Path $Directory) 'Daten\bls-v301-sp1.xml'
Remove-Item -LiteralPath $bls
$rejected=$false
try { $unexpected=InitializeSoftwareCompilationRuntime $Directory; $unexpected.Resolver.Dispose() } catch { $rejected=$_.Exception.Message -match 'bls-v301-sp1.xml' }
if(-not $rejected){throw 'Missing sibling data file was not rejected during runtime preflight'}
'PASS real x86 runtime rejects missing external XML before opening a database.'
'@, [Text.UTF8Encoding]::new($true))
$executable=Join-Path $env:SystemRoot $(if([Environment]::Is64BitOperatingSystem) { 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' } else { 'System32\WindowsPowerShell\v1.0\powershell.exe' })
& $executable -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $child -Scripts $TestScriptsRoot -Directory $fixture
exit $LASTEXITCODE
