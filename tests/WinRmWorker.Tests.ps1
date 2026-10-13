# Run with Windows PowerShell 5.1. All network/remoting calls are mocked.
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\WinRmWorker.ps1') -Raw
$tokens = $null; $parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$source = $source.Replace('$req = [Console]::In.ReadLine() | ConvertFrom-Json', '$req = $script:request')
# Replace only TCP implementation; diagnostic control flow remains intact.
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
$tcp = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-TcpPort' }, $true)
$mockTcp = 'function Test-TcpPort([string]$computer, [int]$port) { if ($script:scenario -eq "tcp-failure") { throw "Simulated TCP failure" } }'
$source = $source.Remove($tcp.Extent.StartOffset, $tcp.Extent.EndOffset - $tcp.Extent.StartOffset).Insert($tcp.Extent.StartOffset, $mockTcp)
function New-PSSessionOption { param($OpenTimeout, $OperationTimeout, $CancelTimeout) return @{} }
function New-PSSession {
    param($ComputerName, $Port, $SessionOption, $ErrorAction, [switch]$UseSSL, $Credential)
    $script:sessionCalls++
    if ($Port -ne $(if ($script:request.UseSsl) { 5986 } else { 5985 })) { throw 'Wrong port' }
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
foreach ($ssl in @($false, $true)) {
    foreach ($scenario in @('success', 'tcp-failure', 'session-failure', 'command-failure', 'wrong-reply')) {
        $script:scenario = $scenario
        $script:request = [pscustomobject]@{ Host='example.invalid'; UseSsl=$ssl; Username=$(if ($ssl) { 'DOMAIN\test' } else { '' }); Password='test-only' }
        $script:sessionCalls=0; $script:commandCalls=0; $script:removed=0
        $writer = [IO.StringWriter]::new()
        $original = [Console]::Out
        try { [Console]::SetOut($writer); & ([scriptblock]::Create($source)) }
        finally { [Console]::SetOut($original) }
        $result = $writer.ToString() | ConvertFrom-Json
        Assert ($result.Success -eq ($scenario -eq 'success')) "$scenario success mismatch: $($result.Error)"
        Assert ($script:sessionCalls -eq 1) 'Session must be attempted even after TCP failure'
        Assert ($script:commandCalls -eq $(if ($scenario -eq 'session-failure') { 0 } else { 1 })) 'Wrong command call count'
        Assert ($script:removed -eq $(if ($scenario -eq 'session-failure') { 0 } else { 1 })) 'Session cleanup missing'
        if ($scenario -eq 'session-failure') { Assert ($result.Command -like '*keine Sitzung*') 'Missing skipped status' }
        if ($scenario -ne 'success') { Assert ($result.Error.Length -gt 0) 'Missing error details' }
        Write-Output "PASS SSL=$ssl scenario=$scenario"
    }
}
