. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
# Windows PowerShell 5.1. All services/remoting are simulated; only this test's
# GUID fixture below artifacts/test-results is changed. Dummy EXEs are text files, never executed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
function AssertLivePhase([string]$phase) {
    if (-not $script:checkLiveProgress) { return }
    $lastLine=@($script:workerOutput.ToString() -split '\r?\n' | Where-Object { $_.Trim() })[-1]
    $lastMessage=$lastLine | ConvertFrom-Json
    Assert ($lastMessage.Event -eq 'progress' -and $lastMessage.Phase -eq $phase) "Phase '$phase' must reach the UI before the operation starts, not after the remote call returns"
}
$workspace = $RepositoryRoot
$script:fixture = Join-Path $workspace ('artifacts\test-results\su-' + [guid]::NewGuid().ToString('N').Substring(0,8))
Assert ($script:fixture.StartsWith($workspace + '\artifacts\test-results\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture must remain below workspace artifacts/test-results'
[void][IO.Directory]::CreateDirectory($script:fixture)
. ([scriptblock]::Create((Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'UpdateSteps.ps1') -Raw -Encoding UTF8)))
. ([scriptblock]::Create((Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'UploadDirectory.ps1') -Raw -Encoding UTF8)))

function FixturePath([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    Assert ($full.StartsWith($script:fixture + '\', [StringComparison]::OrdinalIgnoreCase)) "Mutation outside fixture: $full"
    $full
}
function WriteFixture([string]$path, [string]$text) {
    $full = FixturePath $path
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
    [IO.File]::WriteAllText($full, $text)
}
function Get-Item {
    [CmdletBinding()] param([string]$LiteralPath, [switch]$Force)
    $item = Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force:$Force -ErrorAction Stop
    if ($LiteralPath -eq $script:reparsePath) {
        return [pscustomobject]@{ Attributes=($item.Attributes -bor [IO.FileAttributes]::ReparsePoint); PSIsContainer=$item.PSIsContainer; FullName=$item.FullName }
    }
    if ($LiteralPath.StartsWith($script:fixture + '\', [StringComparison]::OrdinalIgnoreCase) -and $item.Extension -eq '.exe') {
        $version = [IO.File]::ReadAllText($LiteralPath)
        return [pscustomobject]@{ VersionInfo=([pscustomobject]@{ ProductVersion=$version; FileVersion=$version });
            Attributes=$item.Attributes; PSIsContainer=$item.PSIsContainer; FullName=$item.FullName }
    }
    $item
}
function Get-Service {
    [CmdletBinding()] param([string]$Name)
    if (-not $Name) {
        if ($script:changedServiceSet) { return }
        return @($script:serviceStates.Values)
    }
    $match = @($script:serviceStates.Values | Where-Object { $_.Name -like $Name })
    Assert ($match.Count -eq 1) "Unexpected service query: $Name"
    $match[0]
}
function Stop-Service {
    [CmdletBinding()] param([string]$Name, [switch]$Force)
    AssertLivePhase 'Dienste stoppen'
    $service = Get-Service -Name $Name
    $script:events.Add('stop:' + $service.Name)
    if ($script:stopFailure -eq $service.Name) { throw 'Simulated stop failure' }
    $service.Status = 'Stopped'
}
function Start-Service {
    [CmdletBinding()] param([string]$Name)
    AssertLivePhase 'Dienste starten'
    $service = Get-Service -Name $Name
    $script:events.Add('start:' + $service.Name)
    if ($script:startFailure -eq $service.Name) { throw 'Simulated start failure' }
    $service.Status = 'Running'
}
function Remove-Item {
    [CmdletBinding()] param([string]$LiteralPath, [switch]$Recurse, [switch]$Force)
    $checked = FixturePath $LiteralPath
    $script:events.Add('delete:' + $checked)
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $checked -Recurse:$Recurse -Force:$Force -ErrorAction Stop
}
function Copy-Item {
    [CmdletBinding()] param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force, $ToSession)
    if ($null -eq $ToSession) { AssertLivePhase 'Update installieren' }
    $source = FixturePath $LiteralPath
    $target = FixturePath $Destination
    $script:events.Add($(if ($null -ne $ToSession) { 'transfer:' } else { 'copy:' }) + $target)
    if ($script:copyFailure -and $target.StartsWith($script:copyFailure + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Simulated copy failure' }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $source -Destination $target -Recurse:$Recurse -Force:$Force -ErrorAction Stop
}
function New-PSSessionOption { param($OpenTimeout, $OperationTimeout) @{} }
function New-PSSession {
    param($ComputerName, $Port, $SessionOption, $ErrorAction, [switch]$UseSSL)
    $expectedPort = if ($script:request.UseSsl) { $script:request.HttpsPort } else { 5985 }
    Assert ($Port -eq $expectedPort -and $UseSSL.IsPresent -eq $script:request.UseSsl) 'Updates must use the selected WinRM transport and port'
    [pscustomobject]@{ Id=1 }
}
function Invoke-Command { param($Session, $ScriptBlock, [object[]]$ArgumentList, $ErrorAction) & $ScriptBlock @ArgumentList }
function Remove-PSSession { param($Session, $ErrorAction) $script:sessionRemoved++ }

$source = Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'RemoteWorker.ps1') -Raw -Encoding UTF8
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
$script:realInventory = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'ReadInventory' }, $true).Body.GetScriptBlock()
function Get-ItemProperty {
    param($LiteralPath, $Name, $ErrorAction)
    $prefix='HKLM:\SYSTEM\CurrentControlSet\Services\'
    Assert ($LiteralPath.StartsWith($prefix) -and $Name -eq 'ImagePath') 'Unexpected registry query in integration test'
    $svc=@($script:installed | Where-Object Name -eq $LiteralPath.Substring($prefix.Length))
    Assert ($svc.Count -eq 1) 'Expected one registry ImagePath per service'
    [pscustomobject]@{ ImagePath=('"' + $svc[0].ExePath + '" --service') }
}
$readers = @{
    ReadInventory = @'
function ReadInventory {
    $script:inventoryReads++
    if ($script:useRealInventory) { & $script:realInventory; return }
    foreach ($svc in $script:installed) {
        if ($script:inventoryReads -gt 1 -and $script:missingAfter -eq $svc.Name) { continue }
        $version = if ($svc.Kind -eq 'serversuite') { [IO.File]::ReadAllText((Join-Path $svc.InstallDir 'version.txt')).Trim() }
            else { [IO.File]::ReadAllText($svc.ExePath) }
        if ($script:inventoryReads -gt 1 -and $script:staleAfter -eq $svc.Name) { $version = '2025.1.1' }
        $state = $script:serviceStates[$svc.Name].Status
        if ($script:inventoryReads -gt 1 -and $script:stoppedAfter -eq $svc.Name) { $state = 'Stopped' }
        [pscustomobject]@{ Name=$svc.Name; Kind=$svc.Kind; ExePath=$svc.ExePath; InstallDir=$svc.InstallDir;
            StartType=$svc.StartType; State=$state; ProductVersion=$version; FileVersion=$(if ($svc.Kind -eq 'serversuite') { '' } else { $version }) }
    }
}
'@
    ReadSoftwareInventory = 'function ReadSoftwareInventory { [pscustomobject]@{ DotNet48Installed=$true; PosVersion="Nicht installiert" } }'
    CopyRemoteFileWithProgress = @'
function CopyRemoteFileWithProgress($session, [string]$localFile, [string]$remoteFile, [string]$label) {
    Progress 'Upload test' $label 25
    Copy-Item -LiteralPath $localFile -Destination $remoteFile -ToSession $session -Force -ErrorAction Stop
    Progress 'Upload test' $label 99
}
'@
}
# Replace from the end to preserve AST offsets. The entire real update flow remains.
$nodes = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('ReadInventory','ReadSoftwareInventory','CopyRemoteFileWithProgress') }, $true))
foreach ($node in @($nodes | Sort-Object { $_.Extent.StartOffset } -Descending)) {
    $source = $source.Remove($node.Extent.StartOffset, $node.Extent.EndOffset - $node.Extent.StartOffset).Insert($node.Extent.StartOffset, $readers[$node.Name])
}
$source = $source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 0', 'return').Replace('exit 1', 'return')
$worker = [scriptblock]::Create($source)

