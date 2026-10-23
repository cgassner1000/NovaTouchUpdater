# Windows PowerShell 5.1. All services/remoting are simulated; only this test's
# GUID fixture below obj is changed. Dummy EXEs are text files, never executed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:fixture = Join-Path $workspace ('obj\suite-update-test-' + [guid]::NewGuid().ToString('N'))
Assert ($script:fixture.StartsWith($workspace + '\obj\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture must remain below workspace obj'
[void][IO.Directory]::CreateDirectory($script:fixture)
. ([scriptblock]::Create((Get-Content -LiteralPath (Join-Path $workspace 'UpdateSteps.ps1') -Raw -Encoding UTF8)))

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
    $service = Get-Service -Name $Name
    $script:events.Add('stop:' + $service.Name)
    if ($script:stopFailure -eq $service.Name) { throw 'Simulated stop failure' }
    $service.Status = 'Stopped'
}
function Start-Service {
    [CmdletBinding()] param([string]$Name)
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
    $source = FixturePath $LiteralPath
    $target = FixturePath $Destination
    $script:events.Add($(if ($null -ne $ToSession) { 'transfer:' } else { 'copy:' }) + $target)
    if ($script:copyFailure -and $target.StartsWith($script:copyFailure + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Simulated copy failure' }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $source -Destination $target -Recurse:$Recurse -Force:$Force -ErrorAction Stop
}
function New-PSSessionOption { param($OpenTimeout, $OperationTimeout) @{} }
function New-PSSession { param($ComputerName, $SessionOption, $ErrorAction) [pscustomobject]@{ Id=1 } }
function Invoke-Command { param($Session, $ScriptBlock, [object[]]$ArgumentList, $ErrorAction) & $ScriptBlock @ArgumentList }
function Remove-PSSession { param($Session, $ErrorAction) $script:sessionRemoved++ }

$source = Get-Content -LiteralPath (Join-Path $workspace 'RemoteWorker.ps1') -Raw -Encoding UTF8
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
$readers = @{
    ReadInventory = @'
function ReadInventory {
    $script:inventoryReads++
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
}
# Replace from the end to preserve AST offsets. The entire real update flow remains.
$nodes = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('ReadInventory','ReadSoftwareInventory') }, $true))
foreach ($node in @($nodes | Sort-Object { $_.Extent.StartOffset } -Descending)) {
    $source = $source.Remove($node.Extent.StartOffset, $node.Extent.EndOffset - $node.Extent.StartOffset).Insert($node.Extent.StartOffset, $readers[$node.Name])
}
$source = $source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 0', 'return').Replace('exit 1', 'return')
$worker = [scriptblock]::Create($source)

function ResetCase([string]$name) {
    $script:case = Join-Path $script:fixture $name
    [void][IO.Directory]::CreateDirectory($script:case)
    $script:events = [Collections.Generic.List[string]]::new()
    $script:serviceStates = @{}; $script:installed = @()
    $script:stopFailure=''; $script:startFailure=''; $script:copyFailure=''; $script:reparsePath=''
    $script:staleAfter=''; $script:stoppedAfter=''; $script:missingAfter=''
    $script:inventoryReads=0; $script:sessionRemoved=0; $script:changedServiceSet=$false
    $script:request = @{ Host='example.invalid'; Action='update'; Username=''; Password=''; UseSsl=$false;
        PaymentZip=''; PaymentVersion=''; PaymentSha256=''; FiscalZip=''; FiscalVersion=''; FiscalSha256='';
        ServerSuiteZip=''; ServerSuiteVersion='2026.4.9792'; ServerSuiteSha256='';
        RemoteInstallPath=(Join-Path $script:case 'remote'); SkipZipTransfer=$false; ForceUpdate=$false }
}
function Service([string]$suffix, [string]$instance, [string]$version='2026.3.9700', [string]$startType='Automatic', [string]$relativeExe='suite.exe') {
    $name = 'nt3_server_suite_' + $suffix
    $root = Join-Path $script:case ($instance + '\apps\nt3-server-suite')
    $exe = Join-Path $root $relativeExe
    WriteFixture $exe '3.13.2'
    WriteFixture (Join-Path $root 'version.txt') $version
    WriteFixture (Join-Path $root 'obsolete.txt') 'must be removed'
    WriteFixture (Join-Path $script:case ($instance + '\apps\outside.txt')) 'must survive'
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
function Package([hashtable]$entries=@{ 'apps/nt3-server-suite/suite.exe'='3.13.2'; 'apps/nt3-server-suite/version.txt'='2026.4.9792.0'; 'apps/nt3-server-suite/new.txt'='new'; 'apps/unrelated/no.txt'='never deploy' }, [string]$kind='serversuite') {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
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
    try { [Console]::SetOut($writer); & $worker } finally { [Console]::SetOut($original) }
    $messages = @($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
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
}
function Passed([string]$message) { Write-Output ('PASS ' + $message) }

try {
    ResetCase 'multiple'
    Service 'a' 'one'; Service 'b' 'two' '2026.4.9792.0'; Service 'c' 'one'; Service 'disabled' 'three' '2026.3.9700' 'Disabled' 'bin\suite.exe'
    Package @{ 'apps/nt3-server-suite/suite.exe'='3.13.2'; 'apps/nt3-server-suite/bin/suite.exe'='3.13.2'; 'apps/nt3-server-suite/version.txt'='2026.4.9792.0'; 'apps/nt3-server-suite/new.txt'='new'; 'apps/unrelated/no.txt'='never deploy' }
    $result = RunWorker
    Assert ($result.Changed -and $result.Services.Count -eq 4) 'All suite instances must be returned and verified'
    $targetDeletes = @($script:events | Where-Object { $_ -match '^delete:.*\\nt3-server-suite$' })
    Assert ($targetDeletes.Count -eq 3) 'Shared suite directory must be replaced once'
    $firstDelete = $script:events.IndexOf($targetDeletes[0])
    $stops = @($script:events | Where-Object { $_ -like 'stop:*' })
    Assert ($stops.Count -eq 3) 'Every non-stopped suite instance, including current ones, must stop'
    foreach ($stop in $stops) { Assert ($script:events.IndexOf($stop) -lt $firstDelete) 'All services must stop before the first deletion' }
    $lastCopy = @($script:events | Where-Object { $_ -like 'copy:*' })[-1]
    foreach ($start in @($script:events | Where-Object { $_ -like 'start:*' })) { Assert ($script:events.IndexOf($start) -gt $script:events.IndexOf($lastCopy)) 'All folders must be copied before any restart' }
    Assert ('start:nt3_server_suite_disabled' -notin $script:events -and $script:serviceStates['nt3_server_suite_disabled'].Status -eq 'Stopped') 'Disabled service must remain stopped'
    foreach ($instance in @('one','two','three')) {
        $apps = Join-Path $script:case ($instance + '\apps')
        Assert (-not (Test-Path -LiteralPath (Join-Path $apps 'nt3-server-suite\obsolete.txt'))) 'Old suite files must be removed'
        Assert (Test-Path -LiteralPath (Join-Path $apps 'nt3-server-suite\new.txt')) 'New suite content must be deployed'
        Assert ([IO.File]::ReadAllText((Join-Path $apps 'outside.txt')) -eq 'must survive') 'Files outside the suite directory must survive'
        Assert (-not (Test-Path -LiteralPath (Join-Path $apps 'unrelated'))) 'Other ZIP apps must not be deployed'
    }
    Passed 'all instances, shared paths, nested EXEs, disabled services and exact folder replacement'

    ResetCase 'external-python'; Service 'a' 'one'; Service 'b' 'two'
    $python = Join-Path $script:case 'portable-python\python.exe'
    WriteFixture $python '3.13.2'
    foreach ($svc in $script:installed) { $svc.ExePath=$python }
    Package @{ 'apps/nt3-server-suite/version.txt'='2026.4.9792'; 'apps/nt3-server-suite/main.py'='new suite code' }
    $result=RunWorker
    Assert ($result.Changed -and $result.Services.Count -eq 2 -and [IO.File]::ReadAllText($python) -eq '3.13.2') 'An external shared Python EXE must remain untouched while every suite instance updates'
    foreach ($svc in $script:installed) { Assert ([IO.File]::ReadAllText((Join-Path $svc.InstallDir 'version.txt')) -eq '2026.4.9792') 'Each instance must receive the suite version.txt' }
    Passed 'external Python is not required in the ZIP; each suite version.txt is deployed and verified'

    foreach ($selectedKind in @('payment','fiscal','serversuite')) {
        foreach ($force in @($false,$true)) {
            ResetCase ($selectedKind + '-only-force-' + $force)
            Service 'a' 'one'; Service 'b' 'two'; Component 'payment'; Component 'fiscal'
            if ($selectedKind -eq 'serversuite') { Package }
            else { Package @{ 'component.exe'='1.2.3.4'; 'nested/updated.txt'='new' } $selectedKind }
            # Stale version fields without a ZIP must never select another component.
            $script:request.PaymentVersion='1.2.3.4'; $script:request.FiscalVersion='1.2.3.4'
            $script:request.ForceUpdate=$force
            $expectedVersion = if ($selectedKind -eq 'serversuite') { '2026.4.9792.0' } else { '1.2.3.4' }
            $before = @{}
            foreach ($svc in $script:installed) {
                if ($force -and $svc.Kind -eq $selectedKind) {
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
            Passed ("single ZIP updates only $selectedKind (Force=$force), leaving other services and files untouched")
        }
    }
    ResetCase 'combined'; Service 'a' 'one'; Component 'payment'; Component 'fiscal'; Package
    foreach ($kind in @('payment','fiscal')) { Package @{ 'component.exe'='1.2.3.4'; 'nested/updated.txt'='new' } $kind }
    $null=RunWorker
    foreach ($kind in @('payment','fiscal')) {
        $root = Join-Path $script:case $kind
        Assert ([IO.File]::ReadAllText((Join-Path $root 'component.exe')) -eq '1.2.3.4' -and [IO.File]::ReadAllText((Join-Path $root 'nested\updated.txt')) -eq 'new') 'Existing component ZIPs must still overlay nested files at the correct paths'
        Assert ([IO.File]::ReadAllText((Join-Path $root 'settings.json')) -eq 'preserve local settings') 'Existing payment/fiscal overlay behavior must retain extra files'
    }
    Passed 'combined suite/payment/fiscal updates preserve existing overlay behavior'

    ResetCase 'absent'; Package; $result = RunWorker
    Assert ($result.Skipped -and -not $result.Changed -and $script:events.Count -eq 0) 'Missing suite is optional and must not transfer or change anything'
    Passed 'absent suite is skipped without transfer'
    ResetCase 'current'; Service 'a' 'one' '2026.4.9792.0'; Package; $result = RunWorker
    Assert (-not $result.Changed -and $script:events.Count -eq 0) 'Current three/four-part version must avoid all update IO'
    Passed 'already current suite avoids all update operations'
    ResetCase 'force'; Service 'a' 'one' '2026.4.9792.0'; Package; $script:request.ForceUpdate=$true; $null=RunWorker
    Assert ('stop:nt3_server_suite_a' -in $script:events) 'Force must reinstall a selected current suite'
    Passed 'force update reinstalls current selected suite'

    foreach ($bad in @('missing-folder','missing-exe','missing-version','empty-version','version','traversal','empty')) {
        ResetCase $bad; Service 'a' 'one'
        switch ($bad) {
            'missing-folder' { Package @{ 'suite.exe'='2026.4.9792' }; $pattern='apps.*nt3-server-suite' }
            'missing-exe' { Package @{ 'apps/nt3-server-suite/other.exe'='3.13.2'; 'apps/nt3-server-suite/version.txt'='2026.4.9792' }; $pattern='Dienst-EXE' }
            'missing-version' { Package @{ 'apps/nt3-server-suite/suite.exe'='2026.4.9792' }; $pattern='keine.*version.txt' }
            'empty-version' { Package @{ 'apps/nt3-server-suite/suite.exe'='2026.4.9792'; 'apps/nt3-server-suite/version.txt'=" `r`n" }; $pattern='version.txt.*erwartet' }
            'version' { Package @{ 'apps/nt3-server-suite/suite.exe'='2026.4.9792'; 'apps/nt3-server-suite/version.txt'='2025.1.1' }; $pattern='2025.1.1.*2026.4.9792' }
            'traversal' { Package @{ '../outside.txt'='unsafe' }; $pattern='ZIP-Eintrag' }
            'empty' { Package @{}; $pattern='leer|apps.*nt3-server-suite' }
        }
        $null = RunWorker $false $pattern
        NoServiceOrTargetChanges
        Assert (-not (Test-Path -LiteralPath (Join-Path $script:case 'outside.txt'))) 'ZIP traversal must not escape staging'
        Passed ('reject invalid ZIP before stopping services: ' + $bad)
    }
    ResetCase 'wrong-target'; Service 'a' 'one'; Package
    $wrong = Join-Path $script:case 'one\apps\different\suite.exe'; WriteFixture $wrong '2026.3.9700'
    WriteFixture (Join-Path ([IO.Path]::GetDirectoryName($wrong)) 'version.txt') '2026.3.9700'
    $script:installed[0].ExePath=$wrong; $script:installed[0].InstallDir=[IO.Path]::GetDirectoryName($wrong)
    $null = RunWorker $false 'Kein eindeutiger Ordner'; NoServiceOrTargetChanges
    Passed 'unrecognized installation path fails before stopping or deleting'
    ResetCase 'changed-services'; Service 'a' 'one'; Package; $script:changedServiceSet=$true
    $null=RunWorker $false 'Server-Suite-Dienste'; NoServiceOrTargetChanges
    Passed 'service set changes abort before stopping or deleting'
    ResetCase 'linked-target'; Service 'a' 'one'; Package
    $script:reparsePath=Join-Path $script:case 'one\apps\nt3-server-suite'
    $null=RunWorker $false 'Junctions'; NoServiceOrTargetChanges
    Passed 'linked installation target is rejected before stopping or deleting'
    ResetCase 'overlap'; Service 'a' 'one'; Package
    $script:request.RemoteInstallPath=Join-Path $script:case 'one\apps\nt3-server-suite\download'
    $null=RunWorker $false 'Staging und Installationsziel'; NoServiceOrTargetChanges
    Passed 'staging inside installation target is rejected before stopping or deleting'

    foreach ($fault in @('stop','copy','start')) {
        ResetCase ('failure-' + $fault); Service 'a' 'one'; Service 'b' 'two'; Package
        if ($fault -eq 'stop') { $script:stopFailure='nt3_server_suite_b' }
        if ($fault -eq 'copy') { $script:copyFailure=Join-Path $script:case 'one\apps\nt3-server-suite' }
        if ($fault -eq 'start') { $script:startFailure='nt3_server_suite_a' }
        $null = RunWorker $false ('Simulated ' + $fault + ' failure')
        Assert ('start:nt3_server_suite_a' -in $script:events -and 'start:nt3_server_suite_b' -in $script:events) 'Every service must receive a restart attempt even after failure'
        if ($fault -eq 'stop') { Assert (@($script:events | Where-Object { $_ -like 'copy:*' -or $_ -match '^delete:.*\\nt3-server-suite$' }).Count -eq 0) 'Failed stop must prevent all installation changes' }
        Passed ('failure is reported and all restarts are attempted: ' + $fault)
    }
    ResetCase 'two-failures'; Service 'a' 'one'; Package
    $script:copyFailure=Join-Path $script:case 'one\apps\nt3-server-suite'; $script:startFailure='nt3_server_suite_a'
    $null=RunWorker $false 'Simulated copy failure.*Simulated start failure'
    Passed 'copy error is retained alongside a restart error'
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
}
finally {
    # The resolved absolute GUID path was checked at creation; remove only it.
    Assert ($script:fixture.StartsWith($workspace + '\obj\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($script:fixture) -match '^suite-update-test-[a-f0-9]{32}$') 'Invalid final fixture cleanup'
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $script:fixture -Recurse -Force -ErrorAction Stop
}
