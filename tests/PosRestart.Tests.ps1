# Windows PowerShell 5.1. No real process, task, remoting or native program runs.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$path = Join-Path $PSScriptRoot '..\RemoteCommand.ps1'
$source = [IO.File]::ReadAllText($path)
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
$functionAst = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'RequestPosRestart' }, $false)
$definitionSource = $functionAst.Extent.Text
$sessionFunction = $functionAst.Body.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-PosUserSessions' }, $true)
$nativeSource = $sessionFunction.Body.Find({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('public static class PosUserSessions') }, $true)
Assert ($null -ne $nativeSource) 'Expected the native Windows session discovery helper'
Add-Type -TypeDefinition $nativeSource.Value -ErrorAction Stop
Write-Output 'PASS Windows session discovery helper compiles in Windows PowerShell'
# Replace only session discovery with a fixture. No real sessions are queried;
# the actual C# implementation above is compiled but is never invoked.
$definitionSource = $definitionSource.Remove($sessionFunction.Extent.StartOffset - $functionAst.Extent.StartOffset, $sessionFunction.Extent.Text.Length)
$nativeCall = '& $taskkill '
Assert ([regex]::Matches($definitionSource, [regex]::Escape($nativeCall)).Count -eq 1) 'Expected one replaceable taskkill call'
$definitionSource = $definitionSource.Replace($nativeCall, 'Invoke-TestTaskkill ')
Assert (-not $definitionSource.Contains($nativeCall)) 'Native taskkill must be removed before tests can run'
. ([scriptblock]::Create($definitionSource))