function ResetCase([string]$name) {
    $script:case = Join-Path $script:fixture $name
    [void][IO.Directory]::CreateDirectory($script:case)
    $script:events = [Collections.Generic.List[string]]::new()
    $script:serviceStates = @{}; $script:installed = @(); $script:pythonRoots=@()
    $script:stopFailure=''; $script:startFailure=''; $script:copyFailure=''; $script:reparsePath=''
    $script:staleAfter=''; $script:stoppedAfter=''; $script:missingAfter=''
    $script:inventoryReads=0; $script:sessionRemoved=0; $script:changedServiceSet=$false
    $script:useRealInventory=$false
    $script:checkLiveProgress=$false; $script:workerOutput=$null
    $script:request = @{ Host='example.invalid'; Action='update'; Username=''; Password=''; UseSsl=$false;
        PaymentZip=''; PaymentVersion=''; PaymentSha256=''; FiscalZip=''; FiscalVersion=''; FiscalSha256='';
        ServerSuiteZip=''; ServerSuiteVersion='2026.4.9792'; ServerSuiteSha256='';
        RemoteInstallPath=(Join-Path $script:case 'remote'); SkipZipTransfer=$false; ForceUpdate=$false }
}
function Service([string]$suffix, [string]$instance, [string]$version='2026.3.9700', [string]$startType='Automatic', [string]$relativeExe='python.exe', [string]$fullName='') {
    $name = if ($fullName) { $fullName } else { 'nt3_server_suite_' + $suffix }
    $root = Join-Path $script:case ($instance + '\apps\nt3-server-suite')
    $exe = Join-Path $script:case ($instance + '\portable_python\' + $relativeExe)
    WriteFixture $exe '3.13.2'
    WriteFixture (Join-Path $root 'version.txt') $version
    WriteFixture (Join-Path $root 'obsolete.txt') 'keep local suite file'
    WriteFixture (Join-Path $script:case ($instance + '\apps\outside.txt')) 'must survive'
    WriteFixture (Join-Path $script:case ($instance + '\portable_python\python.exe')) '3.13.2'
    WriteFixture (Join-Path $script:case ($instance + '\portable_python\obsolete-python.txt')) 'keep local Python file'
    $script:pythonRoots=@($script:pythonRoots + (Join-Path $script:case ($instance + '\portable_python')) | Select-Object -Unique)
    WriteFixture (Join-Path $script:case ($instance + '\outside-root.txt')) 'must survive'
    $status = if ($startType -eq 'Disabled') { 'Stopped' } else { 'Running' }
    $service = [pscustomobject]@{ Name=$name; Status=$status; StartType=$startType }
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {
        param($status, $timeout)
        Assert ($this.Status -eq $status) "Service did not reach $status : $($this.Name)"
    }
    $script:serviceStates[$name] = $service
    $script:installed += [pscustomobject]@{ Name=$name; Kind='serversuite'; ExePath=$exe; InstallDir=$root;
        StartType=$startType; State=$status; ProductVersion=$version; FileVersion='' }
}
function Component([string]$kind) {
    # Existing payment/fiscal names may use suffixes other than a dot.
    $name = 'nt.' + $kind + '_shop'
    $root = Join-Path $script:case $kind
    $exe = Join-Path $root 'component.exe'
    WriteFixture $exe '1.0.0.0'
    WriteFixture (Join-Path $root 'settings.json') 'preserve local settings'
    WriteFixture (Join-Path $root 'nested\updated.txt') 'old'
    $service = [pscustomobject]@{ Name=$name; Status='Running'; StartType='Automatic' }
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($status,$timeout) Assert ($this.Status -eq $status) 'Unexpected component state' }
    $script:serviceStates[$name] = $service
    $script:installed += [pscustomobject]@{ Name=$name; Kind=$kind; ExePath=$exe; InstallDir=$root;
        StartType='Automatic'; State='Running'; ProductVersion='1.0.0.0'; FileVersion='1.0.0.0' }
}
function Package([hashtable]$entries=@{ 'apps/nt3-server-suite/suite.exe'='3.13.2'; 'apps/nt3-server-suite/version.txt'='2026.4.9792.0'; 'apps/nt3-server-suite/new.txt'='new'; 'apps/unrelated/no.txt'='never deploy' }, [string]$kind='serversuite', [switch]$WithoutPython) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    # Standard suite packages include both payloads. Invalid-package cases can
    # deliberately omit Python or provide their own incomplete runtime layout.
    if ($kind -eq 'serversuite' -and -not $WithoutPython -and @($entries.Keys | Where-Object { $_ -like 'installer/python/portable_python*' }).Count -eq 0) {
        $entries=$entries.Clone()
        $entries['installer/python/portable_python/python.exe']='3.14.0'
        $entries['installer/python/portable_python/Lib/runtime.py']='new Python library'
        $entries['installer/python/do-not-deploy.txt']='outside portable_python'
    }
    $filename = if ($kind -eq 'serversuite') { 'nt.server.suite-2026.4.9792.zip' } else { 'nt.' + $kind + '_1.2.3.4.zip' }
    $zip = Join-Path $script:case $filename
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($key in $entries.Keys) {
            $entry = $archive.CreateEntry($key)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write([string]$entries[$key]) } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    $prefix = if ($kind -eq 'serversuite') { 'ServerSuite' } elseif ($kind -eq 'payment') { 'Payment' } else { 'Fiscal' }
    $script:request[$prefix + 'Zip'] = $zip
    $script:request[$prefix + 'Sha256'] = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($kind -ne 'serversuite') { $script:request[$prefix + 'Version'] = '1.2.3.4' }
}
function RunWorker([bool]$success=$true, [string]$errorPattern='') {
    $script:requestJson = $script:request | ConvertTo-Json -Compress
    $writer = [IO.StringWriter]::new()
    $original = [Console]::Out
    $script:workerOutput=$writer
    try { [Console]::SetOut($writer); & $worker } finally { [Console]::SetOut($original); $script:workerOutput=$null }
    $messages = @($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $script:lastWorkerMessages=$messages
    $results = @($messages | Where-Object Event -eq 'result')
    Assert ($results.Count -eq 1) "Expected exactly one result: $($writer.ToString())"
    $result = $results[0]
    Assert ($result.Success -eq $success) "Unexpected result: $($result | ConvertTo-Json -Compress -Depth 5)"
    if ($errorPattern) { Assert ($result.Error -match $errorPattern) "Wrong error: $($result.Error)" }
    Assert ($script:sessionRemoved -eq 1) 'Remote session must always be closed'
    Assert (@(Get-ChildItem -LiteralPath $script:case -Filter '.novatouch-updater-stage-*' -Directory -Recurse).Count -eq 0) 'Staging must be removed after success or failure'
    $result
}
function NoServiceOrTargetChanges {
    Assert (@($script:events | Where-Object { $_ -match '^(stop|start|copy):' -or ($_ -match '^delete:' -and $_ -notmatch '\.novatouch-updater-stage-') }).Count -eq 0) 'Invalid payload must not stop services or change installed files'
    foreach ($svc in $script:installed) {
        $version = if ($svc.Kind -eq 'serversuite') { [IO.File]::ReadAllText((Join-Path $svc.InstallDir 'version.txt')) } else { [IO.File]::ReadAllText($svc.ExePath) }
        Assert ($version -eq $svc.ProductVersion) 'Original installation must survive validation failure'
    }
    foreach ($root in $script:pythonRoots) {
        Assert ([IO.File]::ReadAllText((Join-Path $root 'python.exe')) -eq '3.13.2' -and
            (Test-Path -LiteralPath (Join-Path $root 'obsolete-python.txt'))) 'Original Python installation must survive validation failure'
    }
}
function Passed([string]$message) { Write-Output ('PASS ' + $message) }

try {
    ResetCase 'multiple'
    $script:checkLiveProgress=$true
    Service 'a' 'one'; Service 'b' 'two' '2026.4.9792.0'; Service 'c' 'one'; Service 'disabled' 'three' '2026.3.9700' 'Disabled' 'Lib\site-packages\win32\PythonService.exe'
    Package @{ 'apps/nt3-server-suite/version.txt'='2026.4.9792.0'; 'apps/nt3-server-suite/new.txt'='new'; 'apps/unrelated/no.txt'='never deploy';
        'installer/python/portable_python/python.exe'='3.14.0'; 'installer/python/portable_python/Lib/runtime.py'='new Python library';
        'installer/python/portable_python/Lib/site-packages/win32/PythonService.exe'='3.14.0' }
    $result = RunWorker
    Assert ($result.Changed -and $result.Services.Count -eq 4) 'All suite instances must be returned and verified'
    Assert (@($script:events | Where-Object { $_ -match '^delete:.*\\(nt3-server-suite|portable_python)$' }).Count -eq 0) 'Client folders must never be deleted'
    $firstCopy = $script:events.IndexOf(@($script:events | Where-Object { $_ -like 'copy:*' })[0])
    $stops = @($script:events | Where-Object { $_ -like 'stop:*' })
    Assert ($stops.Count -eq 3) 'Every non-stopped suite instance, including current ones, must stop'
    foreach ($stop in $stops) { Assert ($script:events.IndexOf($stop) -lt $firstCopy) 'All services must stop before the first copy' }
    $lastCopy = @($script:events | Where-Object { $_ -like 'copy:*' })[-1]
    foreach ($start in @($script:events | Where-Object { $_ -like 'start:*' })) { Assert ($script:events.IndexOf($start) -gt $script:events.IndexOf($lastCopy)) 'All folders must be copied before any restart' }
    Assert ('start:nt3_server_suite_disabled' -notin $script:events -and $script:serviceStates['nt3_server_suite_disabled'].Status -eq 'Stopped') 'Disabled service must remain stopped'
    foreach ($instance in @('one','two','three')) {
        $apps = Join-Path $script:case ($instance + '\apps')
        Assert ([IO.File]::ReadAllText((Join-Path $apps 'nt3-server-suite\obsolete.txt')) -eq 'keep local suite file') 'Additional local suite files must be retained'
        Assert (Test-Path -LiteralPath (Join-Path $apps 'nt3-server-suite\new.txt')) 'New suite content must be deployed'
        Assert ([IO.File]::ReadAllText((Join-Path $apps 'outside.txt')) -eq 'must survive') 'Files outside the suite directory must survive'
        Assert (-not (Test-Path -LiteralPath (Join-Path $apps 'unrelated'))) 'Other ZIP apps must not be deployed'
        $installRoot = [IO.Path]::GetDirectoryName($apps)
        Assert ([IO.File]::ReadAllText((Join-Path $installRoot 'portable_python\python.exe')) -eq '3.14.0') 'Python must be replaced beside apps'
        Assert ([IO.File]::ReadAllText((Join-Path $installRoot 'portable_python\obsolete-python.txt')) -eq 'keep local Python file') 'Additional local Python files must be retained'
        Assert ([IO.File]::ReadAllText((Join-Path $installRoot 'portable_python\Lib\runtime.py')) -eq 'new Python library') 'New Python subdirectories must be copied'
        Assert (-not (Test-Path -LiteralPath (Join-Path $apps 'nt3-server-suite\portable_python'))) 'Python must not be copied inside the suite directory'
        Assert ([IO.File]::ReadAllText((Join-Path $installRoot 'outside-root.txt')) -eq 'must survive') 'Files beside apps and portable_python must survive'
        Assert (-not (Test-Path -LiteralPath (Join-Path $installRoot 'installer'))) 'Other installer content must not be deployed'
    }
    $versionCopies=@($script:events | Where-Object { $_ -match '^copy:.*\\nt3-server-suite\\version.txt$' })
    $pythonCopies=@($script:events | Where-Object { $_ -match '^copy:.*\\portable_python\\python.exe$' })
    Assert ($versionCopies.Count -eq 3 -and $pythonCopies.Count -eq 3) 'Shared suite and Python folders must receive each file once'
    Assert (@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Instanz vorgesehen' }).Count -eq 4 -and
        @($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Instanz gepr' + [char]0x00FC + 'ft' }).Count -eq 4) 'Every suite instance must appear in planning and verification logs'
    $copyMessages=@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Update installieren' })
    Assert (@($copyMessages | Where-Object { $_.Message -like '*Dateien werden installiert nach*' }).Count -eq 6) 'Every distinct suite and Python destination must be reported live during installation'
    Assert ($copyMessages[0].ProgressPercent -eq 0 -and $copyMessages[-1].ProgressPercent -eq 100) 'Copy progress must run from zero to 100 across all destinations'
    $previousPercent=-1
    foreach ($message in $copyMessages) {
        Assert ($message.ProgressPercent -ge $previousPercent -and $message.ProgressPercent -le 100) 'Copy progress must never move backwards between shared and separate folders'
        $previousPercent=$message.ProgressPercent
    }
    $stopMessages=@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Dienste stoppen' })
    $startMessages=@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Dienste starten' })
    Assert ($stopMessages[-1].ProgressPercent -eq 50 -and $startMessages[0].ProgressPercent -eq 50 -and $startMessages[-1].ProgressPercent -eq 100) 'Service progress must reach 50 only after every stop and 100 after every enabled restart'
    $extractMessages=@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Archive entpacken' })
    Assert ($extractMessages[0].ProgressPercent -eq 0 -and $extractMessages[-1].ProgressPercent -eq 100 -and
        $extractMessages[-1].Message -like '*nt.server.suite-2026.4.9792.zip fertig:*') 'Extraction progress must reach the UI with ZIP name and verified completion'
    Passed 'all instances copied and verified without deleting client folders or extra files; shared paths once'
    Passed 'stop, installation and start phases reach the UI before their operations execute'

    ResetCase 'real-inventory'; Service 'a' 'one' -fullName 'nt3_server_suite'; Service 'b' 'two' -fullName 'nt3_server_suiteRetail'; Service 'c' 'one' -fullName 'NT3_SERVER_SUITE_C'
    $script:useRealInventory=$true; Package; $result=RunWorker
    Assert ($result.Services.Count -eq 3 -and @($script:events | Where-Object { $_ -like 'stop:*' }).Count -eq 3 -and
        @($script:events | Where-Object { $_ -like 'start:*' }).Count -eq 3) 'Real inventory must route every prefix-matching service through stop/copy/start'
    foreach ($svc in $result.Services) {
        Assert ($svc.ProductVersion -eq '2026.4.9792.0' -and $svc.State -eq 'Running' -and
            [IO.File]::ReadAllText($svc.ExePath) -eq '3.14.0') 'Every EXE-derived installation must receive both payloads and be verified'
    }
    Passed 'real service-registry inventory through update: all nt3_server_suite prefixes and distinct EXE roots'

    ResetCase 'new-targets'; Service 'a' 'one'; Package; $script:useRealInventory=$true
    Remove-Item -LiteralPath (Join-Path $script:case 'one\apps') -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $script:case 'one\portable_python') -Recurse -Force
    $script:events.Clear()
    $result=RunWorker
    Assert ($result.Services[0].ProductVersion -eq '2026.4.9792.0' -and
        [IO.File]::ReadAllText((Join-Path $script:case 'one\portable_python\python.exe')) -eq '3.14.0') 'Registered service with missing target folders must create suite and Python directories'
    Assert (@($script:events | Where-Object { $_ -match '^delete:.*\\(nt3-server-suite|portable_python)$' }).Count -eq 0) 'Creating missing target folders must not delete client folders'
    Passed 'real inventory with missing version.txt and target folders creates both targets and verifies the service'

    ResetCase 'external-python'; Service 'a' 'one'; Service 'b' 'two'
    foreach ($svc in $script:installed) {
        $installRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($svc.InstallDir))
        $svc.ExePath=Join-Path $installRoot 'portable_python\python.exe'
    }
    Package @{ 'apps/nt3-server-suite/version.txt'='2026.4.9792'; 'apps/nt3-server-suite/main.py'='new suite code' }
    $result=RunWorker
    Assert ($result.Changed -and $result.Services.Count -eq 2) 'Both external-Python services must update'
    foreach ($svc in $script:installed) {
        Assert ([IO.File]::ReadAllText($svc.ExePath) -eq '3.14.0') 'Registered Python executable must be replaced at its original path'
        Assert ([IO.File]::ReadAllText((Join-Path $svc.InstallDir 'version.txt')) -eq '2026.4.9792') 'Suite version must still come from its own version.txt'
    }
    Passed 'external Python service executable is replaced; suite version.txt remains the version source'

    ResetCase 'missing-client-python'; Service 'a' 'one'; Package
    $pythonRoot=Join-Path $script:case 'one\portable_python'
    Remove-Item -LiteralPath $pythonRoot -Recurse -Force
    $script:events.Clear()
    $null=RunWorker
    Assert ([IO.File]::ReadAllText((Join-Path $pythonRoot 'python.exe')) -eq '3.14.0') 'Missing client runtime must be created at the sibling path'
    Passed 'missing client portable_python is installed from the ZIP'

    ResetCase 'file-folder-conflict'; Service 'a' 'one'; Package
    $pythonExe=Join-Path $script:case 'one\portable_python\python.exe'
    Remove-Item -LiteralPath $pythonExe -Force
    [void][IO.Directory]::CreateDirectory($pythonExe)
    $script:events.Clear()
    $null=RunWorker $false 'Datei kann keinen vorhandenen Ordner ersetzen'
    Assert ([IO.Directory]::Exists($pythonExe) -and -not [IO.File]::Exists((Join-Path $pythonExe 'python.exe'))) 'Conflicting directory must remain intact and must not receive a wrongly nested file'
    Passed 'file-versus-folder conflict reports an error and preserves the existing directory'

    ResetCase 'local-service-exes'
    Service 'HKS' 'one' -relativeExe 'nt3_server_suite_HKS.exe'
    Service 'Retail' 'two' -relativeExe 'Services\nt3_server_suite_Retail.exe'
    Service 'Shared' 'one' -relativeExe 'nt3_server_suite_Shared.exe'
    $script:useRealInventory=$true
    $serviceHashes=@{}
    foreach ($svc in $script:installed) { $serviceHashes[$svc.ExePath]=(Get-FileHash -LiteralPath $svc.ExePath -Algorithm SHA256).Hash }
    Package
    $result=RunWorker
    Assert ($result.Changed -and $result.Services.Count -eq 3) 'All instances must update without instance-specific service EXEs in the ZIP'
    foreach ($svc in $script:installed) {
        Assert ((Get-FileHash -LiteralPath $svc.ExePath -Algorithm SHA256).Hash -eq $serviceHashes[$svc.ExePath]) 'Service EXE absent from the ZIP must remain byte-identical'
        Assert (('copy:' + $svc.ExePath) -notin $script:events) 'Instance-specific EXE must not be overwritten without a matching ZIP file'
        Assert ([IO.File]::ReadAllText((Join-Path $svc.InstallDir 'version.txt')) -eq '2026.4.9792.0') 'Every suite root must receive the new version'
        Assert (('stop:' + $svc.Name) -in $script:events -and ('start:' + $svc.Name) -in $script:events) 'Each instance must stop and restart'
    }
    foreach ($root in $script:pythonRoots) {
        Assert ([IO.File]::ReadAllText((Join-Path $root 'python.exe')) -eq '3.14.0') 'Generic Python EXE must still be overwritten from the ZIP'
        Assert ([IO.File]::ReadAllText((Join-Path $root 'obsolete-python.txt')) -eq 'keep local Python file') 'Additional local runtime files must remain intact'
    }
    Assert (@($script:events | Where-Object { $_ -match '^delete:' -and $_ -notmatch '\.novatouch-updater-stage-' }).Count -eq 0) 'No installed client files or folders may be deleted'
    Passed 'installation-generated service EXEs absent from the ZIP survive updates of all separate and shared instances'

    foreach ($missingClientExe in @($false,$true)) {
        ResetCase ('payload-service-exe-' + $missingClientExe)
        Service 'HKS' 'one' -relativeExe 'nt3_server_suite_HKS.exe'
        $serviceExe=$script:installed[0].ExePath
        if ($missingClientExe) { Remove-Item -LiteralPath $serviceExe -Force; $script:events.Clear() }
        $script:useRealInventory=$true
        Package @{ 'apps/nt3-server-suite/version.txt'='2026.4.9792';
            'installer/python/portable_python/nt3_server_suite_HKS.exe'='updated service EXE' }
        $null=RunWorker
        Assert ([IO.File]::ReadAllText($serviceExe) -eq 'updated service EXE') 'A matching ZIP file must replace or create the service EXE'
        Assert (('copy:' + $serviceExe) -in $script:events) 'A matching service EXE must be copied from the ZIP'
        Passed ('matching ZIP service EXE is copied; missing client EXE: ' + $missingClientExe)
    }

    ResetCase 'service-exe-missing-both'
    Service 'HKS' 'one' -relativeExe 'nt3_server_suite_HKS.exe'
    Remove-Item -LiteralPath $script:installed[0].ExePath -Force
    $script:events.Clear()
    $script:useRealInventory=$true
    Package
    $null=RunWorker $false 'Dienst-EXE.*nt3_server_suite_HKS.*Client.*ZIP'
    NoServiceOrTargetChanges
    Passed 'service EXE missing from both client and ZIP fails before any service or installation changes'

    foreach ($badPython in @('missing','empty')) {
        ResetCase ('bad-python-' + $badPython); Service 'a' 'one'
        switch ($badPython) {
            'missing' { Package -WithoutPython; $pattern='installer.*python.*portable_python' }
            'empty' { Package @{ 'apps/nt3-server-suite/suite.exe'='3.13.2'; 'apps/nt3-server-suite/version.txt'='2026.4.9792'; 'installer/python/portable_python/'='' } -WithoutPython; $pattern='installer.*python.*portable_python' }
        }
        $null=RunWorker $false $pattern; NoServiceOrTargetChanges
        Passed ('invalid Python payload rejected before any service or installation changes: ' + $badPython)
    }

    foreach ($selectedKind in @('payment','fiscal','serversuite')) {
        foreach ($mode in @('normal','force','retry')) {
            $force = $mode -eq 'force'
            $retry = $mode -eq 'retry'
            ResetCase ($selectedKind + '-only-' + $mode)
            Service 'a' 'one'; Service 'b' 'two'; Component 'payment'; Component 'fiscal'
            if ($selectedKind -eq 'serversuite') { Package }
            else { Package @{ 'component.exe'='1.2.3.4'; 'nested/updated.txt'='new' } $selectedKind }
            # Stale version fields without a ZIP must never select another component.
            $script:request.PaymentVersion='1.2.3.4'; $script:request.FiscalVersion='1.2.3.4'
            $script:request.ForceUpdate=$force
            $script:request.RetryUpdate=$retry
            # Verify the complete update path over custom HTTPS as well as HTTP.
            $script:request.UseSsl=$force; $script:request.HttpsPort=443
            $expectedVersion = if ($selectedKind -eq 'serversuite') { '2026.4.9792.0' } else { '1.2.3.4' }
            $before = @{}
            foreach ($svc in $script:installed) {
                if (($force -or $retry) -and $svc.Kind -eq $selectedKind) {
                    $versionPath = if ($selectedKind -eq 'serversuite') { Join-Path $svc.InstallDir 'version.txt' } else { $svc.ExePath }
                    WriteFixture $versionPath $expectedVersion
                }
                foreach ($file in Get-ChildItem -LiteralPath $svc.InstallDir -File -Recurse -Force) { $before[$file.FullName] = [IO.File]::ReadAllText($file.FullName) }
            }
            Assert (@(@('PaymentZip','FiscalZip','ServerSuiteZip') | Where-Object { $script:request[$_] }).Count -eq 1) 'Exactly one ZIP must be selected in this case'
            $result=RunWorker
            Assert ($result.Changed) 'One selected ZIP must suffice for a successful update'
            $expectedEvents = @()
            foreach ($svc in $script:installed) {
                if ($svc.Kind -eq $selectedKind) {
                    $versionPath = if ($selectedKind -eq 'serversuite') { Join-Path $svc.InstallDir 'version.txt' } else { $svc.ExePath }
                    Assert ([IO.File]::ReadAllText($versionPath) -eq $expectedVersion) 'Selected component must reach its target version'
                    $expectedEvents += ('stop:' + $svc.Name), ('start:' + $svc.Name)
                } else {
                    foreach ($file in Get-ChildItem -LiteralPath $svc.InstallDir -File -Recurse -Force) {
                        Assert ($before.ContainsKey($file.FullName) -and [IO.File]::ReadAllText($file.FullName) -eq $before[$file.FullName]) 'Files of unselected components must remain unchanged'
                    }
                    foreach ($file in @($before.Keys | Where-Object { $_.StartsWith($svc.InstallDir + '\', [StringComparison]::OrdinalIgnoreCase) })) {
                        Assert (Test-Path -LiteralPath $file -PathType Leaf) 'Files of unselected components must not be deleted'
                    }
                }
            }
            $actualEvents = @($script:events | Where-Object { $_ -match '^(stop|start):' })
            Assert ($actualEvents.Count -eq $expectedEvents.Count -and @($actualEvents | Where-Object { $_ -notin $expectedEvents }).Count -eq 0) 'Only services of the selected component may stop or start'
            Assert (@($script:events | Where-Object { $_ -like 'transfer:*' }).Count -eq 1) 'Only the selected ZIP may be transferred'
            foreach ($instance in @('one','two')) {
                $pythonRoot = Join-Path $script:case ($instance + '\portable_python')
                $expectedPython = if ($selectedKind -eq 'serversuite') { '3.14.0' } else { '3.13.2' }
                Assert ([IO.File]::ReadAllText((Join-Path $pythonRoot 'python.exe')) -eq $expectedPython) 'Python must update only with a selected suite ZIP, including Force mode'
                Assert (Test-Path -LiteralPath (Join-Path $pythonRoot 'obsolete-python.txt')) 'Additional Python files must remain present for every update selection'
            }
            Passed ("single ZIP updates only $selectedKind (mode=$mode), leaving other services and files untouched")
        }
    }
    ResetCase 'combined'; Service 'a' 'one'; Component 'payment'; Component 'fiscal'; Package
    foreach ($kind in @('payment','fiscal')) { Package @{ 'component.exe'='1.2.3.4'; 'nested/updated.txt'='new'; 'nested/empty/'='' } $kind }
    $null=RunWorker
    foreach ($kind in @('payment','fiscal')) {
        $root = Join-Path $script:case $kind
        Assert ([IO.File]::ReadAllText((Join-Path $root 'component.exe')) -eq '1.2.3.4' -and [IO.File]::ReadAllText((Join-Path $root 'nested\updated.txt')) -eq 'new') 'Existing component ZIPs must still overlay nested files at the correct paths'
        Assert ([IO.File]::ReadAllText((Join-Path $root 'settings.json')) -eq 'preserve local settings') 'Existing payment/fiscal overlay behavior must retain extra files'
        Assert ([IO.Directory]::Exists((Join-Path $root 'nested\empty'))) 'Explicit ZIP directory entries must still be extracted and deployed'
    }
    $extractMessages=@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Archive entpacken' })
    Assert (@($extractMessages | Where-Object { $_.Message -like '*.zip fertig:*' -and $_.ProgressPercent -eq 100 }).Count -eq 3) 'Each extracted ZIP must report its own 100 percent completion'
    foreach ($zip in @($script:request.PaymentZip,$script:request.FiscalZip,$script:request.ServerSuiteZip)) {
        $filename=[IO.Path]::GetFileName($zip)
        $zipMessages=@($extractMessages | Where-Object { $_.Message.StartsWith($filename, [StringComparison]::OrdinalIgnoreCase) })
        Assert ($zipMessages[0].ProgressPercent -eq 0 -and $zipMessages[-1].ProgressPercent -eq 100) 'Each ZIP must restart extraction progress at zero'
        $previousPercent=-1
        foreach ($message in $zipMessages) {
            Assert ($message.ProgressPercent -ge $previousPercent -and $message.ProgressPercent -le 100) 'Extraction progress must stay monotonic within each ZIP'
            $previousPercent=$message.ProgressPercent
        }
    }
    Passed 'combined suite/payment/fiscal updates preserve existing overlay behavior'
    Passed 'extraction progress resets for each archive and handles explicit empty directories'

    ResetCase 'absent'; Package; $result = RunWorker
    Assert ($result.Skipped -and -not $result.Changed -and $script:events.Count -eq 0) 'Missing suite is optional and must not transfer or change anything'
    Passed 'absent suite is skipped without transfer'
    ResetCase 'current'; Service 'a' 'one' '2026.4.9792.0'; Package; $result = RunWorker
    Assert (-not $result.Changed -and $script:events.Count -eq 0) 'Current three/four-part version must avoid all update IO'
    Passed 'already current suite avoids all update operations'
    ResetCase 'force'; Service 'a' 'one' '2026.4.9792.0'; Package; $script:request.ForceUpdate=$true; $null=RunWorker
    Assert ('stop:nt3_server_suite_a' -in $script:events) 'Force must reinstall a selected current suite'
    Passed 'force update reinstalls current selected suite'

    foreach ($bad in @('missing-folder','missing-version','empty-version','version','traversal','empty','entry-conflict')) {
        ResetCase $bad; Service 'a' 'one'
        switch ($bad) {
            'missing-folder' { Package @{ 'suite.exe'='2026.4.9792' }; $pattern='apps.*nt3-server-suite' }
            'missing-version' { Package @{ 'apps/nt3-server-suite/suite.exe'='2026.4.9792' }; $pattern='keine.*version.txt' }
            'empty-version' { Package @{ 'apps/nt3-server-suite/suite.exe'='2026.4.9792'; 'apps/nt3-server-suite/version.txt'=" `r`n" }; $pattern='version.txt.*erwartet' }
            'version' { Package @{ 'apps/nt3-server-suite/suite.exe'='2026.4.9792'; 'apps/nt3-server-suite/version.txt'='2025.1.1' }; $pattern='2025.1.1.*2026.4.9792' }
            'traversal' { Package @{ '../outside.txt'='unsafe' }; $pattern='ZIP-Eintrag' }
            'empty' { Package @{} -WithoutPython; $pattern='leer|apps.*nt3-server-suite' }
            'entry-conflict' { Package @{ 'apps/nt3-server-suite/version.txt'='2026.4.9792'; 'apps/nt3-server-suite/version.txt/nested.txt'='conflict' }; $pattern='' }
        }
        $null = RunWorker $false $pattern
        NoServiceOrTargetChanges
        Assert (-not (Test-Path -LiteralPath (Join-Path $script:case 'outside.txt'))) 'ZIP traversal must not escape staging'
        if ($bad -eq 'entry-conflict') {
            Assert (@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Phase -eq 'Archive entpacken' -and $_.ProgressPercent -eq 100 }).Count -eq 0) 'Failed extraction must not report 100 percent'
        }
        Passed ('reject invalid ZIP before stopping services: ' + $bad)
    }
    ResetCase 'wrong-target'; Service 'a' 'one'; Package
    $wrong = Join-Path $script:case 'one\apps\different\suite.exe'; WriteFixture $wrong '2026.3.9700'
    WriteFixture (Join-Path ([IO.Path]::GetDirectoryName($wrong)) 'version.txt') '2026.3.9700'
    $script:installed[0].ExePath=$wrong; $script:installed[0].InstallDir=[IO.Path]::GetDirectoryName($wrong)
    $null = RunWorker $false 'Kein eindeutiger Ordner'; NoServiceOrTargetChanges
    Passed 'unrecognized installation path fails before stopping or deleting'
    ResetCase 'mismatched-inventory'; Service 'a' 'one'; Package
    $script:installed[0].ExePath=Join-Path $script:case 'other\portable_python\python.exe'
    WriteFixture $script:installed[0].ExePath '3.13.2'
    $null=RunWorker $false 'Dienstpfad und Server-Suite-Installationspfad'; NoServiceOrTargetChanges
    Passed 'inventory cannot redirect copies away from the registered service EXE installation'
    ResetCase 'wrong-apps-parent'; Service 'a' 'one'; Package
    $wrong=Join-Path $script:case 'one\other\nt3-server-suite'
    WriteFixture (Join-Path $wrong 'version.txt') '2026.3.9700'
    WriteFixture (Join-Path $wrong 'suite.exe') '3.13.2'
    $script:installed[0].InstallDir=$wrong; $script:installed[0].ExePath=Join-Path $wrong 'suite.exe'
    $null=RunWorker $false 'direkt unter apps'; NoServiceOrTargetChanges
    Passed 'nonstandard suite parent cannot derive an unintended Python deletion target'
    ResetCase 'changed-services'; Service 'a' 'one'; Package; $script:changedServiceSet=$true
    $null=RunWorker $false 'Server-Suite-Dienste'; NoServiceOrTargetChanges
    Passed 'service set changes abort before stopping or deleting'
    ResetCase 'linked-target'; Service 'a' 'one'; Package
    $script:reparsePath=Join-Path $script:case 'one\apps\nt3-server-suite'
    $null=RunWorker $false 'Junctions'; NoServiceOrTargetChanges
    Passed 'linked installation target is rejected before stopping or deleting'
    ResetCase 'linked-python'; Service 'a' 'one'; Package
    $script:reparsePath=Join-Path $script:case 'one\portable_python'
    $null=RunWorker $false 'Junctions'; NoServiceOrTargetChanges
    Passed 'linked Python target is rejected before stopping or deleting either installation'
    ResetCase 'overlap'; Service 'a' 'one'; Package
    $script:request.RemoteInstallPath=Join-Path $script:case 'one\apps\nt3-server-suite\download'
    $null=RunWorker $false 'Staging und Installationsziel'; NoServiceOrTargetChanges
    Passed 'staging inside installation target is rejected before stopping or deleting'
    ResetCase 'python-overlap'; Service 'a' 'one'; Package
    $script:request.RemoteInstallPath=Join-Path $script:case 'one\portable_python\download'
    $null=RunWorker $false 'Staging und Installationsziel'; NoServiceOrTargetChanges
    Passed 'staging inside Python target is rejected before stopping or deleting either installation'

    foreach ($fault in @('stop','copy','start')) {
        ResetCase ('failure-' + $fault); Service 'a' 'one'; Service 'b' 'two'; Package
        if ($fault -eq 'stop') { $script:stopFailure='nt3_server_suite_b' }
        if ($fault -eq 'copy') { $script:copyFailure=Join-Path $script:case 'one\apps\nt3-server-suite' }
        if ($fault -eq 'start') { $script:startFailure='nt3_server_suite_a' }
        $null = RunWorker $false ('Simulated ' + $fault + ' failure')
        Assert ('start:nt3_server_suite_a' -in $script:events -and 'start:nt3_server_suite_b' -in $script:events) 'Every service must receive a restart attempt even after failure'
        if ($fault -eq 'stop') { Assert (@($script:events | Where-Object { $_ -like 'copy:*' -or $_ -match '^delete:.*\\(nt3-server-suite|portable_python)$' }).Count -eq 0) 'Failed stop must prevent all installation changes' }
        Passed ('failure is reported and all restarts are attempted: ' + $fault)
    }
    ResetCase 'two-failures'; Service 'a' 'one'; Package
    $script:copyFailure=Join-Path $script:case 'one\apps\nt3-server-suite'; $script:startFailure='nt3_server_suite_a'
    $null=RunWorker $false 'Simulated copy failure.*Simulated start failure'
    Passed 'copy error is retained alongside a restart error'
    ResetCase 'python-copy-failure'; Service 'a' 'one'; Service 'b' 'two'; Package
    $script:copyFailure=Join-Path $script:case 'one\portable_python'
    $null=RunWorker $false 'Simulated copy failure'
    Assert ('start:nt3_server_suite_a' -in $script:events -and 'start:nt3_server_suite_b' -in $script:events) 'Python copy failure must still attempt to restart every suite service'
    Passed 'Python copy failure is reported and every suite service receives a restart attempt'
    foreach ($fault in @('stale','stopped','missing')) {
        ResetCase ('verification-' + $fault); Service 'a' 'one'; Service 'b' 'two'; Package
        Set-Variable -Scope Script -Name ($fault + 'After') -Value 'nt3_server_suite_b'
        $null=RunWorker $false $(switch ($fault) { 'stale' { 'Falsche Version.*nt3_server_suite_b' }; 'stopped' { 'Dienst l.uft nicht.*nt3_server_suite_b' }; 'missing' { 'Dienst verschwunden.*nt3_server_suite_b' } })
        Passed ('post-update verification covers every instance: ' + $fault)
    }
    foreach ($existing in @('identical','missing','different')) {
        ResetCase ('reuse-' + $existing); Service 'a' 'one'; Package; $script:request.SkipZipTransfer=$true
        if ($existing -ne 'missing') {
            $remoteZip = Join-Path $script:request.RemoteInstallPath ([IO.Path]::GetFileName($script:request.ServerSuiteZip))
            [void][IO.Directory]::CreateDirectory($script:request.RemoteInstallPath)
            if ($existing -eq 'identical') { Microsoft.PowerShell.Management\Copy-Item -LiteralPath $script:request.ServerSuiteZip -Destination (FixturePath $remoteZip) }
            else { WriteFixture $remoteZip 'wrong archive' }
        }
        if ($existing -eq 'identical') { $null=RunWorker } else { $null=RunWorker $false 'SHA256'; NoServiceOrTargetChanges }
        Assert (@($script:events | Where-Object { $_ -like 'transfer:*' }).Count -eq 0) 'Disabled ZIP transfer must never upload files'
        Passed ('reuse ZIP with SHA256 validation: ' + $existing)
    }

    ResetCase 'cleanup-handshake'
    WriteFixture (Join-Path $script:request.RemoteInstallPath 'old.zip') 'old upload'
    WriteFixture (Join-Path $script:request.RemoteInstallPath 'nested\keep.txt') 'local folder'
    $script:request.Action='check-upload-directory'
    $check=RunWorker
    Assert ($check.UploadDirectory.EntryCount -eq 2 -and $check.UploadDirectory.CanClean -and $script:events.Count -eq 0) 'Upload inspection must be read-only and return a confirmation snapshot'
    $firstSnapshot=$check.UploadDirectory.Snapshot
    $local=Join-Path $script:case 'new.zip'; WriteFixture $local 'new upload'
    $script:request.TransferFiles=@(@{LocalPath=$local;Sha256=(Get-FileHash -LiteralPath $local).Hash})
    $script:request.Action='transfer'; $script:sessionRemoved=0; $null=RunWorker
    Assert ([IO.File]::ReadAllText((Join-Path $script:request.RemoteInstallPath 'old.zip')) -eq 'old upload') 'Declining cleanup keeps existing files during upload'
    $script:request.Action='clean-upload-directory'; $script:request.ExpectedDirectorySnapshot=$firstSnapshot; $script:sessionRemoved=0
    $null=RunWorker $false 'seit der Abfrage'
    Assert ([IO.File]::Exists((Join-Path $script:request.RemoteInstallPath 'old.zip'))) 'Changed folders require a fresh confirmation'
    $script:request.Action='check-upload-directory'; $script:sessionRemoved=0; $check=RunWorker
    $script:request.Action='clean-upload-directory'; $script:request.ExpectedDirectorySnapshot=$check.UploadDirectory.Snapshot; $script:sessionRemoved=0
    $cleaned=RunWorker
    Assert ($cleaned.Changed -and $cleaned.UploadDirectory.EntryCount -eq 0 -and [IO.Directory]::Exists($script:request.RemoteInstallPath)) 'Explicitly confirmed cleanup empties the exact folder and preserves the directory'
    Passed 'upload directory check, keep, stale confirmation rejection and confirmed cleanup'

    foreach ($skipTransfer in @($false,$true)) {
        ResetCase ('no-cleanup-needed-' + $skipTransfer); Service 'a' 'one' '2026.4.9792.0'; Package
        $script:request.Action='check-upload-directory'; $script:request.CheckUpdateUpload=$true; $script:request.SkipZipTransfer=$skipTransfer
        $check=RunWorker
        Assert (-not $check.UploadRequired -and $script:events.Count -eq 0 -and -not [IO.Directory]::Exists($script:request.RemoteInstallPath)) 'Current updates and disabled transfers must never prepare or clean an upload folder'
    }
    Passed 'already current updates and disabled ZIP transfers bypass upload cleanup'

    foreach ($reuse in @($false,$true)) {
        ResetCase ('transfer-progress-' + $reuse)
        $script:request.Action='transfer'
        $transferFiles=@()
        foreach ($filename in @('first.zip','second.exe')) {
            $path=Join-Path $script:case $filename
            WriteFixture $path ('dummy transfer content: ' + $filename)
            $transferFiles += @{ LocalPath=$path; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
            if ($reuse) { WriteFixture (Join-Path $script:request.RemoteInstallPath $filename) ('dummy transfer content: ' + $filename) }
        }
        $script:request.TransferFiles=$transferFiles
        $result=RunWorker
        $verifiedMessages=@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Message -like 'Datei verifiziert:*' })
        Assert (($verifiedMessages.ProgressPercent -join ',') -eq '100,100') 'Every transferred or reused file reaches 100 only after hash verification'
        $fileStarts=@($script:lastWorkerMessages | Where-Object { $_.Event -eq 'progress' -and $_.Message -like 'Datei */* : *' -and $_.ProgressPercent -eq 0 })
        Assert ($fileStarts.Count -eq 2) 'Each upload file must restart its percentage at zero'
        Assert (@($script:events | Where-Object { $_ -like 'transfer:*' }).Count -eq $(if ($reuse) { 0 } else { 2 })) 'Progress reporting must retain hash-based transfer reuse'
        Assert (@($script:events | Where-Object { $_ -match '^(copy|stop|start):' }).Count -eq 0) 'Transfer progress must not cause installation or service operations'
        Passed ('verified transfer progress, reuse=' + $reuse)
    }
}
finally {
    # The resolved absolute GUID path was checked at creation; remove only it.
    Assert ($script:fixture.StartsWith($workspace + '\artifacts\test-results\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($script:fixture) -match '^su-[a-f0-9]{8}$') 'Invalid final fixture cleanup'
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $script:fixture -Recurse -Force -ErrorAction Stop
}
