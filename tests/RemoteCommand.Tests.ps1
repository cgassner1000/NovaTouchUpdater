# Windows PowerShell 5.1. All remoting, service and shutdown actions are mocked.
# The native shutdown call is replaced before any worker code can execute.
$ErrorActionPreference = 'Stop'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\RemoteCommand.ps1') -Raw -Encoding UTF8
$tokens = $null; $parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
$nativeCall = '& $shutdown '
Assert ([regex]::Matches($source, [regex]::Escape($nativeCall)).Count -eq 1) 'Expected exactly one replaceable native shutdown call'
$source = $source.Replace($nativeCall, 'Invoke-TestShutdown ')
$source = $source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 1', 'return')
Assert (-not $source.Contains($nativeCall)) 'Native shutdown must be removed before tests run'
$worker = [scriptblock]::Create($source)

function New-PSSessionOption { param($OpenTimeout, $OperationTimeout) return @{} }
function New-PSSession {
    param($ComputerName, $SessionOption, $ErrorAction, [switch]$UseSSL, $Credential)
    $script:connections++
    Assert ($ComputerName -eq 'example.invalid') 'Only the mocked host may be addressed'
    Assert ($UseSSL.IsPresent -eq [bool]$script:request.UseSsl) 'HTTPS selection must be forwarded'
    if ($script:request.Username) {
        Assert ($null -ne $Credential -and $Credential.UserName -eq $script:request.Username) 'Selected credentials must be forwarded'
    } else {
        Assert ($null -eq $Credential) 'Empty username must use the current Windows identity'
    }
    if ($script:connectionError) { throw 'Simulated connection failure' }
    return [pscustomobject]@{ Id=1 }
}
function Invoke-Command {
    param($Session, $ScriptBlock, $ArgumentList, $ErrorAction)
    if ($script:remoteError) { throw 'Simulated remoting failure' }
    & $ScriptBlock @ArgumentList
}
function Remove-PSSession { param($Session, $ErrorAction) $script:removed++ }
function Invoke-TestShutdown {
    $script:shutdownCalls++
    $script:shutdownArgs = @($args)
    $global:LASTEXITCODE = $script:shutdownExitCode
    if ($script:shutdownExitCode -ne 0) { 'Simulated access denied' }
}
# Fail closed if a future change introduces a different reboot API.
function Restart-Computer { throw 'Unexpected real restart API in test' }
function Stop-Computer { throw 'Unexpected real shutdown API in test' }
function Get-Service {
    param($Name, $ErrorAction)
    $script:serviceReads++
    if ($Name) {
        if (-not $script:services.ContainsKey($Name)) { throw "Unknown mocked service: $Name" }
        return $script:services[$Name]
    }
    return @($script:services.Values)
}
function Stop-Service {
    param($Name, [switch]$Force, $ErrorAction)
    $script:serviceActions.Add("stop:$Name")
    if ($Name -eq $script:failedService) { throw 'Simulated service failure' }
    $script:services[$Name].Status = 'Stopped'
}
function Start-Service {
    param($Name, $ErrorAction)
    $script:serviceActions.Add("start:$Name")
    $script:services[$Name].Status = 'Running'
}
function Service([string]$name, [string]$status='Running') {
    $service = [pscustomobject]@{ Name=$name; Status=$status }
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {
        param($desired, $timeout)
        $this.Status = [string]$desired
    }
    $script:services[$name] = $service
}
function CommandCase {
    param([string]$Name, [hashtable]$Request=@{}, [scriptblock]$Setup={}, [bool]$Success=$true,
        [int]$Shutdowns=0, [string[]]$Actions=@(), [string]$ErrorPattern='',
        [int]$Connections=1, [int]$Cleanup=1)
    $script:request = @{ Action='restart-services'; Host='example.invalid'; UseSsl=$false; Username=''; Password=''; RestartPayment=$false; RestartFiscal=$false; StartServerSuite=$false }
    foreach ($key in $Request.Keys) { $script:request[$key] = $Request[$key] }
    $script:connections=0; $script:removed=0; $script:shutdownCalls=0; $script:serviceReads=0
    $script:shutdownArgs=@(); $script:shutdownExitCode=0
    $script:services=@{}; $script:serviceActions=[Collections.Generic.List[string]]::new()
    $script:connectionError=$false; $script:remoteError=$false; $script:failedService=''
    & $Setup
    $script:requestJson = $script:request | ConvertTo-Json -Compress
    $writer = [IO.StringWriter]::new()
    $original = [Console]::Out
    try { [Console]::SetOut($writer); & $worker }
    finally { [Console]::SetOut($original) }
    $events = @($writer.ToString() -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    $results = @($events | Where-Object Event -eq 'result')
    Assert ($results.Count -eq 1) "$Name must emit exactly one result"
    $result = $results[0]
    Assert ($result.Success -eq $Success) "$Name unexpected result: $($result | ConvertTo-Json -Compress)"
    Assert ($script:shutdownCalls -eq $Shutdowns) "$Name unexpected shutdown call count"
    Assert ((($script:serviceActions | Sort-Object) -join ',') -ceq (($Actions | Sort-Object) -join ',')) "$Name unexpected service changes"
    Assert ($script:connections -eq $Connections) "$Name unexpected session count"
    Assert ($script:removed -eq $Cleanup) "$Name must clean up its session"
    if ($Shutdowns) {
        Assert ((($script:shutdownArgs | ForEach-Object { [string]$_ }) -join '|') -ceq '/r|/t|30|/d|p:4:1|/c|Terminal-Neustart durch NovaTouch Updater') "$Name must request a restart with 30 seconds delay"
    }
    if ($Request.Action -eq 'restart-terminal') {
        Assert ($script:serviceReads -eq 0) "$Name must not access services for a Windows restart"
        if ($Success) {
            Assert ($result.Changed -eq $true -and $result.Message -match '30 Sekunden angefordert') "$Name must acknowledge the request without claiming boot completion"
        }
    }
    if ($ErrorPattern) { Assert ($result.Error -match $ErrorPattern) "$Name missing error details" }
    Write-Output "PASS $Name"
}

CommandCase 'Windows restart' -Request @{ Action='restart-terminal' } -Shutdowns 1
CommandCase 'Windows restart with HTTPS credentials' -Request @{ Action='restart-terminal'; UseSsl=$true; Username='DOMAIN\test'; Password='test-only' } -Shutdowns 1
CommandCase 'Windows restart ignores service flags' -Request @{ Action='restart-terminal'; RestartPayment=$true; RestartFiscal=$true; StartServerSuite=$true } -Shutdowns 1 -Setup { Service 'nt.payment'; Service 'nt.fiscal'; Service 'nt3_server_suite' }
CommandCase 'Windows restart without service fields' -Request @{ Action='restart-terminal' } -Shutdowns 1 -Setup { $script:request.Remove('RestartPayment'); $script:request.Remove('RestartFiscal'); $script:request.Remove('StartServerSuite') }
CommandCase 'shutdown rejection' -Request @{ Action='restart-terminal' } -Shutdowns 1 -Success $false -Setup { $script:shutdownExitCode=5 } -ErrorPattern 'Exitcode 5.*Simulated access denied'
CommandCase 'restart connection failure' -Request @{ Action='restart-terminal' } -Success $false -Setup { $script:connectionError=$true } -ErrorPattern 'Simulated connection failure' -Cleanup 0
CommandCase 'restart remoting failure' -Request @{ Action='restart-terminal' } -Success $false -Setup { $script:remoteError=$true } -ErrorPattern 'Simulated remoting failure'
CommandCase 'payment restart only' -Request @{ RestartPayment=$true } -Setup { Service 'nt.payment.1'; Service 'nt.payment.2' 'Stopped'; Service 'nt.fiscal'; Service 'other' } -Actions @('stop:nt.payment.1','start:nt.payment.1','start:nt.payment.2')
CommandCase 'fiscal restart only' -Request @{ RestartFiscal=$true } -Setup { Service 'nt.payment'; Service 'nt.fiscal' } -Actions @('stop:nt.fiscal','start:nt.fiscal')
CommandCase 'server suite start' -Request @{ StartServerSuite=$true } -Setup { Service 'nt3_server_suite' 'Stopped'; Service 'other' } -Actions @('start:nt3_server_suite')
CommandCase 'all service actions' -Request @{ RestartPayment=$true; RestartFiscal=$true; StartServerSuite=$true } -Setup { Service 'nt.payment'; Service 'nt.fiscal'; Service 'nt3_server_suite' } -Actions @('stop:nt.payment','start:nt.payment','stop:nt.fiscal','start:nt.fiscal','stop:nt3_server_suite','start:nt3_server_suite')
CommandCase 'missing selected service' -Request @{ RestartFiscal=$true } -Setup { Service 'nt.payment' } -Success $false -ErrorPattern 'kein passender Dienst'
CommandCase 'service failure remains visible' -Request @{ RestartPayment=$true } -Setup { Service 'nt.payment'; $script:failedService='nt.payment' } -Actions @('stop:nt.payment') -Success $false -ErrorPattern 'Simulated service failure'
CommandCase 'no selection rejected' -Success $false -Connections 0 -Cleanup 0 -ErrorPattern 'Kein Dienst'
CommandCase 'unknown action rejected' -Request @{ Action='arbitrary-command' } -Success $false -Connections 0 -Cleanup 0 -ErrorPattern 'Unbekannte Aktion'
