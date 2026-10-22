# Windows PowerShell 5.1. Authentication is mocked; the real runspace scheduler
# is tested with a replacement probe that cannot contact remote hosts.
$ErrorActionPreference = 'Stop'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\ConnectionWorker.ps1') -Raw -Encoding UTF8
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
$probe = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'TestHostConnection' }, $true)
. ([scriptblock]::Create($probe.Extent.Text))

function New-PSSessionOption {
    param($OpenTimeout, $OperationTimeout, $CancelTimeout)
    Assert ($OpenTimeout -eq 3000 -and $OperationTimeout -eq 3000 -and $CancelTimeout -eq 500) 'Probe must use short connection/cancellation timeouts'
    return @{}
}
function New-PSSession {
    param($ComputerName, $Port, $SessionOption, $ErrorAction, [switch]$UseSSL, $Credential)
    $script:connections++
    Assert ($ComputerName -eq 'example.invalid') 'Unexpected probe target'
    Assert ($Port -eq $(if ($script:ssl) { 5986 } else { 5985 })) 'Probe must use the selected WinRM port'
    Assert ($UseSSL.IsPresent -eq $script:ssl) 'Probe must preserve HTTPS selection'
    Assert ($Credential -eq $script:credential) 'Probe must preserve selected credentials/current identity'
    if ($script:failConnection) { throw 'Simulated offline or authentication failure' }
    return [pscustomobject]@{ Id=1 }
}
function Remove-PSSession { param($Session, $ErrorAction) $script:removed++ }
function Invoke-Command { throw 'Connection monitoring must not run remote application commands' }
foreach ($script:ssl in @($false, $true)) {
    foreach ($script:failConnection in @($false, $true)) {
        $script:connections=0; $script:removed=0
        $script:credential = if ($script:ssl) { [pscredential]::new('DOMAIN\test', (ConvertTo-SecureString 'test-only' -AsPlainText -Force)) } else { $null }
        $result = @(TestHostConnection 'example.invalid' $script:credential $script:ssl)
        Assert ($result.Count -eq 1) 'Probe must return one host result'
        Assert ($result[0].Connected -eq (-not $script:failConnection)) 'Probe must distinguish successful and failed authentication'
        Assert ($result[0].Host -eq 'example.invalid' -and $script:connections -eq 1) 'Probe must preserve the host identity'
        Assert ($script:removed -eq $(if ($script:failConnection) { 0 } else { 1 })) 'Probe must close an established session'
        Write-Output "PASS authenticated probe HTTPS=$script:ssl failure=$script:failConnection"
    }
}

