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
    [pscustomobject]@{ Status = $(if ($script:scenario -eq 'stopped-service') { 'Stopped' } else { 'Running' }) }
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
function Start-Service { throw 'Checking local WinRM must not change service state' }
function Set-Service { throw 'Checking local WinRM must not change service configuration' }
foreach ($scenario in @('success', 'already-trusted', 'empty-list', 'existing-wildcard', 'missing-service', 'stopped-service',
    'broken-wsman', 'read-denied', 'write-denied', 'policy-override', 'invalid-host')) {
    $script:scenario = $scenario
    $script:order = [Collections.Generic.List[string]]::new()
    $script:writes = 0
    $script:stored = switch ($scenario) {
        'already-trusted' { 'keep.invalid,POS01,pos02' }
        'empty-list' { '' }
        'existing-wildcard' { '*.existing.invalid' }
        default { 'keep.invalid, POS01' }
    }
    $script:request = [pscustomobject]@{ Action = 'winrm-prepare'; Hosts = @(' pos01 ', 'POS02', 'pos02') }
    if ($scenario -eq 'invalid-host') { $script:request.Hosts = @('*.invalid') }
    $writer = [IO.StringWriter]::new(); $original = [Console]::Out
    try { [Console]::SetOut($writer); & ([scriptblock]::Create($source)) }
    finally { [Console]::SetOut($original) }
    $result = $writer.ToString() | ConvertFrom-Json
    $succeeds = $scenario -in @('success', 'already-trusted', 'empty-list', 'existing-wildcard')
    Assert ($result.Success -eq $succeeds) "Unexpected preparation result ($scenario): $($result.Error)"
    Assert ($result.Tcp -eq 'Nicht getestet' -and $result.Session -eq 'Nicht getestet') 'Preparation must not run remote diagnostics'
    if ($succeeds) {
        Assert ($script:stored -split ',' -icontains 'POS01') 'First requested host missing'
        Assert ($script:stored -split ',' -icontains 'POS02') 'Second requested host missing'
        Assert (@($script:stored -split ',' | Where-Object { $_ -ieq 'pos02' }).Count -eq 1) 'Hosts must not be duplicated'
        Assert ($result.Message -match 'installiert und funktionsfähig') 'Missing local health result'
        Assert ($script:order[0] -eq 'service' -and $script:order[1] -eq 'wsman') 'Local health must precede TrustedHosts changes'
    }
    if ($scenario -eq 'success') {
        Assert ($script:stored -eq 'keep.invalid,POS01,POS02' -and $script:writes -eq 1) 'Existing hosts must be preserved and missing hosts added once'
        Assert ($script:order[$script:order.Count - 1] -eq 'read') 'TrustedHosts must be verified after writing'
    }
    if ($scenario -eq 'already-trusted') { Assert ($script:writes -eq 0) 'Already trusted hosts must not require another write' }
    if ($scenario -eq 'existing-wildcard') { Assert ($script:stored.StartsWith('*.existing.invalid,')) 'Existing wildcard entries must be preserved' }
    if ($scenario -in @('missing-service', 'stopped-service', 'broken-wsman', 'read-denied', 'invalid-host')) {
        Assert ($script:writes -eq 0) 'Failed prerequisites must not change TrustedHosts'
    }
    if ($scenario -in @('read-denied', 'write-denied', 'policy-override')) {
        Assert ($result.Error -match 'Administrator' -and $result.Error -match 'Gruppenrichtlinie') 'Missing actionable permissions/policy error'
    }
    if (-not $succeeds) { Assert ($result.Error.Length -gt 0) 'Failed preparation must explain its cause' }
    Write-Output "PASS local WinRM preparation: $scenario"
}
