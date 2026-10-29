. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('database-provider-' + [guid]::NewGuid().ToString('N'))))
Assert ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'provider fixtures stay within test artifacts'
[void][IO.Directory]::CreateDirectory($fixture)
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'SoftwareDatabase.ps1'))))
$script:providerProgress = [Collections.Generic.List[string]]::new()
function Progress { param($Phase,$Message,$Percent) $script:providerProgress.Add([string]$Message) }
function CompileFixture([string]$Path, [string]$Source, [string[]]$References = @()) {
    $compiler = New-Object Microsoft.CSharp.CSharpCodeProvider
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $false; $parameters.OutputAssembly = $Path
    $parameters.TempFiles = [CodeDom.Compiler.TempFileCollection]::new($fixture, $false)
    foreach ($reference in $References) { [void]$parameters.ReferencedAssemblies.Add($reference) }
    try {
        $result = $compiler.CompileAssemblyFromSource($parameters, $Source)
        if ($result.Errors.HasErrors) { throw ($result.Errors | Out-String) }
    } finally { $parameters.TempFiles.Delete(); $compiler.Dispose() }
}
try {
    $bin = Join-Path $fixture 'dev\dotnet\bin'
    foreach ($name in @('v4.8', 'v4.7', 'v4.6.2', 'v4.0.30319', 'net8.0', 'Core21')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $bin $name))
    }
    $valid = Join-Path $bin 'v4.6.2'; $missing = Join-Path $bin 'v4.8'; $corrupt = Join-Path $bin 'v4.7'
    $client = Join-Path $valid 'InterSystems.Data.IRISClient.dll'
    $binding = Join-Path $valid 'InterSystems.Data.Bindings.dll'
    CompileFixture $client @'
using System.Reflection;
[assembly: AssemblyVersion("99.0.0.0")]
namespace InterSystems.Data.IRISClient {
    public class ClientBase { }
    public sealed class IRISConnectionStringBuilder { }
}
'@
    CompileFixture $binding @'
using System.Reflection;
[assembly: AssemblyVersion("99.0.0.0")]
namespace InterSystems.Data.IRISClient { public sealed class BindingsConnection : ClientBase { } }
namespace InterSystems.Data.IRISTypes {
    public sealed class IRISObject { }
    public enum ClientTypeId { tStatus, tString, tInt, tBool, tObject }
    public sealed class IRISBinaryStream { }
}
'@ @($client)
    Copy-Item -LiteralPath $client -Destination (Join-Path $missing 'InterSystems.Data.IRISClient.dll')
    foreach ($name in @('InterSystems.Data.IRISClient.dll', 'InterSystems.Data.Bindings.dll')) {
        [IO.File]::WriteAllText((Join-Path $corrupt $name), 'Invalid assembly fixture, never executed')
    }
    $directories = @(GetSoftwareDatabaseProviderDirectories $fixture)
    Assert ((Split-Path $directories[0] -Leaf) -eq 'v4.8' -and $directories -contains $valid -and
        @($directories | Where-Object { $_ -match 'net8\.0|Core21' }).Count -eq 0) 'discovery orders Framework versions newest first and excludes .NET/Core runtime assemblies'
    $provider = InitializeSoftwareDatabaseProvider $directories
    Assert ($provider.BuilderType.Assembly.Location -eq $client -and $provider.ConnectionType.Assembly.Location -eq $binding -and
        $provider.BinaryStreamType.FullName -eq 'InterSystems.Data.IRISTypes.IRISBinaryStream') 'the real CLR loads the client and dependent object bindings from the discovered v4.6.2 directory'
    Assert ($script:providerProgress.Exists([Predicate[string]]{ param($line) $line.Contains('v4.8') -and $line.Contains('Bindings.dll') }) -and
        $script:providerProgress.Exists([Predicate[string]]{ param($line) $line.Contains('v4.7') -and $line.Contains('HRESULT') })) 'missing bindings and corrupt DLLs are diagnosed and do not prevent fallback to a valid provider'
    $message = ''
    try { InitializeSoftwareDatabaseProvider @($missing,$corrupt) | Out-Null } catch { $message = $_.Exception.Message }
    Assert ($message.Contains($missing) -and $message.Contains($corrupt) -and $message.Contains('FileNotFoundException') -and $message.Contains('BadImageFormatException') -and $message.Contains('HRESULT')) 'when every candidate fails, the final error contains every directory, original exception types and HRESULTs'
    $message = ''
    $absent = Join-Path $fixture 'absent-provider'
    try { InitializeSoftwareDatabaseProvider @($absent) | Out-Null } catch { $message = $_.Exception.Message }
    Assert ($message.Contains($absent) -and $message.Contains('Keine Client-DLL')) 'an installation without provider DLLs reports the searched paths'
    Write-Host 'PASS provider loading tests completed without database connections, installed application DLLs or service changes'
} finally {
    if ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        # Assemblies loaded by the real CLR can remain locked until this test
        # process exits. Keep those small fixture DLLs inside test artifacts.
        Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