$script:exe = 'C:\Program Files (x86)\novatouch\bin\KassaPreh.exe'
$script:cashierSid = 'S-1-5-21-123-456-789-1001'
function ProcessInfo([int]$id=101, [int]$session=2, [string]$sid=$script:cashierSid, [string]$exe=$script:exe) {
    [pscustomobject]@{ ProcessId=$id; SessionId=$session; ExecutablePath=$exe; CreationDate=[datetime]'2026-10-22T10:00:00';
        CommandLine=('"' + $exe + '" --station "Bar 1"'); OwnerSid=$sid }
}
function Test-Path { param($LiteralPath, $PathType, $ErrorAction)
    Assert ($LiteralPath -ceq $script:exe -and $PathType -eq 'Leaf') 'Only the fixed POS executable may be tested'
    -not $script:fileMissing
}
function Get-CimInstance { param($ClassName, $Filter, $ErrorAction)
    Assert ($ClassName -eq 'Win32_Process') 'Only process inventory is expected'
    if ($script:inventoryError) { throw 'Simulated process inventory failure' }
    if ($Filter -eq "Name = 'KassaPreh.exe'") { return @($script:processes) }
    Assert ($Filter -eq 'ProcessId = 101') 'Only the original exact process may be addressed'
    if ($script:reusePid -and -not $script:killed) {
        $replacement = ProcessInfo
        $replacement.CreationDate = $replacement.CreationDate.AddMinutes(1)
        return $replacement
    }
    @($script:processes | Where-Object ProcessId -eq 101)
}
function Invoke-CimMethod { param($InputObject, $MethodName, $ErrorAction)
    Assert ($MethodName -in @('GetOwnerSid','GetOwner')) 'Only process ownership may be read'
    if ($MethodName -eq 'GetOwnerSid') { return [pscustomobject]@{ ReturnValue=$script:ownerReturn; Sid=$InputObject.OwnerSid } }
    [pscustomobject]@{ ReturnValue=0; Domain='POSPC'; User='Cashier' }
}
function Get-PosUserSessions {
    $script:sessionReads++
    if ($script:sessionError) { throw 'Simulated Windows session query failure' }
    if ($script:sessionChanged -and $script:sessionReads -gt 1) { return [pscustomobject]@{ SessionId=2; Sid='S-1-5-21-999-999-999-9999'; Account='POSPC\OtherUser' } }
    @($script:users)
}
function Invoke-TestTaskkill {
    Assert (($args -join '|') -ceq '/PID|101|/F') 'taskkill must address only the prevalidated PID, never an image name or process tree'
    $script:kills++
    $global:LASTEXITCODE = $script:killExit
    if ($script:killExit -ne 0) { return 'Simulated taskkill rejection' }
    $script:killed = $true
    if (-not $script:stuck) { $script:processes = @($script:processes | Where-Object ProcessId -ne 101) }
    if ($script:watchdog) { $script:processes += ProcessInfo -id 202 }
}
function Start-Sleep { param($Milliseconds)
    if ($script:crash -and $script:runs -gt 0) { $script:processes = @() }
}
function New-Object { param($ComObject)
    Assert ($ComObject -ceq 'Schedule.Service') 'Only the mocked local Task Scheduler may be created'
    $script:schedulers++
    if ($script:schedulerError) { throw 'Simulated Task Scheduler access denied' }
    $script:scheduler
}
function MockTaskScheduler {
    $script:action = [pscustomobject]@{ Path=''; WorkingDirectory=''; Arguments='' }
    $actions = [pscustomobject]@{}
    $actions | Add-Member ScriptMethod Create { param($type) Assert ($type -eq 0) 'Only a direct executable action is allowed'; $script:action }
    $script:definition = [pscustomobject]@{
        RegistrationInfo=[pscustomobject]@{ Description='' }
        Principal=[pscustomobject]@{ UserId=''; LogonType=0; RunLevel=1 }
        Settings=[pscustomobject]@{ Enabled=$false; AllowDemandStart=$false; DisallowStartIfOnBatteries=$true; StopIfGoingOnBatteries=$true; ExecutionTimeLimit='' }
        Actions=$actions
    }
    $script:task = [pscustomobject]@{}
    $script:task | Add-Member ScriptMethod RunEx {
        param($parameters, $flags, $sessionId, $user)
        Assert ((-not $script:restartExisting) -or $script:killed) 'A previously running application must have been stopped before starting it'
        Assert ($null -eq $parameters -and $flags -eq 12 -and $sessionId -eq 2 -and $user -ceq $script:cashierSid) 'Start must use the cashier SID and the original exact session'
        $script:runs++
        if ($script:launchError) { throw 'Simulated interactive start failure' }
        if (-not $script:noLaunch) {
            $session = if ($script:wrongSession) { 3 } else { 2 }
            $sid = if ($script:wrongOwner) { 'S-1-5-21-123-456-789-9999' } else { $script:cashierSid }
            $script:processes += ProcessInfo -id 202 -session $session -sid $sid
        }
    }
    $script:folder = [pscustomobject]@{}
    $script:folder | Add-Member ScriptMethod RegisterTaskDefinition {
        param($name, $definition, $flags, $user, $password, $logon, $sddl)
        Assert ($name -match '^NovaTouchUpdater-POS-[a-f0-9]{32}$') 'Each task needs a unique scoped name'
        Assert ($flags -eq 2 -and $user -ceq $script:cashierSid -and $null -eq $password -and $logon -eq 3 -and $null -eq $sddl) 'Create a new interactive-token task without storing a password'
        Assert ($definition.Principal.UserId -ceq $script:cashierSid -and $definition.Principal.LogonType -eq 3 -and $definition.Principal.RunLevel -eq 0) 'Task principal must be the cashier at normal user privilege'
        Assert ($definition.Settings.Enabled -and $definition.Settings.AllowDemandStart -and -not $definition.Settings.DisallowStartIfOnBatteries -and -not $definition.Settings.StopIfGoingOnBatteries -and $definition.Settings.ExecutionTimeLimit -ceq 'PT0S') 'Task must run on demand and must not later terminate the POS'
        $expectedArguments = if ($script:restartExisting) { '--station "Bar 1"' } else { '' }
        Assert ($script:action.Path -ceq $script:exe -and $script:action.WorkingDirectory -ceq 'C:\Program Files (x86)\novatouch\bin' -and $script:action.Arguments -ceq $expectedArguments) 'Preserve executable, bin working directory and existing arguments; a fresh start uses no invented arguments'
        Assert ($script:kills -eq 0) 'Task must be prepared before terminating the POS'
        $script:taskName = $name
        $script:registrations++
        if ($script:registrationError -and -not $script:partialRegistration) { throw 'Simulated task registration failure' }
        $script:taskExists = $true
        if ($script:partialRegistration) { throw 'Simulated failure after registration' }
        if ($script:appearsDuringPreparation) { $script:processes += ProcessInfo -id 202 }
        $script:task
    }
    $script:folder | Add-Member ScriptMethod GetTask { param($name)
        Assert ($name -ceq $script:taskName) 'Only the task owned by this request may be inspected'
        if (-not $script:taskExists) { throw 'Mock task not found' }
        $script:task
    }
    $script:folder | Add-Member ScriptMethod DeleteTask { param($name, $flags)
        Assert ($name -ceq $script:taskName -and $flags -eq 0) 'Only the exact temporary task may be deleted'
        $script:deletes++
        if ($script:deletes -le $script:deleteFailures) { throw 'Simulated task cleanup failure' }
        $script:taskExists = $false
    }
    $script:scheduler = [pscustomobject]@{}
    $script:scheduler | Add-Member ScriptMethod Connect { }
    $script:scheduler | Add-Member ScriptMethod GetFolder { param($path) Assert ($path -ceq '\') 'Use the local task root'; $script:folder }
    $script:scheduler | Add-Member ScriptMethod NewTask { param($flags) Assert ($flags -eq 0) 'Create an empty task definition'; $script:definition }
}
function PosCase {
    param([string]$Name, [scriptblock]$Setup={}, [bool]$Success=$true, [bool]$Skipped=$false,
        [int]$Kills=0, [int]$Runs=0, [int]$Deletes=0, [string]$ErrorPattern='', [bool]$Changed=$true)
    $script:fileMissing=$false; $script:inventoryError=$false; $script:ownerReturn=0
    $script:schedulerError=$false; $script:registrationError=$false; $script:partialRegistration=$false
    $script:launchError=$false; $script:noLaunch=$false; $script:wrongSession=$false; $script:wrongOwner=$false
    $script:crash=$false; $script:reusePid=$false; $script:stuck=$false; $script:watchdog=$false
    $script:killed=$false; $script:taskExists=$false; $script:taskName=''
    $script:sessionReads=0; $script:sessionError=$false; $script:sessionChanged=$false; $script:appearsDuringPreparation=$false
    $script:users=@([pscustomobject]@{ SessionId=2; Sid=$script:cashierSid; Account='POSPC\Cashier' })
    $script:kills=0; $script:runs=0; $script:deletes=0; $script:registrations=0; $script:schedulers=0
    $script:killExit=0; $script:deleteFailures=0; $script:processes=@(ProcessInfo)
    MockTaskScheduler
    & $Setup
    $script:restartExisting = @($script:processes | Where-Object { $_.ExecutablePath -ceq $script:exe }).Count -eq 1
    try { $result = RequestPosRestart }
    catch { $result = [pscustomobject]@{ Results=@(); Failures=@($_.Exception.Message); Skipped=$false; Changed=$false } }
    Assert (($result.Failures.Count -eq 0) -eq $Success) "$Name unexpected result: $($result | ConvertTo-Json -Compress -Depth 5)"
    Assert ([bool]$result.Skipped -eq $Skipped) "$Name unexpected skip state"
    Assert ($script:kills -eq $Kills -and $script:runs -eq $Runs -and $script:deletes -eq $Deletes) "$Name unexpected process/task operations: kill=$script:kills run=$script:runs delete=$script:deletes"
    Assert ((-not $script:taskExists) -or $script:deleteFailures -ge 3) "$Name left a temporary task behind"
    if ($Skipped) { Assert (-not $result.Changed -and $script:schedulers -eq 0) "$Name must have no side effects" }
    if ($ErrorPattern) { Assert (($result.Failures -join '; ') -match $ErrorPattern) "$Name missing failure details: $($result.Failures -join '; ')" }
    if ($Success -and -not $Skipped) {
        Assert ($result.Changed -eq $Changed -and ($result.Results -join '; ') -match 'POSPC\\Cashier.*Sitzung 2.*Aufgabe gelöscht') "$Name must verify the user/session and confirm task cleanup"
    }
    Write-Output "PASS $Name"
}

PosCase 'POS restart as logged-on cashier, exact session, preserved arguments and cleanup' -Kills 1 -Runs 1 -Deletes 1
PosCase 'missing executable is skipped' -Setup { $script:fileMissing=$true } -Skipped $true
PosCase 'installed but inactive POS starts as logged-on cashier without taskkill' -Setup { $script:processes=@() } -Runs 1 -Deletes 1
PosCase 'another installation is untouched while the standard POS starts' -Setup { $script:processes=@(ProcessInfo -exe 'C:\other\KassaPreh.exe') } -Runs 1 -Deletes 1
Assert (@($script:processes | Where-Object { $_.ExecutablePath -ceq 'C:\other\KassaPreh.exe' -and $_.ProcessId -eq 101 }).Count -eq 1) 'Another POS installation must remain untouched'
PosCase 'inactive POS with no logged-on user is skipped' -Setup { $script:processes=@(); $script:users=@() } -Skipped $true
PosCase 'inactive POS with multiple logged-on users is skipped' -Setup { $script:processes=@(); $script:users += [pscustomobject]@{ SessionId=3; Sid='S-1-5-21-999-999-999-9999'; Account='POSPC\OtherUser' } } -Skipped $true
PosCase 'inactive POS with the same user in two sessions is skipped' -Setup { $script:processes=@(); $script:users += [pscustomobject]@{ SessionId=3; Sid=$script:cashierSid; Account='POSPC\Cashier' } } -Skipped $true
PosCase 'session discovery failure is reported without WinRM-user fallback' -Setup { $script:processes=@(); $script:sessionError=$true } -Success $false -ErrorPattern 'session query failure'
PosCase 'session changed before initial start still removes task' -Setup { $script:processes=@(); $script:sessionChanged=$true } -Success $false -Deletes 1 -ErrorPattern 'Benutzersitzung hat sich'
PosCase 'initial start failure still removes task' -Setup { $script:processes=@(); $script:launchError=$true } -Success $false -Runs 1 -Deletes 1 -ErrorPattern 'interactive start failure'
PosCase 'initial start timeout still removes task' -Setup { $script:processes=@(); $script:noLaunch=$true } -Success $false -Runs 1 -Deletes 1 -ErrorPattern '30 Sekunden'
PosCase 'initial start cleanup failure is visible' -Setup { $script:processes=@(); $script:deleteFailures=3 } -Success $false -Runs 1 -Deletes 3 -ErrorPattern 'nicht gelöscht'
PosCase 'POS appears before initial launch and is not started twice' -Setup { $script:processes=@(); $script:appearsDuringPreparation=$true } -Deletes 1 -Changed $false
PosCase 'session zero is skipped' -Setup { $script:processes=@(ProcessInfo -session 0) } -Skipped $true
PosCase 'service-account process is skipped' -Setup { $script:processes=@(ProcessInfo -sid 'S-1-5-18') } -Skipped $true
PosCase 'ambiguous processes are never killed' -Setup { $script:processes += ProcessInfo -id 303 -session 3 } -Success $false -ErrorPattern 'Mehrere KassaPreh'
PosCase 'unreadable process path is never killed' -Setup { $script:processes[0].ExecutablePath=$null } -Success $false -ErrorPattern 'nicht lesbar'
PosCase 'inventory error is not a missing installation' -Setup { $script:inventoryError=$true } -Success $false -ErrorPattern 'inventory failure'
PosCase 'unreadable owner is never killed' -Setup { $script:ownerReturn=5 } -Success $false -ErrorPattern 'Benutzer.*nicht ermittelt'
PosCase 'ambiguous command line is never killed' -Setup { $script:processes[0].CommandLine='unrecognized --args' } -Success $false -ErrorPattern 'Startparameter'
PosCase 'scheduler access denied leaves POS running' -Setup { $script:schedulerError=$true } -Success $false -ErrorPattern 'Scheduler access denied'
PosCase 'registration failure leaves POS running' -Setup { $script:registrationError=$true } -Success $false -ErrorPattern 'registration failure'
PosCase 'partial registration is still cleaned' -Setup { $script:partialRegistration=$true } -Success $false -Deletes 1 -ErrorPattern 'after registration'
PosCase 'reused PID is never killed and task is removed' -Setup { $script:reusePid=$true } -Success $false -Deletes 1 -ErrorPattern 'Prozess hat sich'
PosCase 'taskkill rejection is reported and task removed' -Setup { $script:killExit=5 } -Success $false -Kills 1 -Deletes 1 -ErrorPattern 'Exitcode 5'
PosCase 'stuck process is not launched twice' -Setup { $script:stuck=$true } -Success $false -Kills 1 -Deletes 1 -ErrorPattern 'nicht beendet'
PosCase 'interactive launch failure still removes task' -Setup { $script:launchError=$true } -Success $false -Kills 1 -Runs 1 -Deletes 1 -ErrorPattern 'interactive start failure'
PosCase 'launch timeout still removes task' -Setup { $script:noLaunch=$true } -Success $false -Kills 1 -Runs 1 -Deletes 1 -ErrorPattern '30 Sekunden'
PosCase 'wrong session is not success' -Setup { $script:wrongSession=$true } -Success $false -Kills 1 -Runs 1 -Deletes 1 -ErrorPattern 'nicht.*bestätigt'
PosCase 'wrong owner is not success' -Setup { $script:wrongOwner=$true } -Success $false -Kills 1 -Runs 1 -Deletes 1 -ErrorPattern 'nicht.*bestätigt'
PosCase 'immediate POS crash is not success' -Setup { $script:crash=$true } -Success $false -Kills 1 -Runs 1 -Deletes 1 -ErrorPattern 'nicht.*bestätigt'
PosCase 'watchdog restart is verified without duplicate launch' -Setup { $script:watchdog=$true } -Kills 1 -Deletes 1
PosCase 'transient cleanup failure is retried' -Setup { $script:deleteFailures=2 } -Kills 1 -Runs 1 -Deletes 3
PosCase 'persistent cleanup failure reports exact task name' -Setup { $script:deleteFailures=3 } -Success $false -Kills 1 -Runs 1 -Deletes 3 -ErrorPattern 'NovaTouchUpdater-POS-[a-f0-9]{32}.*nicht gelöscht'