Add-Type -TypeDefinition @'
using System.Threading;
public static class ConnectionTestCounters {
    public static int Active, Maximum;
    public static string ExpectedUser;
    public static bool ExpectedSsl;
    public static void Enter() {
        int count = Interlocked.Increment(ref Active);
        int old;
        do { old = Maximum; if (old >= count) break; }
        while (Interlocked.CompareExchange(ref Maximum, count, old) != old);
    }
    public static void Leave() { Interlocked.Decrement(ref Active); }
}
'@
$fakeProbe = @'
function TestHostConnection {
    param([string]$computer, [pscredential]$credential, [bool]$useSsl)
    [ConnectionTestCounters]::Enter()
    try {
        if ($useSsl -ne [ConnectionTestCounters]::ExpectedSsl) { throw 'Wrong TLS selection in runspace' }
        $user = if ($null -ne $credential) { $credential.UserName } else { '' }
        if ($user -ne [ConnectionTestCounters]::ExpectedUser) { throw 'Wrong credentials in runspace' }
        switch ($computer) {
            'slow' { Start-Sleep -Milliseconds 350 }
            'fast' { Start-Sleep -Milliseconds 10 }
            'hang' { Start-Sleep -Milliseconds 6500 }
            'throws' { throw 'Simulated probe exception' }
            'cleanup-warning' { Write-Error 'Simulated cleanup warning after successful connection' -ErrorAction Continue }
            default { Start-Sleep -Milliseconds 30 }
        }
        [pscustomobject]@{ Host=$computer; Connected=($computer -ne 'offline'); Message='Simulated result' }
    }
    finally { [ConnectionTestCounters]::Leave() }
}
'@
$batchSource = $source.Remove($probe.Extent.StartOffset, $probe.Extent.EndOffset - $probe.Extent.StartOffset).Insert($probe.Extent.StartOffset, $fakeProbe)
$batchSource = $batchSource.Replace('$req = [Console]::In.ReadLine() | ConvertFrom-Json', '$req = $script:request')
$batchAst = [Management.Automation.Language.Parser]::ParseInput($batchSource, [ref]$tokens, [ref]$parseErrors)
$remoteCalls = @($batchAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'New-PSSession' }, $true))
Assert ($remoteCalls.Count -eq 0) 'The runspace test must contain no real authentication calls'
$batch = [scriptblock]::Create($batchSource)
function RunBatch([string[]]$computers, [int]$parallelism, [bool]$ssl, [string]$username='', [string]$action='monitor-connections') {
    [ConnectionTestCounters]::Active=0; [ConnectionTestCounters]::Maximum=0
    [ConnectionTestCounters]::ExpectedSsl=$ssl; [ConnectionTestCounters]::ExpectedUser=$username
    $script:request = [pscustomobject]@{ Action=$action; Hosts=$computers; Parallelism=$parallelism; UseSsl=$ssl; Username=$username; Password='test-only' }
    $writer = [IO.StringWriter]::new()
    $original = [Console]::Out
    try { [Console]::SetOut($writer); & $batch }
    finally { [Console]::SetOut($original) }
    return @($writer.ToString() -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}
$elapsed = [Diagnostics.Stopwatch]::StartNew()
$events = @(RunBatch @('slow','fast','offline','throws','hang','tail','cleanup-warning') 2 $true 'DOMAIN\test')
$checks = @($events | Where-Object Event -eq 'progress')
$final = @($events | Where-Object Event -eq 'result')
Assert ($final.Count -eq 1 -and $final[0].Success) 'Batch must finish after independent per-host failures'
Assert ($checks.Count -eq 7) 'Each host must receive one connection result'
Assert ($checks[0].Host -eq 'fast') 'Fast host must be reported before a slower earlier host'
Assert (@($checks | Where-Object { $_.Host -ne 'throws' -and $_.Connected -isnot [bool] }).Count -eq 0) 'Completed connection results must contain JSON booleans'
Assert (($checks | Where-Object Host -eq 'offline').Connected -eq $false) 'Offline host must be marked unreachable'
Assert ($null -eq ($checks | Where-Object Host -eq 'throws').Connected) 'A broken probe must remain unknown rather than report a proven offline host'
Assert (($checks | Where-Object Host -eq 'cleanup-warning').Connected -eq $true) 'Cleanup warnings must not turn successful authentication into an offline result'
Assert (($checks | Where-Object Host -eq 'hang').Message -match 'Timeout') 'Hung probe must time out'
Assert (($checks | Where-Object Host -eq 'tail').Connected -eq $true) 'Later hosts must still be checked'
Assert ([ConnectionTestCounters]::Maximum -eq 2 -and [ConnectionTestCounters]::Active -eq 0) 'Parallelism must be bounded and all probes cleaned up'
Assert ($elapsed.Elapsed.TotalSeconds -lt 9) 'One stalled probe must not indefinitely block a round'
Write-Output 'PASS bounded parallel scheduling, streaming results, timeout and cleanup'

$events = @(RunBatch @('first','second','second') 0 $false)
Assert (@($events | Where-Object Event -eq 'progress').Count -eq 2) 'Duplicate hosts must not be checked twice'
Assert ([ConnectionTestCounters]::Maximum -eq 1) 'Zero parallelism must be clamped to one'
Write-Output 'PASS duplicate hosts and minimum parallelism'
$events = @(RunBatch @('first') 2 $false '' 'invalid-action')
Assert (-not ($events | Where-Object Event -eq 'result').Success) 'Unknown actions must be rejected'
Assert ([ConnectionTestCounters]::Maximum -eq 0) 'Unknown actions must not run any probes'
Write-Output 'PASS unknown action rejected'
