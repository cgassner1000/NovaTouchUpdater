. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
# Run with Windows PowerShell 5.1. All network/remoting calls are mocked.
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $TestScriptsRoot 'WinRmWorker.ps1') -Raw
$tokens = $null; $parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$source = $source.Replace('$req = [Console]::In.ReadLine() | ConvertFrom-Json', '$req = $script:request')
# Replace only TCP implementation; diagnostic control flow remains intact.
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
$tcp = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-TcpPort' }, $true)
$mockTcp = 'function Test-TcpPort([string]$computer, [int]$port) { $script:tcpCalls++; if ($port -ne $script:expectedPort) { throw "Wrong TCP port" }; if ($script:scenario -eq "tcp-failure") { throw "Simulated TCP failure" } }'
$source = $source.Remove($tcp.Extent.StartOffset, $tcp.Extent.EndOffset - $tcp.Extent.StartOffset).Insert($tcp.Extent.StartOffset, $mockTcp)
function New-PSSessionOption { param($OpenTimeout, $OperationTimeout, $CancelTimeout) return @{} }
function New-PSSession {
    param($ComputerName, $Port, $SessionOption, $ErrorAction, [switch]$UseSSL, $Credential)
    $script:sessionCalls++
    if ($Port -ne $script:expectedPort) { throw 'Wrong port' }
    if ($UseSSL.IsPresent -ne $script:request.UseSsl) { throw 'Wrong transport' }
    if ($script:request.Username -and $Credential.UserName -ne $script:request.Username) { throw 'Wrong credentials' }
    if (-not $script:request.Username -and $null -ne $Credential) { throw 'Expected current Windows identity' }
    if ($script:scenario -eq 'session-failure') { throw 'Simulated session failure' }
    return [pscustomobject]@{ Id=1 }
}
function Invoke-Command {
    param($Session, $ScriptBlock, $ErrorAction)
    $script:commandCalls++
    if ($script:scenario -eq 'command-failure') { throw 'Simulated command failure' }
    if ($script:scenario -eq 'wrong-reply') { return 'Incorrect reply' }
    & $ScriptBlock
}
function Remove-PSSession { param($Session, $ErrorAction) $script:removed++ }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
foreach ($transport in @(@{ Ssl=$false; Port=443 }, @{ Ssl=$true; Port=$null }, @{ Ssl=$true; Port=443 })) {
    $ssl = $transport.Ssl
    foreach ($scenario in @('success', 'tcp-failure', 'session-failure', 'command-failure', 'wrong-reply')) {
        $script:scenario = $scenario
        $script:request = [pscustomobject]@{ Host='example.invalid'; UseSsl=$ssl; Username=$(if ($ssl) { 'DOMAIN\test' } else { '' }); Password='test-only' }
        if ($null -ne $transport.Port) { $script:request | Add-Member HttpsPort $transport.Port }
        $script:expectedPort = if (-not $ssl) { 5985 } elseif ($null -eq $transport.Port) { 5986 } else { $transport.Port }
        $script:tcpCalls=0; $script:sessionCalls=0; $script:commandCalls=0; $script:removed=0
        $writer = [IO.StringWriter]::new()
        $original = [Console]::Out
        try { [Console]::SetOut($writer); & ([scriptblock]::Create($source)) }
        finally { [Console]::SetOut($original) }
        $result = $writer.ToString() | ConvertFrom-Json
        Assert ($result.Success -eq ($scenario -eq 'success')) "$scenario success mismatch: $($result.Error)"
        Assert ($script:sessionCalls -eq 1) 'Session must be attempted even after TCP failure'
        Assert ($script:tcpCalls -eq 1) 'TCP must be tested once on the configured transport port'
        Assert ($script:commandCalls -eq $(if ($scenario -eq 'session-failure') { 0 } else { 1 })) 'Wrong command call count'
        Assert ($script:removed -eq $(if ($scenario -eq 'session-failure') { 0 } else { 1 })) 'Session cleanup missing'
        if ($scenario -eq 'session-failure') { Assert ($result.Command -like '*keine Sitzung*') 'Missing skipped status' }
        if ($scenario -ne 'success') { Assert ($result.Error.Length -gt 0) 'Missing error details' }
        Write-Output "PASS SSL=$ssl port=$script:expectedPort scenario=$scenario"
    }
}
foreach ($invalidPort in @(0,65536)) {
    $script:request = [pscustomobject]@{ Host='example.invalid'; UseSsl=$true; HttpsPort=$invalidPort; Username=''; Password='' }
    $script:tcpCalls=0; $script:sessionCalls=0
    $writer=[IO.StringWriter]::new(); $original=[Console]::Out
    try { [Console]::SetOut($writer); & ([scriptblock]::Create($source)) } finally { [Console]::SetOut($original) }
    $result=$writer.ToString() | ConvertFrom-Json
    Assert (-not $result.Success -and $result.Error -match 'HTTPS-Port' -and $script:tcpCalls -eq 0 -and $script:sessionCalls -eq 0) 'Invalid diagnostic port must fail before TCP or remoting'
    Write-Output "PASS invalid HTTPS port $invalidPort rejected before TCP or remoting"
}
