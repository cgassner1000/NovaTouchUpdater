# Only the real inventory function runs. Services, registry and file reads are mocked.
$ErrorActionPreference = 'Stop'
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\RemoteWorker.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
$reader = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ReadInventory' }, $true).Body.GetScriptBlock()
function Get-Service {
    [CmdletBinding()] param()
    if ($script:providerError) { Write-Error 'Simulated service provider error'; return }
    $script:services
}
function Get-ItemProperty { param($LiteralPath, $Name, $ErrorAction)
    $prefix = 'HKLM:\SYSTEM\CurrentControlSet\Services\'
    Assert ($LiteralPath.StartsWith($prefix) -and $Name -eq 'ImagePath') 'Only the service image path may be read'
    $key = $LiteralPath.Substring($prefix.Length)
    Assert ($script:images.ContainsKey($key)) "Unexpected service registry read: $key"
    [pscustomobject]@{ ImagePath=$script:images[$key] }
}
function Get-Item { param($LiteralPath, $ErrorAction)
    $script:exeReads += $LiteralPath
    if (-not $script:versions.ContainsKey($LiteralPath)) { throw 'Simulated missing service executable' }
    [pscustomobject]@{ VersionInfo=$script:versions[$LiteralPath] }
}
function Test-Path { param($LiteralPath, $PathType, $ErrorAction) $script:directories.ContainsKey($LiteralPath) }
function Get-Content { param($LiteralPath, [switch]$Raw, $Encoding, $ErrorAction)
    if (-not $script:versionTexts.ContainsKey($LiteralPath)) { throw 'Simulated missing or unreadable version.txt' }
    $script:versionTexts[$LiteralPath]
}
function Reset {
    $script:services=@(); $script:images=@{}; $script:versions=@{}; $script:versionTexts=@{}; $script:directories=@{}; $script:exeReads=@(); $script:providerError=$false
}
function SuiteVersion([string]$root, [string]$text) {
    $script:directories[$root]=$true
    $script:versionTexts[([IO.Path]::Combine($root, 'version.txt'))]=$text
}
function Fails([string]$pattern) {
    $errorText=''
    try { $null=@(& $reader) } catch { $errorText=$_.Exception.Message }
    Assert ($errorText -match $pattern) "Expected inventory error '$pattern', got '$errorText'"
}
function Service([string]$name, [string]$exe, [string]$version, [string]$fileVersion='') {
    $script:services += [pscustomobject]@{ Name=$name; Status='Running'; StartType='Automatic' }
    $script:images[$name] = '"' + $exe + '" --service'
    $script:versions[$exe] = [pscustomobject]@{ ProductVersion=$version; FileVersion=$fileVersion }
}
Reset
Service 'nt.payment.1' 'C:\payment\payment.exe' '1.2.3.4'
Service 'nt.fiscal.1' 'C:\fiscal\fiscal.exe' '2.3.4.5'
Service 'nt3_server_suite_4711' 'C:\Program Files\SuiteA\apps\nt3-server-suite\python\python.exe' '3.13.2'
Service 'NT3_SERVER_SUITE_SHOP_B' 'D:\SuiteB\apps\nt3-server-suite\suite.exe' '3.12.1'
SuiteVersion 'C:\Program Files\SuiteA\apps\nt3-server-suite' " 2026.4.9792`r`n"
SuiteVersion 'D:\SuiteB\apps\nt3-server-suite' 'Version: 2026.3.9700'
Service 'unrelated' 'C:\other\other.exe' '9.9.9'
Service 'nt3_server_suite_' 'C:\empty-suffix\suite.exe' '9.9.9'
Service 'nt3_server_suite' 'C:\no-suffix\suite.exe' '9.9.9'
$result = @(& $reader)
Assert ($result.Count -eq 4) 'Inventory must include payment, fiscal and both named server-suite instances only'
$suite = @($result | Where-Object Kind -eq 'serversuite')
Assert ($suite.Count -eq 2 -and $suite[0].Name -eq 'nt3_server_suite_4711' -and $suite[1].Name -eq 'NT3_SERVER_SUITE_SHOP_B') 'Host-specific suffixes and case must not lose suite instances'
Assert ($suite[0].InstallDir -ceq 'C:\Program Files\SuiteA\apps\nt3-server-suite' -and $suite[1].InstallDir -ceq 'D:\SuiteB\apps\nt3-server-suite' -and
    $suite[0].ProductVersion -ceq '2026.4.9792' -and $suite[1].ProductVersion -ceq '2026.3.9700') 'Every suite instance must retain its own directory and version.txt version'
