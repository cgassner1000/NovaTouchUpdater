. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert($Condition,[string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
$source=[IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'RemoteWorker.ps1'))
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
$replacements=@{
    InvokeWorkerCommand=@'
function InvokeWorkerCommand {
    [CmdletBinding()]param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@())
    Assert ($Session.Id -eq 123 -and $script:action -eq 'database-inventory') 'inventory executes through the authenticated target session'
    $script:inventoryCalls++
    [pscustomobject]@{ Name='REMOTE-IRIS';Directory='D:\IRIS';ConfigurationFile='D:\IRIS\iris.cpf';Version='2026.1';Namespaces=@('APP','USER');Error='';SuperServerPort=51774;ConnectionError='' }
}
'@
    ReadSoftwareInventory='function ReadSoftwareInventory { throw "Database Check must not read uninstall inventory or run installation actions" }'
}
foreach ($name in @($replacements.Keys | Sort-Object { $candidate=$_; ($ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $candidate},$true)).Extent.StartOffset } -Descending)) {
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    $source=$source.Remove($node.Extent.StartOffset,$node.Extent.EndOffset-$node.Extent.StartOffset).Insert($node.Extent.StartOffset,$replacements[$name])
}
$source=$source.Replace('$requestLine = [Console]::In.ReadLine()','$requestLine = $script:requestJson').Replace('exit 0','return').Replace('exit 1','return')
$worker=[scriptblock]::Create($source)
$script:sessions=0;$script:removed=0;$script:inventoryCalls=0;$script:published=0
function New-PSSessionOption { param($OpenTimeout,$OperationTimeout) @{} }
function New-PSSession {
    param($ComputerName,$Port,$SessionOption,[switch]$UseSSL,$Credential,$ErrorAction)
    Assert ($ComputerName -eq 'fixture.invalid' -and $Port -eq 6001 -and $UseSSL -and $Credential.UserName -eq 'fixture-user') 'database operations retain WinRM host, HTTPS port and configured credentials'
    $script:sessions++;[pscustomobject]@{Id=123}
}
function Remove-PSSession { param($Session,$ErrorAction) Assert ($Session.Id -eq 123) 'database remoting session is cleaned up';$script:removed++ }
function ReadSoftwareIrisRegistrations { throw 'target discovery is supplied by a mock remoting invocation' }
function ReadSoftwareIrisInventory { throw 'target discovery is supplied by a mock remoting invocation' }
function PublishRemoteSoftwarePackages($Request,$Session,$WorkerPath) {
    Assert ($Session.Id -eq 123 -and $Request.SkipLocalPackageCopy -eq $script:direct -and $Request.IrisUseTls -and $Request.RemoteInstallPath -eq 'C:\install\Novacom') 'remote database dispatch preserves direct streaming and TLS settings and supplies the default target path'
    $script:published++
    $outcome=[pscustomobject]@{Event='result';Success=$true;Changed=$true;Skipped=$false;Services=@();DatabaseUploadedKinds=@('ntc');
        Message='MSI-Daten über WinRM gestreamt und in zzzSoftware geprüft/gespeichert.';PSComputerName='fixture.invalid';RunspaceId=[guid]::NewGuid();PSShowComputerName=$true}
    # Match real WinRM transport instead of returning a local hashtable. The
    # final Emit call must accept the deserialized result after a committed upload.
    $transported=[Management.Automation.PSSerializer]::Deserialize([Management.Automation.PSSerializer]::Serialize($outcome))
    Assert ($transported.PSObject.TypeNames[0].StartsWith('Deserialized.')) 'the upload fixture crosses the real PowerShell serialization boundary'
    $transported
}
function Start-Process { throw 'No process actions allowed in dispatch tests' }
function Stop-Service { throw 'No services allowed in dispatch tests' }
function Start-Service { throw 'No services allowed in dispatch tests' }
foreach ($script:action in @('database-inventory','publish-msi-packages')) {
    foreach ($script:direct in @($false,$true)) {
        $script:requestJson=@{Action=$script:action;LocalExecution=$false;Host='fixture.invalid';UseSsl=$true;HttpsPort=6001;Username='fixture-user';Password='fixture-password';MsiPackages=@(@{Kind='ntc'});SkipLocalPackageCopy=$script:direct;IrisUseTls=$true;RemoteInstallPath=''} | ConvertTo-Json -Depth 8 -Compress
        $previous=[Console]::Out;$writer=[IO.StringWriter]::new()
        try { [Console]::SetOut($writer);& $worker } finally { [Console]::SetOut($previous) }
        $results=@($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim().StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object Event -eq 'result')
        Assert ($results.Count -eq 1 -and $results[0].Success) 'remote database worker returns exactly one successful result without service or installer actions'
        if ($script:action -eq 'publish-msi-packages') {
            Assert ($results[0].Changed -and -not $results[0].Skipped -and @($results[0].DatabaseUploadedKinds).Count -eq 1 -and
                $results[0].DatabaseUploadedKinds[0] -eq 'ntc' -and $results[0].Message.Contains('zzzSoftware')) 'the deserialized upload result retains success, committed product kinds and its message for both transfer modes'
        }
        if ($script:action -eq 'database-inventory') { Assert ($results[0].IrisInstances.Count -eq 1 -and $results[0].IrisInstances[0].Name -eq 'REMOTE-IRIS') 'Database Check returns remote instance and namespace inventory through the JSON protocol' }
    }
}
Assert ($script:sessions -eq 4 -and $script:removed -eq 4 -and $script:inventoryCalls -eq 2 -and $script:published -eq 2) 'each remote database action owns and releases exactly one session'
$script:action='update'
$script:requestJson=@{Action='update';LocalExecution=$false;Host='fixture.invalid';MsiPackages=@(@{Kind='ntc'})} | ConvertTo-Json -Depth 5 -Compress
$previous=[Console]::Out;$writer=[IO.StringWriter]::new()
try { [Console]::SetOut($writer);& $worker } finally { [Console]::SetOut($previous) }
$results=@($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim().StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object Event -eq 'result')
Assert ($results.Count -eq 1 -and -not $results[0].Success -and $results[0].Error.Contains('einzeln') -and $script:sessions -eq 4) 'MSI installation requires an explicit individual package instead of treating a database package list as an install request'
