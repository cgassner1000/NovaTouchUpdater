. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
# All service, WSMan and configuration calls are mocked. Never touch real TrustedHosts.
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'WinRmWorker.ps1') -Raw -Encoding UTF8
$tokens = $null; $parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$source = $source.Replace('$req = [Console]::In.ReadLine() | ConvertFrom-Json', '$req = $script:request')
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
function Get-Service {
    param($Name, $ErrorAction)
    $script:order.Add('service')
    Assert ($Name -eq 'WinRM') 'Only the local WinRM service may be inspected'
    if ($script:scenario -eq 'missing-service') { throw 'Service not installed' }
    return $script:service
}
function Test-WSMan {
    param($ComputerName, $ErrorAction)
    $script:order.Add('wsman')
    Assert ($ComputerName -eq 'localhost') 'Preparation must only probe the local WSMan endpoint'
    if ($script:scenario -eq 'broken-wsman') { throw 'No local listener' }
    [pscustomobject]@{ ProductVersion = 'Test-only WSMan' }
}
function Get-Item {
    param($LiteralPath, $ErrorAction)
    $script:order.Add('read')
    Assert ($LiteralPath -eq 'WSMan:\localhost\Client\TrustedHosts') 'Unexpected configuration read'
    if ($script:scenario -eq 'read-denied') { throw 'Access denied reading TrustedHosts' }
    [pscustomobject]@{ Value = $script:stored }
}
function Set-Item {
    param($LiteralPath, $Value, [switch]$Force, $ErrorAction)
    $script:order.Add('write')
    Assert ($LiteralPath -eq 'WSMan:\localhost\Client\TrustedHosts' -and $Force.IsPresent) 'Unexpected configuration write'
    $script:writes++
    if ($script:scenario -eq 'write-denied') { throw 'Access denied writing TrustedHosts' }
    if ($script:scenario -ne 'policy-override') { $script:stored = $Value }
}
function New-PSSession { throw 'Preparation must never open a remote session' }
function Invoke-Command { throw 'Preparation must never execute a remote command' }
function Start-Service {
    param($Name, $ErrorAction)
    Assert ($Name -eq 'WinRM') 'Only local WinRM may be started'
    $script:order.Add('start')
    if ($script:scenario -eq 'start-denied') { throw 'Access denied starting WinRM' }
    $script:service.Status = 'StartPending'
}
function Resume-Service {
    param($Name, $ErrorAction)
    Assert ($Name -eq 'WinRM') 'Only local WinRM may be resumed'
    $script:order.Add('resume')
    $script:service.Status = 'StartPending'
}
function Set-Service {
    param($Name, $StartupType, $ErrorAction)
    Assert ($Name -eq 'WinRM' -and $StartupType -eq 'Automatic') 'Only disabled local WinRM may be enabled'
    $script:order.Add('enable')
    if ($script:scenario -eq 'activation-denied') { throw 'Policy denies changing WinRM' }
    $script:service.StartType = $StartupType
}
foreach ($scenario in @('success', 'already-trusted', 'empty-list', 'existing-wildcard', 'missing-service', 'stopped-service',
    'disabled-service', 'starting-service', 'stopping-service', 'paused-service', 'start-denied', 'start-timeout', 'activation-denied',
    'local-only', 'broken-wsman', 'read-denied', 'write-denied', 'policy-override', 'invalid-host')) {
    $script:scenario = $scenario
    $script:order = [Collections.Generic.List[string]]::new()
    $script:writes = 0
    $script:service = [pscustomobject]@{
        Status = $(switch ($scenario) {
            { $_ -in @('stopped-service','disabled-service','start-denied','start-timeout','activation-denied') } { 'Stopped'; break }
            'starting-service' { 'StartPending' }
            'stopping-service' { 'StopPending' }
            'paused-service' { 'Paused' }
            default { 'Running' }
        })
        StartType = $(if ($scenario -in @('disabled-service','activation-denied')) { 'Disabled' } else { 'Manual' })
    }
    $script:service | Add-Member ScriptMethod WaitForStatus {
        param($status, $timeout)
        $script:order.Add('wait:' + $status)
        if ($script:scenario -eq 'start-timeout') { throw 'Timed out waiting for WinRM' }
        $this.Status = [string]$status
    }
    $script:stored = switch ($scenario) {
        'already-trusted' { 'keep.invalid,POS01,pos02' }
        'empty-list' { '' }
        'existing-wildcard' { '*.existing.invalid' }
        'local-only' { '' }
        default { 'keep.invalid, POS01' }
    }
    $script:request = [pscustomobject]@{ Action = 'winrm-prepare'; Hosts = @(' pos01 ', 'POS02', 'pos02') }
    if ($scenario -eq 'invalid-host') { $script:request.Hosts = @('*.invalid') }
    if ($scenario -eq 'local-only') { $script:request.Hosts = @() }
    $writer = [IO.StringWriter]::new(); $original = [Console]::Out
    try { [Console]::SetOut($writer); & ([scriptblock]::Create($source)) }
    finally { [Console]::SetOut($original) }
    $result = $writer.ToString() | ConvertFrom-Json
    $succeeds = $scenario -in @('success', 'already-trusted', 'empty-list', 'existing-wildcard', 'stopped-service',
        'disabled-service', 'starting-service', 'stopping-service', 'paused-service', 'local-only')
    Assert ($result.Success -eq $succeeds) "Unexpected preparation result ($scenario): $($result.Error)"
    Assert ($result.Tcp -eq 'Nicht getestet' -and $result.Session -eq 'Nicht getestet') 'Preparation must not run remote diagnostics'
    if ($succeeds) {
        if ($scenario -ne 'local-only') {
            Assert ($script:stored -split ',' -icontains 'POS01') 'First requested host missing'
            Assert ($script:stored -split ',' -icontains 'POS02') 'Second requested host missing'
            Assert (@($script:stored -split ',' | Where-Object { $_ -ieq 'pos02' }).Count -eq 1) 'Hosts must not be duplicated'
        }
        Assert ($result.Message -match 'installiert und funktionsfähig') 'Missing local health result'
        Assert ($script:service.Status -eq 'Running' -and $script:order[0] -eq 'service' -and
            $script:order.IndexOf('wsman') -lt $script:order.IndexOf('read')) 'Running service and local health must precede TrustedHosts changes'
    }
    if ($scenario -in @('stopped-service','disabled-service','stopping-service')) {
        Assert ('start' -in $script:order -and $script:order.IndexOf('wait:Running') -lt $script:order.IndexOf('wsman')) 'WinRM must be started and confirmed before WSMan is tested'
    }
    if ($scenario -eq 'disabled-service') { Assert ($script:order.IndexOf('enable') -lt $script:order.IndexOf('start')) 'Disabled WinRM must be enabled before starting' }
    if ($scenario -eq 'starting-service') { Assert ('start' -notin $script:order -and 'wait:Running' -in $script:order) 'Already starting WinRM must be awaited without another start' }
    if ($scenario -eq 'paused-service') { Assert ('resume' -in $script:order -and 'start' -notin $script:order) 'Paused WinRM must be resumed' }
    if ($scenario -eq 'success') { Assert ('start' -notin $script:order -and 'enable' -notin $script:order) 'Healthy WinRM must not be restarted or reconfigured' }
    if ($scenario -eq 'local-only') { Assert ($script:writes -eq 0) 'Local-only diagnosis must not write an empty TrustedHosts list' }
    if ($scenario -eq 'success') {
        Assert ($script:stored -eq 'keep.invalid,POS01,POS02' -and $script:writes -eq 1) 'Existing hosts must be preserved and missing hosts added once'
        Assert ($script:order[$script:order.Count - 1] -eq 'read') 'TrustedHosts must be verified after writing'
    }
    if ($scenario -eq 'already-trusted') { Assert ($script:writes -eq 0) 'Already trusted hosts must not require another write' }
    if ($scenario -eq 'existing-wildcard') { Assert ($script:stored.StartsWith('*.existing.invalid,')) 'Existing wildcard entries must be preserved' }
    if ($scenario -in @('missing-service', 'start-denied', 'start-timeout', 'activation-denied', 'broken-wsman', 'read-denied', 'invalid-host')) {
        Assert ($script:writes -eq 0) 'Failed prerequisites must not change TrustedHosts'
    }
    if ($scenario -in @('start-denied', 'start-timeout', 'activation-denied')) {
        Assert ('wsman' -notin $script:order -and $result.Error -match 'Administrator') 'A failed service start must stop preparation with an actionable error'
    }
    if ($scenario -in @('read-denied', 'write-denied', 'policy-override')) {
        Assert ($result.Error -match 'Administrator' -and $result.Error -match 'Gruppenrichtlinie') 'Missing actionable permissions/policy error'
    }
    if (-not $succeeds) { Assert ($result.Error.Length -gt 0) 'Failed preparation must explain its cause' }
    Write-Output "PASS local WinRM preparation: $scenario"
}