Assert ($suite[0].FileVersion -eq '' -and $suite[1].FileVersion -eq '' -and $script:exeReads.Count -eq 2) 'Suite inventory must never read or fall back to Python EXE versions'
Assert ($result[0].ProductVersion -eq '1.2.3.4' -and $result[1].ProductVersion -eq '2.3.4.5') 'Payment/fiscal EXE version detection must be unchanged'
Write-Output 'PASS per-instance version.txt takes precedence over portable Python; payment/fiscal unchanged'
Reset
Service 'nt3_server_suite_1' 'C:\SharedSuite\apps\nt3-server-suite\suite.exe' '3.13.2'
Service 'nt3_server_suite_2' 'C:\SharedSuite\apps\nt3-server-suite\suite.exe' '3.13.2'
SuiteVersion 'C:\SharedSuite\apps\nt3-server-suite' '2026.4.9792'
$result = @(& $reader)
Assert ($result.Count -eq 2 -and $result[0].InstallDir -ceq $result[1].InstallDir) 'Services sharing one installation must still remain two inventory records'
Write-Output 'PASS suite instances sharing the same EXE are preserved'
Reset
Service 'nt3_server_suite_a' 'C:\Python Portable\python.exe' '3.13.2'
Service 'nt3_server_suite_b' 'C:\Python Portable\python.exe' '3.13.2'
$script:images['nt3_server_suite_a']='"C:\Python Portable\python.exe" "D:\Shop A\apps\nt3-server-suite\main.py"'
$script:images['nt3_server_suite_b']='C:\Python Portable\python.exe D:\ShopB\apps\nt3-server-suite\main.py'
SuiteVersion 'D:\Shop A\apps\nt3-server-suite' '2026.4.9792'
SuiteVersion 'D:\ShopB\apps\nt3-server-suite' '2026.3.9700'
$result=@(& $reader)
Assert ($result[0].ProductVersion -eq '2026.4.9792' -and $result[1].ProductVersion -eq '2026.3.9700' -and $script:exeReads.Count -eq 0) 'Shared external Python must resolve each instance from its own service arguments'
Write-Output 'PASS external shared Python with different suite folders and quoted/unquoted arguments'
Reset
Service 'nt3_server_suite_a' 'C:\ShopA\apps\python\python.exe' '3.13.2'
Service 'nt3_server_suite_b' 'D:\ShopB\portable_python\python.exe' '3.13.2'
SuiteVersion 'C:\ShopA\apps\nt3-server-suite' '2026.4.9792'
SuiteVersion 'D:\ShopB\apps\nt3-server-suite' '2026.3.9700'
$result=@(& $reader)
Assert ($result[0].ProductVersion -eq '2026.4.9792' -and $result[1].ProductVersion -eq '2026.3.9700') 'Portable Python beside apps must use the matching installation root'
Write-Output 'PASS sibling portable Python resolves apps/nt3-server-suite/version.txt for each installation'
foreach ($invalid in @('missing','empty','invalid')) {
    Reset
    Service 'nt3_server_suite_a' 'C:\ShopA\apps\nt3-server-suite\python.exe' '3.13.2'
    if ($invalid -ne 'missing') { SuiteVersion 'C:\ShopA\apps\nt3-server-suite' $(if ($invalid -eq 'empty') { " `r`n" } else { 'unavailable' }) }
    Fails 'nt3_server_suite_a.*version.txt'
    Assert ($script:exeReads.Count -eq 0) 'Invalid version.txt must not trigger an EXE version fallback'
    Write-Output ('PASS missing/invalid version.txt remains an explicit error without Python fallback: ' + $invalid)
}
Reset
Service 'nt3_server_suite_a' 'C:\Python\python.exe' '3.13.2'
$script:images['nt3_server_suite_a']='"C:\Python\python.exe" "D:\A\apps\nt3-server-suite\main.py" "D:\B\apps\nt3-server-suite\config.ini"'
Fails 'Mehrere Server-Suite-Ordner'
Write-Output 'PASS ambiguous suite paths are rejected'
Reset
Assert (@(& $reader).Count -eq 0) 'No optional components must produce an empty, successful inventory'
Write-Output 'PASS absent components remain optional'
