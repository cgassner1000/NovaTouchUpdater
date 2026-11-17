. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert($value, [string]$message) { if (-not $value) { throw $message }; Write-Output ('PASS ' + $message) }
$source=Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'HostConsole.ps1') -Raw -Encoding UTF8
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
Assert ($errors.Count -eq 0) 'console bootstrap parses in Windows PowerShell 5.1'
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConnectHostConsole'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
function New-PSSessionOption { param($OpenTimeout,$OperationTimeout) @{Open=$OpenTimeout;Operation=$OperationTimeout} }
function New-PSSession {
    param($ComputerName,$Port,$ConfigurationName,$SessionOption,$Credential,[switch]$UseSSL,$ErrorAction)
    $script:connections++
    if ($ComputerName -cne $script:request.Host -or $ConfigurationName -ne 'Microsoft.PowerShell') { throw 'Wrong target or endpoint' }
    $expectedPort=if($script:request.UseSsl){$script:request.HttpsPort}else{5985}
    if ($Port -ne $expectedPort -or $UseSSL.IsPresent -ne $script:request.UseSsl) { throw 'Wrong transport' }
    if ($SessionOption.Open -ne 15000 -or $SessionOption.Operation -ne 180000) { throw 'Wrong timeout' }
    if ($script:request.Username) {
        if ($Credential.UserName -cne $script:request.Username -or $Credential.GetNetworkCredential().Password -cne $script:request.Password) { throw 'Credential changed' }
    } elseif ($null -ne $Credential) { throw 'Current Windows identity must not be replaced' }
    if ($script:connectFailure) { throw 'Synthetic connection failure' }
    [pscustomobject]@{Runspace='Synthetic remote runspace'}
}
function Invoke-Command {
    param($Session,$ScriptBlock,$ErrorAction)
    $script:checks++
    if ($Session.Runspace -ne 'Synthetic remote runspace') { throw 'Wrong session' }
    [pscustomobject]@{Name='SYNTHETIC\admin';ComputerName='SYNTHETIC';IsAdministrator=$script:administrator}
}
function Remove-PSSession { param($Session,$ErrorAction) $script:removed++ }
function Read-Host { param($Prompt) '' }
function Push-TestRunspace { param($Runspace) if($Runspace -ne 'Synthetic remote runspace'){throw 'Wrong runspace'}; $script:pushed++ }
function ResetFixture {
    $script:request=[pscustomobject]@{Host='example.invalid';LocalExecution=$false;UseSsl=$false;HttpsPort=5986;Username='';Password=''}
    $script:connections=0;$script:checks=0;$script:removed=0;$script:pushed=0;$script:connectFailure=$false;$script:administrator=$true
}
foreach($https in @($false,$true)) {
    foreach($credentials in @($false,$true)) {
        ResetFixture
        $script:request.UseSsl=$https;$script:request.HttpsPort=443
        if($credentials){$script:request.Username='SYNTHETIC\admin';$script:request.Password='Synthetic pipe-only secret'}
        $session=ConnectHostConsole $script:request
        Assert ($session.Runspace -eq 'Synthetic remote runspace' -and $script:connections -eq 1 -and $script:checks -eq 1 -and $script:removed -eq 0) "correct single-host transport and verified administrator; HTTPS=$https; credentials=$credentials"
    }
}
ResetFixture
$script:request.Host="target.invalid'; throw 'never execute data"
$null=ConnectHostConsole $script:request
Assert ($script:connections -eq 1) 'hostname text remains data rather than executable startup code'
foreach($failure in @('filtered token','connection failure','invalid port','missing password')) {
    ResetFixture
    switch($failure){
        'filtered token' {$script:administrator=$false}
        'connection failure' {$script:connectFailure=$true}
        'invalid port' {$script:request.UseSsl=$true;$script:request.HttpsPort=0}
        'missing password' {$script:request.Username='SYNTHETIC\admin'}
    }
    $failed=$false
    try{$null=ConnectHostConsole $script:request}catch{$failed=$true}
    Assert ($failed -and $script:pushed -eq 0 -and $script:removed -eq [int]($failure -eq 'filtered token')) "no interactive session or privilege workaround on $failure"
}
# Exercise the real startup control flow. No real remoting or UI input is used.
$bootstrap=$source.Replace("ReadHostConsoleRequest '__PIPE_NAME__'", '$script:request').Replace('$Host.PushRunspace($session.Runspace)', 'Push-TestRunspace $session.Runspace').Replace('exit 1', "throw 'FixtureConsoleClosed'")
ResetFixture
& ([scriptblock]::Create($bootstrap))
Assert ($script:pushed -eq 1 -and $null -eq $script:request.Password -and $script:removed -eq 0) 'the verified remote runspace becomes interactive and remains open after startup'
ResetFixture;$script:administrator=$false
$closed=$false
try{& ([scriptblock]::Create($bootstrap))}catch{$closed=$_.Exception.Message -eq 'FixtureConsoleClosed'}
Assert ($closed -and $script:pushed -eq 0 -and $script:removed -eq 1) 'failed startup closes the dedicated console instead of leaving a local shell'
$prompt=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'global:prompt'},$true)
Assert ($null -ne $prompt.Body.Find({param($node) $node -is [Management.Automation.Language.ExitStatementAst]},$true)) 'leaving the remote runspace closes the local fallback prompt'
