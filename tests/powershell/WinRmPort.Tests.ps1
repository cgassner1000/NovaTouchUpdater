. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
# Exercise the real general-worker session setup. Probe mode returns immediately;
# every remoting API is mocked, so no hosts, files or services are changed.
$ErrorActionPreference = 'Stop'
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$source = Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'RemoteWorker.ps1') -Raw -Encoding UTF8
$source = $source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 0', 'return').Replace('exit 1', 'return')
$worker = [scriptblock]::Create($source)
function New-PSSessionOption { param($OpenTimeout,$OperationTimeout) @{} }
function New-PSSession {
    param($ComputerName, $Port, $SessionOption, $ErrorAction, [switch]$UseSSL, $Credential)
    $script:connections++
    Assert ($ComputerName -eq 'example.invalid' -and $Port -eq $script:expectedPort) 'Wrong host or port'
    Assert ($UseSSL.IsPresent -eq $script:request.UseSsl) 'Wrong TLS selection'
    [pscustomobject]@{ Id=1 }
}
function Remove-PSSession { param($Session, $ErrorAction) $script:removed++ }
function Invoke-Command { throw 'Probe must not execute remote application commands' }
foreach ($test in @(
    @{ Ssl=$false; Port=$null; Expected=5985 },
    @{ Ssl=$true; Port=$null; Expected=5986 },
    @{ Ssl=$true; Port=443; Expected=443 },
    @{ Ssl=$false; Port=443; Expected=5985 },
    @{ Ssl=$true; Port=65535; Expected=65535 },
    @{ Ssl=$true; Port=0; Expected=0 },
    @{ Ssl=$true; Port=65536; Expected=0 }
)) {
    $script:request = @{ Action='probe'; Host='example.invalid'; UseSsl=$test.Ssl; Username=''; Password='' }
    if ($null -ne $test.Port) { $script:request.HttpsPort=$test.Port }
    $script:requestJson = $script:request | ConvertTo-Json -Compress
    $script:expectedPort=$test.Expected; $script:connections=0; $script:removed=0
    $writer=[IO.StringWriter]::new(); $original=[Console]::Out
    try { [Console]::SetOut($writer); & $worker } finally { [Console]::SetOut($original) }
    $events=@($writer.ToString() -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    $result=@($events | Where-Object Event -eq 'result')
    $valid=$test.Expected -gt 0
    Assert ($result.Count -eq 1 -and $result[0].Success -eq $valid) ('Unexpected result: ' + $writer.ToString())
    $expectedCalls = if ($valid) { 1 } else { 0 }
    Assert ($script:connections -eq $expectedCalls -and $script:removed -eq $expectedCalls) 'Wrong connection/cleanup count'
    if (-not $valid) { Assert ($result[0].Error -match 'HTTPS-Port') 'Invalid port must have a clear error' }
    Write-Output "PASS worker HTTPS=$($test.Ssl) configured=$($test.Port) expected=$($test.Expected)"
}
