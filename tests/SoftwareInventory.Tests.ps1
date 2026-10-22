# Windows PowerShell 5.1; registry, services and remoting are mocked. No hosts are contacted.
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\RemoteWorker.ps1') -Raw -Encoding UTF8
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }

# Exercise the real scan/result/error flow and software detection, replacing only
# service inventory and external dependencies. Return instead of exiting this test.
$inventory = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ReadInventory' }, $true)
$mockInventory = @'
function ReadInventory {
    if ($script:inventoryError) { throw $script:inventoryError }
    if ($script:noServices) { return }
    [pscustomobject]@{ Name='nt.payment'; Kind='payment'; ProductVersion='1.2.3.4'; FileVersion='1.2.3.4'; State='Running' }
}
'@
$source = $source.Remove($inventory.Extent.StartOffset, $inventory.Extent.EndOffset - $inventory.Extent.StartOffset).Insert($inventory.Extent.StartOffset, $mockInventory)
$source = $source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 0', 'return').Replace('exit 1', 'return')
$worker = [scriptblock]::Create($source)

$frameworkPath = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
$framework32Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full'
$uninstallPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$uninstall32Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'

function Test-Path {
    param($LiteralPath, $ErrorAction)
    if ($LiteralPath -eq $script:unreadablePath) { throw 'Registry access denied (simulated)' }
    return $script:registry.ContainsKey($LiteralPath)
}
function Get-ItemProperty {
    param($LiteralPath, $ErrorAction)
    if (-not $script:registry.ContainsKey($LiteralPath)) { throw "Unexpected registry read: $LiteralPath" }
    return $script:registry[$LiteralPath]
}
function Get-ChildItem {
    param($LiteralPath, $ErrorAction)
    return $script:children[$LiteralPath]
}
function New-PSSessionOption { param($OpenTimeout, $OperationTimeout) return @{} }
function New-PSSession { param($ComputerName, $SessionOption, $ErrorAction) return [pscustomobject]@{ Id=1 } }
function Invoke-Command { param($Session, $ScriptBlock, $ErrorAction) & $ScriptBlock }
function Remove-PSSession { param($Session, $ErrorAction) $script:removed++ }
function Assert($condition, $message) { if (-not $condition) { throw $message } }

function Framework([long]$release, [switch]$Wow32) {
    $path = if ($Wow32) { $framework32Path } else { $frameworkPath }
    $script:registry[$path] = [pscustomobject]@{ Release=$release }
}
function Program([string]$name, [string]$version, [switch]$Wow32) {
    $root = if ($Wow32) { $uninstall32Path } else { $uninstallPath }
    $path = $root + '\test-' + $script:registry.Count
    $program = [pscustomobject]@{ DisplayName=$name }
    if ($version) { $program | Add-Member DisplayVersion $version }
    $script:registry[$path] = $program
    $script:children[$root] += [pscustomobject]@{ PSPath=$path }
}
function ScanCase($name, [scriptblock]$setup, $expectedFramework, $expectedPos, [bool]$success=$true, [string]$errorPattern='') {
    $script:registry = @{}
    $script:children = @{}
    foreach ($path in @($uninstallPath, $uninstall32Path)) {
        $script:registry[$path] = [pscustomobject]@{}
        $script:children[$path] = @()
    }
    $script:removed = 0
    $script:inventoryError = ''
    $script:noServices = $false
    $script:unreadablePath = ''
    $script:requestJson = '{"Action":"scan","Host":"example.invalid","UseSsl":false,"Username":"","Password":""}'
    & $setup
    $writer = [IO.StringWriter]::new()
    $original = [Console]::Out
    try { [Console]::SetOut($writer); & $worker }
    finally { [Console]::SetOut($original) }
    $events = @($writer.ToString() -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    $results = @($events | Where-Object Event -eq 'result')
    Assert ($results.Count -eq 1) "$name must emit exactly one result"
    $result = $results[0]
    Assert ($result.Success -eq $success) "$name unexpected scan status: $($result | ConvertTo-Json -Compress)"
    if ($null -eq $expectedFramework) {
        Assert ($null -eq $result.PSObject.Properties['DotNet48Installed']) "$name must not report a failed registry read as a missing framework"
        Assert ($null -eq $result.PSObject.Properties['PosVersion']) "$name must not report an unreadable POS inventory as absent"
    } else {
        Assert ($result.DotNet48Installed -is [bool]) "$name framework status must be a JSON boolean"
        Assert ($result.DotNet48Installed -eq $expectedFramework) "$name incorrect .NET detection"
        Assert ($result.PosVersion -eq $expectedPos) "$name incorrect POS version: $($result.PosVersion)"
    }
    if ($errorPattern) { Assert ($result.Error -match $errorPattern) "$name missing scan error details" }
    Assert ($script:removed -eq 1) "$name must close the remoting session"
    Write-Output "PASS $name"
}

ScanCase '4.8 and 64-bit POS' { Framework 528040; Program 'NovaTouch POS' '2.5.1234' } $true '2.5.1234'
ScanCase '4.8 OS-specific release' { Framework 528049 } $true 'Nicht installiert'
ScanCase '4.8.1 and 32-bit POS' { Framework 533320; Program 'NovaTouch POS' '2.5.5678' -Wow32 } $true '2.5.5678'
ScanCase 'below 4.8 threshold' { Framework 528039 } $false 'Nicht installiert'
ScanCase '4.7.2' { Framework 461808 } $false 'Nicht installiert'
ScanCase 'framework and POS absent' {} $false 'Nicht installiert'
ScanCase 'framework key without Release' { $script:registry[$frameworkPath] = [pscustomobject]@{} } $false 'Nicht installiert'
ScanCase 'framework in 32-bit registry' { Framework 528040 -Wow32 } $true 'Nicht installiert'
ScanCase 'POS without version' { Program 'NovaTouch POS' '' } $false 'Version unbekannt'
ScanCase 'case and whitespace in product name' { Program ' novatouch   pos ' '2.5.1' } $false '2.5.1'
ScanCase 'unrelated product is not POS' { Program 'NovaTouch POS Tools' '9.9'; Program 'Other POS' '1.0' } $false 'Nicht installiert'
ScanCase 'duplicate entries across registry views' { Program 'NovaTouch POS' '2.5.1'; Program 'NovaTouch POS' '2.5.1' -Wow32 } $false '2.5.1'
ScanCase 'different installed versions remain visible' { Program 'NovaTouch POS' '2.5.2'; Program 'NovaTouch POS' '2.5.1' -Wow32 } $false '2.5.1, 2.5.2'
ScanCase 'registry error remains unknown' { $script:unreadablePath = $uninstall32Path } $null $null $false 'Registry access denied'
ScanCase 'software results survive service read failure' { Framework 528040; Program 'NovaTouch POS' '2.5.1'; $script:inventoryError='Service inventory failed' } $true '2.5.1' $false 'Service inventory failed'
ScanCase 'software results survive missing services' { Framework 528040; $script:noServices=$true } $true 'Nicht installiert' $false 'Keine nt.payment'
