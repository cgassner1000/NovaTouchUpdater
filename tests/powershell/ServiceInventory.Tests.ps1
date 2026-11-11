. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
. (Join-Path $PSScriptRoot '..\Shared\ServerSuiteRegistryFixture.ps1')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Assert($condition,[string]$message) { if(-not $condition){throw $message};Write-Output ('PASS '+$message) }
$source=ReplaceSuiteRegistryAccess ([IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'RemoteWorker.ps1')))
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
$reader=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ReadInventory'},$true).Body.GetScriptBlock()
foreach($name in @('Normalize-Version','IsTargetVersion','SelectUpdateServices')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
function Get-Service {
    [CmdletBinding()]param()
    if($script:providerError){throw 'simulated service provider error'}
    $script:services
}
function Get-ItemProperty {
    param($LiteralPath,$Name,$ErrorAction)
    $prefix='HKLM:\SYSTEM\CurrentControlSet\Services\'
    if(-not $LiteralPath.StartsWith($prefix) -or $Name -ne 'ImagePath'){throw 'Unexpected registry provider read'}
    [pscustomobject]@{ImagePath=$script:images[$LiteralPath.Substring($prefix.Length)]}
}
function Get-Item {
    param($LiteralPath,$ErrorAction)
    $script:exeReads.Add($LiteralPath)
    if(-not $script:versions.ContainsKey($LiteralPath)){throw 'Unexpected executable version read'}
    [pscustomobject]@{VersionInfo=$script:versions[$LiteralPath]}
}
function Get-Content { throw 'Installed version.txt must never be read' }
function Test-Path { throw 'Inventory must not search installation files' }
function Reset {
    $script:services=@();$script:images=@{};$script:versions=@{};$script:exeReads=[Collections.Generic.List[string]]::new();$script:providerError=$false
    [ServerSuiteRegistryFixture.Registry]::Reset()
}
function Service([string]$Name,[string]$Exe,[string]$Version='3.13.2') {
    $script:services += [pscustomobject]@{Name=$Name;Status='Running';StartType='Automatic'}
    $script:images[$Name]='"'+$Exe+'" --service "D:\Misleading\other.py"'
    $script:versions[$Exe]=[pscustomobject]@{ProductVersion=$Version;FileVersion=$Version}
}
function Registration([string]$Name,[string]$Root,[string]$Version,[string]$View='Registry64') {
    $path='SOFTWARE\Novacom\novatouch3\server.suite\'+$Name
    [ServerSuiteRegistryFixture.Registry]::Seed($View,$path,'InstallDir',$Root)
    [ServerSuiteRegistryFixture.Registry]::Seed($View,$path,'version',$Version)
}
function Fails([string]$Pattern) {
    $errorText='';try{$null=@(& $reader)}catch{$errorText=$_.Exception.Message}
    Assert ($errorText -match $Pattern) ('inventory rejects invalid source: '+$Pattern)
}
Reset
Service 'nt.payment.1' 'C:\Payment\payment.exe' '1.2.3.4';Service 'nt.fiscal.1' 'C:\Fiscal\fiscal.exe' '2.3.4.5'
Service 'nt3_server_suite_USER' 'C:\novacom\novatouch3\USER\portable_python\nt3_server_suite_USER.exe'
Registration 'USER' 'C:\novacom\novatouch3\USER' '2026.4.9809.25670'
Registration 'C4U1' 'C:\novacom\novatouch3\C4U1' '2026.4.9808.123'
$result=@(& $reader);$existing=@($result|Where-Object Name -eq 'nt3_server_suite_USER')[0];$missing=@($result|Where-Object Name -eq 'nt3_server_suite_C4U1')[0]
Assert ($result.Count -eq 4 -and $existing.ProductVersion -ceq '2026.4.9809.25670' -and $existing.RegistryInstallDir -ceq 'C:\novacom\novatouch3\USER' -and $existing.ServiceExists) 'registry version and InstallDir take precedence and an existing service is merged once'
Assert (-not $missing.ServiceExists -and $missing.State -eq 'Dienst fehlt' -and $missing.ExePath -eq '' -and $missing.InstallDir -ceq 'C:\novacom\novatouch3\C4U1\apps\nt3-server-suite') 'registry-only instance remains visible and repairable without inventing a service executable'
Assert ($script:exeReads.Count -eq 2 -and $result[0].ProductVersion -eq '1.2.3.4' -and $result[1].ProductVersion -eq '2.3.4.5' -and $existing.FileVersion -eq '') 'payment and fiscal retain EXE versions while suite reads neither Python nor installed text versions'
Assert ([ServerSuiteRegistryFixture.Registry]::Writes.Count -eq 0 -and [ServerSuiteRegistryFixture.Registry]::Disposed -gt 0) 'inventory is read-only and disposes all registry handles'
Registration 'C4U1' 'C:\novacom\novatouch3\C4U1' '2026.4.9809.25670'
$pending=@(SelectUpdateServices @(& $reader) @{serversuite='2026.4.9809.25670'} $false)
Assert ($pending.Count -eq 1 -and $pending[0].Name -eq 'nt3_server_suite_C4U1') 'a registry-only instance is repaired even at the target version while a running current USER instance is left alone'
Reset
Registration 'nt3_server_suite' 'D:\Default' '2026.4.1';Registration 'HKS' 'D:\Shared' '2026.4.2';Registration 'TAC' 'D:\Shared' '2026.4.3'
$result=@(& $reader)
Assert ($result.Count -eq 3 -and 'nt3_server_suite' -in $result.Name -and 'nt3_server_suite_HKS' -in $result.Name -and 'nt3_server_suite_TAC' -in $result.Name) 'default and named registry instances sharing a root remain distinct setup targets'
Reset
Registration 'USER' 'C:\Suite' '2026.4.2' 'Registry32'
$result=@(& $reader);Assert ($result.Count -eq 1 -and $result[0].RegistryView -eq 'Registry32') '32-bit registry registrations are discoverable independently of worker bitness'
Registration 'USER' 'C:\Suite' '2026.4.3' 'Registry64'
$result=@(& $reader);Assert ($result.Count -eq 1 -and $result[0].ProductVersion -eq '2026.4.3' -and $result[0].RegistryView -eq 'Registry64') 'duplicate registration is merged with native 64-bit version precedence'
Registration 'USER' 'D:\Different' '2026.4.2' 'Registry32';Fails 'Widerspr.chliche Registry-InstallDir'
Reset
Service 'nt3_server_suite_USER' 'D:\Other\portable_python\python.exe';Registration 'USER' 'C:\Suite' '2026.4.3'
$result=@(& $reader);Assert ($result[0].InstallDir -ceq 'C:\Suite\apps\nt3-server-suite' -and $result[0].ExePath -ceq 'D:\Other\portable_python\python.exe') 'registry root stays authoritative when a stale service path needs setup repair'
$script:images.nt3_server_suite_USER='broken image path';$result=@(& $reader)
Assert ($result[0].ServiceExists -and $result[0].ExePath -eq '' -and $result[0].RegistryInstallDir -eq 'C:\Suite') 'broken service image paths do not hide a registry instance from the repairing setup'
foreach($bad in @('relative\path','C:\','C:\Suite"bad')){Reset;Registration 'USER' $bad '2026.4.3';Fails 'InstallDir'}
Reset;Registration 'USER' 'C:\Suite' ''
$result=@(& $reader);Assert ($result[0].ProductVersion -eq 'Version unbekannt') 'missing registry version remains eligible for repair'
Reset
foreach($name in @('nt3_server_suite','nt3_server_suite_','nt3_server_suiteRetail')){Service $name 'C:\Legacy\portable_python\python.exe'}
$result=@(& $reader);Assert ($result.Count -eq 3 -and @($result|Where-Object ProductVersion -ne 'Version unbekannt').Count -eq 0 -and $script:exeReads.Count -eq 0) 'legacy services without installer entries remain visible with unknown registry version and no file fallback'
Reset;Service 'nt3_server_suite_USER' 'C:\Python\python.exe';Fails 'portable_python'
Reset;[ServerSuiteRegistryFixture.Registry]::Denied='Registry64';Fails 'access denied'
Reset;$script:providerError=$true;Fails 'service provider'
Reset;Assert (@(& $reader).Count -eq 0) 'absent optional services and registry instances produce an empty successful inventory'
