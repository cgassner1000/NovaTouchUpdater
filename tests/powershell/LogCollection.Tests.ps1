. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message }; Write-Host "PASS $message" }
. (Join-Path $TestScriptsRoot 'LogCollection.ps1')
$root = Join-Path $TestArtifactsRoot ('collect-logs-' + [guid]::NewGuid().ToString('N'))
$script:fixtureRoot = $root
$script:dropCompletion = $false
[void][IO.Directory]::CreateDirectory($root)
function FixtureFile([string]$relative, [string]$content) {
    $path = Join-Path $root $relative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path, $content)
    return $path
}
function TestLocalSession($Session) { return [bool]$Session.Local }
function ReadInventory { return $script:services }
function Get-CimInstance { param($ClassName, $ErrorAction) Assert ($ClassName -eq 'Win32_UserProfile') 'only user-profile metadata is queried'; return $script:profiles }
function InvokeWorkerCommand {
    param($Session, $ScriptBlock, [object[]]$ArgumentList = @())
    $script:invocations++
    $readFile = $ScriptBlock.ToString() -eq ${function:ReadRemoteLogFile}.ToString()
    if ($readFile) {
        Assert (-not $Session.Local -and $ArgumentList[0].StartsWith($script:fixtureRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'remote reads use the selected session and only fixture files'
        $script:remoteCopies++
        if ($script:failRemoteCopy -and $ArgumentList[0] -eq $script:firstSource) {
            [pscustomobject]@{ Offset=0; Data=[Text.Encoding]::UTF8.GetBytes('partial') }
            throw 'Simulated locked log'
        }
    }
    elseif ($ArgumentList.Count -ge 2) { $ArgumentList[1] = $script:testSystemProfile }
    elseif ($ArgumentList.Count -eq 1) { $ArgumentList += $script:testSystemProfile }
    # A real remote response is deserialized, rather than a live local object.
    & $ScriptBlock @ArgumentList | ForEach-Object {
        if ($readFile -and $script:dropCompletion -and $_.PSObject.Properties['Complete']) { return }
        if ($Session.Local) { $_ } else { $_ | ConvertTo-Json -Depth 14 | ConvertFrom-Json }
    }
}
function Progress { param($phase, $message, $percent) $script:progress.Add([int]$percent) }
try {
    $script:testSystemProfile = Join-Path $root 'Windows\System32\config\systemprofile'
    $base = 'Windows\System32\config\systemprofile\AppData\Local\'
    $script:firstSource = FixtureFile ($base + 'nt.payment.A\logs\same.log') 'payment A'
    $null = FixtureFile ($base + 'nt.payment.A\logs\archive\same.log') 'archived payment A'
    $null = FixtureFile ($base + 'nt.payment.B\logs\same.log') 'payment B'
    $null = FixtureFile 'Windows\System32\config\systemprofile\lokal\nt.fiscal.C\logs\fiscal.log' 'fiscal C'
    $null = FixtureFile ($base + 'nt.payment.NOT-IN-INVENTORY\logs\wrong.log') 'must not be collected'
    $suiteRoot = Join-Path $root 'suite-C4U1'
    $null = FixtureFile 'suite-C4U1\logs\suite.log' 'suite root log'
    $null = FixtureFile 'suite-C4U1\apps\nt3-server-suite\logs\wrong.log' 'must not be collected'
    $script:profiles = @()
    foreach ($user in @('operator-one', 'operator-two')) {
        $profileRoot = Join-Path $root ('Users\' + $user)
        $script:profiles += [pscustomobject]@{ LocalPath = $profileRoot; Special = $false }
        $null = FixtureFile ('Users\' + $user + '\AppData\Local\VirtualStore\Program Files (x86)\novatouch\bin\KassaPreh.ept') $user
        $null = FixtureFile ('Users\' + $user + '\AppData\Local\VirtualStore\Program Files (x86)\novatouch\bin\private.ini') 'must not be collected'
    }
    [IO.File]::SetLastWriteTimeUtc((Join-Path $root 'Users\operator-one\AppData\Local\VirtualStore\Program Files (x86)\novatouch\bin\KassaPreh.ept'), [datetime]'2026-11-18T10:00:00Z')
    [IO.File]::SetLastWriteTimeUtc((Join-Path $root 'Users\operator-two\AppData\Local\VirtualStore\Program Files (x86)\novatouch\bin\KassaPreh.ept'), [datetime]'2026-11-18T11:00:00Z')
    $script:services = @(
        [pscustomobject]@{ Name='nt.payment.A'; Kind='payment'; RegistryInstallDir=''; InstallDir='' }
        [pscustomobject]@{ Name='nt.payment.B'; Kind='payment'; RegistryInstallDir=''; InstallDir='' }
        [pscustomobject]@{ Name='nt.fiscal.C'; Kind='fiscal'; RegistryInstallDir=''; InstallDir='' }
        [pscustomobject]@{ Name='nt3_server_suite_C4U1'; Kind='serversuite'; RegistryInstallDir=$suiteRoot; InstallDir=(Join-Path $suiteRoot 'apps\nt3-server-suite') }
    )
    $sourceHashes = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse)) { $sourceHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
    foreach ($local in @($true, $false)) {
        $destination = Join-Path $root ('output-' + $local)
        [void][IO.Directory]::CreateDirectory($destination)
        [IO.File]::WriteAllText((Join-Path $destination 'Sammlung.txt'), 'reserved fixture')
        $script:progress = [Collections.Generic.List[int]]::new(); $script:invocations = 0; $script:remoteCopies = 0; $script:failRemoteCopy = $false
        $session = [pscustomobject]@{ Local=$local }
        $request = [pscustomobject]@{ Host='fixture.invalid'; LogCollectionDirectory=$destination }
        $result = CollectHostLogs $request $session
        Assert ($result.Success -and $result.LogFileCount -eq 6 -and $result.LogCollectionWarnings.Count -eq 0) "all service and POS logs collected; local=$local"
        Assert ($script:invocations -eq $(if ($local) { 2 } else { 8 }) -and $script:remoteCopies -eq $(if ($local) { 0 } else { 6 })) 'discovery and transfer use only the selected execution mode'
        Assert ([IO.File]::ReadAllText((Join-Path $destination 'Nt.Payment.nt.payment.A\same.log')) -eq 'payment A' -and
            [IO.File]::ReadAllText((Join-Path $destination 'Nt.Payment.nt.payment.B\same.log')) -eq 'payment B' -and
            [IO.File]::ReadAllText((Join-Path $destination 'Nt.Payment.nt.payment.A\archive\same.log')) -eq 'archived payment A') 'identical names remain unchanged and isolated by service and subdirectory'
        Assert ([IO.File]::ReadAllText((Join-Path $destination 'Server.suite.nt3_server_suite_C4U1\suite.log')) -eq 'suite root log' -and
            @(Get-ChildItem -LiteralPath $destination -Filter wrong.log -Recurse).Count -eq 0) 'suite logs come from the instance root and service names exclude unrelated logs'
        Assert ([IO.File]::ReadAllText((Join-Path $destination 'NovaTouch POS\KassaPreh.ept')) -eq 'operator-two' -and
            @(Get-ChildItem -LiteralPath $destination -Directory | Where-Object Name -like 'NovaTouch POS.*').Count -eq 0 -and
            -not (Test-Path -LiteralPath (Join-Path $destination 'NovaTouch POS\logs'))) 'POS has a single shared folder and keeps the newest original filename when profiles overlap'
        Assert (@(Get-ChildItem -LiteralPath $destination -Filter private.ini -Recurse).Count -eq 0 -and ($script:progress | Measure-Object -Maximum).Maximum -lt 100) 'only EPT files are copied from POS and progress stays below completion until the result'
    }
    foreach ($sourcePath in $sourceHashes.Keys) { Assert ((Get-FileHash -LiteralPath $sourcePath).Hash -eq $sourceHashes[$sourcePath]) 'source logs remain unchanged' }

    $liveDestination = Join-Path $root 'live-copy.log'
    $liveWriter = [IO.FileStream]::new($script:firstSource, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    try {
        CopyLocalLogFile $script:firstSource $liveDestination
        CopyRemoteLogFile ([pscustomobject]@{ Local=$false }) $script:firstSource (Join-Path $root 'live-remote-copy.log')
    }
    finally { $liveWriter.Dispose() }
    Assert ([IO.File]::ReadAllText($liveDestination) -eq 'payment A') 'local copying supports logs that are open for writing'
    Assert ([IO.File]::ReadAllText((Join-Path $root 'live-remote-copy.log')) -eq 'payment A') 'remote streaming also reads logs that are open for writing'

    $binary = [byte[]]::new(300000)
    [Random]::new(123).NextBytes($binary)
    $binarySource = Join-Path $root 'binary.ept'
    $binaryTarget = Join-Path $root 'binary-copy.ept'
    [IO.File]::WriteAllBytes($binarySource, $binary)
    CopyRemoteLogFile ([pscustomobject]@{ Local=$false }) $binarySource $binaryTarget
    Assert ((Get-FileHash -LiteralPath $binarySource).Hash -eq (Get-FileHash -LiteralPath $binaryTarget).Hash) 'multi-block binary logs survive remote serialization byte for byte'
    $empty = FixtureFile 'empty.log' ''
    $emptyCopy = Join-Path $root 'empty-copy.log'
    CopyRemoteLogFile ([pscustomobject]@{ Local=$false }) $empty $emptyCopy
    Assert ((Get-Item -LiteralPath $emptyCopy).Length -eq 0) 'empty log files are preserved'
    $script:dropCompletion = $true
    $rejected = $false
    try { CopyRemoteLogFile ([pscustomobject]@{ Local=$false }) $script:firstSource (Join-Path $root 'incomplete.log') } catch { $rejected = $true }
    finally { $script:dropCompletion = $false }
    Assert $rejected 'an interrupted remote stream cannot be reported as a complete file'

    $lockedDestination = Join-Path $root 'output-locked-local'
    [void][IO.Directory]::CreateDirectory($lockedDestination)
    [IO.File]::WriteAllText((Join-Path $lockedDestination 'Sammlung.txt'), 'reserved fixture')
    $locked = [IO.FileStream]::new($script:firstSource, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $lockedResult = CollectHostLogs ([pscustomobject]@{ Host='fixture.invalid'; LogCollectionDirectory=$lockedDestination }) ([pscustomobject]@{ Local=$true }) }
    finally { $locked.Dispose() }
    Assert (-not $lockedResult.Success -and $lockedResult.LogFileCount -eq 5 -and $lockedResult.LogCollectionWarnings.Count -gt 0) 'a genuinely locked local file is reported while other files are collected'

    $null = FixtureFile 'unrelated\secret.txt' 'must not follow a junction'
    $junction = Join-Path (Split-Path -Parent $script:firstSource) 'linked'
    try {
        $null = New-Item -ItemType Junction -Path $junction -Target (Join-Path $root 'unrelated') -ErrorAction Stop
        $linkedPlan = GetLogCollectionPlan (ConvertTo-Json -InputObject $script:services -Depth 10) $script:testSystemProfile
        Assert (@($linkedPlan.Files | Where-Object Source -like '*secret.txt').Count -eq 0 -and $linkedPlan.Warnings.Count -eq 1) 'source junctions are skipped and reported'
        $targetLink = Join-Path $lockedDestination 'linked'
        try {
            $null = New-Item -ItemType Junction -Path $targetLink -Target (Join-Path $root 'unrelated') -ErrorAction Stop
            $rejected = $false
            try { $null = GetLogCollectionDestination $lockedDestination 'linked\escape.log' } catch { $rejected = $true }
            Assert ($rejected -and -not (Test-Path -LiteralPath (Join-Path $root 'unrelated\escape.log'))) 'destination junctions cannot redirect copied files'
        }
        finally { if (Test-Path -LiteralPath $targetLink) { [IO.Directory]::Delete($targetLink) } }
    }
    finally { if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) } }

    $legacy = @($script:services | Where-Object Kind -eq 'serversuite')[0]
    $legacy.RegistryInstallDir = ''
    $plan = GetLogCollectionPlan (ConvertTo-Json -InputObject $script:services -Depth 10) $script:testSystemProfile
    Assert (@($plan.Files | Where-Object RelativePath -eq 'Server.suite.nt3_server_suite_C4U1\suite.log').Count -eq 1) 'legacy suite app directories resolve back to the installation root'
    $legacy.RegistryInstallDir = $suiteRoot
    $script:services += [pscustomobject]@{ Name='nt.payment.MISSING'; Kind='payment'; RegistryInstallDir=''; InstallDir='' }
    $request.LogCollectionDirectory = Join-Path $root 'output-partial'
    [void][IO.Directory]::CreateDirectory($request.LogCollectionDirectory)
    [IO.File]::WriteAllText((Join-Path $request.LogCollectionDirectory 'Sammlung.txt'), 'reserved fixture')
    $script:failRemoteCopy = $true
    $result = CollectHostLogs $request ([pscustomobject]@{ Local=$false })
    Assert (-not $result.Success -and $result.LogFileCount -eq 5 -and $result.LogCollectionWarnings.Count -ge 2 -and
        -not (Test-Path -LiteralPath (Join-Path $request.LogCollectionDirectory 'Nt.Payment.nt.payment.A\same.log'))) 'missing sources and locked remote files are reported; partial files are removed and other logs survive'

    foreach ($bad in @('..\escape.log', 'safe\..\escape.log', 'C:\escape.log', '\\server\share', 'safe\file:stream', '', 'safe\\file')) {
        $rejected = $false
        try { $null = GetLogCollectionDestination $request.LogCollectionDirectory $bad } catch { $rejected = $true }
        Assert $rejected 'remote responses cannot escape the collection directory or write alternate streams'
    }
    $existing = Join-Path $request.LogCollectionDirectory 'existing.log'
    [IO.File]::WriteAllText($existing, 'preserve')
    $rejected = $false
    try { $null = GetLogCollectionDestination $request.LogCollectionDirectory 'existing.log' } catch { $rejected = $true }
    Assert ($rejected -and [IO.File]::ReadAllText($existing) -eq 'preserve') 'existing collected files are never overwritten'

    $overlap = Join-Path $suiteRoot 'logs\NovaTouch LOG 2026-11-18 15-00'
    [void][IO.Directory]::CreateDirectory($overlap)
    [IO.File]::WriteAllText((Join-Path $overlap 'Sammlung.txt'), 'reserved fixture')
    $overlapResult = CollectHostLogs ([pscustomobject]@{ Host='fixture.invalid'; LogCollectionDirectory=$overlap }) ([pscustomobject]@{ Local=$true })
    Assert ($overlapResult.Success -and $overlapResult.LogFileCount -eq 6 -and
        @(Get-ChildItem -LiteralPath $overlap -Filter Sammlung.txt -Recurse).Count -eq 1) 'a collector beside the suite does not collect its own output recursively'
    $null = FixtureFile 'suite-C4U1\logs\NovaTouch LOG 2026-11-18 14-59.zip' 'owned archive, must not collect itself'
    $null = FixtureFile 'suite-C4U1\logs\NovaTouch LOG 2026-11-18 14-58.zip.partial' 'owned partial archive'
    $overlapAgain = Join-Path $suiteRoot 'logs\NovaTouch LOG 2026-11-18 15-00 (2)'
    [void][IO.Directory]::CreateDirectory($overlapAgain)
    [IO.File]::WriteAllText((Join-Path $overlapAgain 'Sammlung.txt'), 'reserved fixture')
    $again = CollectHostLogs ([pscustomobject]@{ Host='fixture.invalid'; LogCollectionDirectory=$overlapAgain }) ([pscustomobject]@{ Local=$true })
    Assert ($again.Success -and $again.LogFileCount -eq 6) 'previous updater folders and ZIP archives are excluded when source and output logs folders coincide'

    # Regression: the actual service path is AppData\Roaming\Novacom, including its archives.
    $roamingBase = 'Windows\System32\config\systemprofile\AppData\Roaming\Novacom\'
    $null = FixtureFile ($roamingBase + 'Nt.Payment.108\logs\Communication.log') 'current payment 108'
    $null = FixtureFile ($roamingBase + 'Nt.Payment.108\logs\archive\Server.log') 'archived payment 108'
    $null = FixtureFile ($roamingBase + 'Nt.Fiscal.108\logs\Server.log') 'current fiscal 108'
    $null = FixtureFile ($roamingBase + 'Nt.Payment.109\logs\wrong.log') 'unrelated service'
    $null = FixtureFile ($base + 'Nt.Payment.108\logs\Communication.log') 'obsolete local copy'
    $script:services = @(
        [pscustomobject]@{ Name='Nt.Payment.108'; Kind='payment' }
        [pscustomobject]@{ Name='Nt.Fiscal.108'; Kind='fiscal' }
    )
    $script:failRemoteCopy = $false
    foreach ($local in @($true, $false)) {
        $output = Join-Path $root ('output-roaming-' + $local)
        [void][IO.Directory]::CreateDirectory($output)
        [IO.File]::WriteAllText((Join-Path $output 'Sammlung.txt'), 'reserved fixture')
        $roamingResult = CollectHostLogs ([pscustomobject]@{ Host='fixture.invalid'; LogCollectionDirectory=$output }) ([pscustomobject]@{ Local=$local })
        Assert ($roamingResult.Success -and $roamingResult.LogFileCount -eq 4 -and $roamingResult.LogCollectionWarnings.Count -eq 0 -and
            [IO.File]::ReadAllText((Join-Path $output 'Nt.Payment.Nt.Payment.108\Communication.log')) -eq 'current payment 108' -and
            [IO.File]::ReadAllText((Join-Path $output 'Nt.Payment.Nt.Payment.108\archive\Server.log')) -eq 'archived payment 108' -and
            [IO.File]::ReadAllText((Join-Path $output 'Nt.Fiskal.Nt.Fiscal.108\Server.log')) -eq 'current fiscal 108' -and
            @(Get-ChildItem -LiteralPath $output -Filter wrong.log -Recurse).Count -eq 0) "Roaming Novacom logs take precedence, retain service/archive folders and exclude other services; local=$local"
    }

    $posRoaming = 'AppData\Roaming\novacom software gmbh\novatouch\logs\'
    $null = FixtureFile ('Users\operator-one\' + $posRoaming + 'Communication.log') 'operator-one roaming'
    $null = FixtureFile ('Users\operator-one\' + $posRoaming + 'archive\Server.log') 'operator-one archive'
    $null = FixtureFile ('Users\operator-one\' + $posRoaming + 'KassaPreh.ept') 'roaming EPT with the same name'
    $null = FixtureFile ('Users\operator-two\' + $posRoaming + 'Communication.log') 'operator-two roaming'
    $null = FixtureFile ('Users\roaming-only\' + $posRoaming + 'only.log') 'profile without VirtualStore'
    [IO.File]::SetLastWriteTimeUtc((Join-Path $root ('Users\operator-one\' + $posRoaming + 'KassaPreh.ept')), [datetime]'2026-11-18T12:00:00Z')
    [IO.File]::SetLastWriteTimeUtc((Join-Path $root ('Users\operator-one\' + $posRoaming + 'Communication.log')), [datetime]'2026-11-18T12:00:00Z')
    [IO.File]::SetLastWriteTimeUtc((Join-Path $root ('Users\operator-two\' + $posRoaming + 'Communication.log')), [datetime]'2026-11-18T13:00:00Z')
    $null = FixtureFile 'Users\operator-one\AppData\Roaming\novacom software gmbh\novatouch\settings.json' 'outside the requested logs directory'
    $null = FixtureFile 'Users\operator-one\AppData\Roaming\novacom software gmbh\other-app\logs\wrong.log' 'unrelated application'
    $script:profiles += [pscustomobject]@{ LocalPath=(Join-Path $root 'Users\roaming-only'); Special=$false }
    foreach ($local in @($true, $false)) {
        $output = Join-Path $root ('output-pos-roaming-' + $local)
        [void][IO.Directory]::CreateDirectory($output)
        [IO.File]::WriteAllText((Join-Path $output 'Sammlung.txt'), 'reserved fixture')
        $posResult = CollectHostLogs ([pscustomobject]@{ Host='fixture.invalid'; LogCollectionDirectory=$output }) ([pscustomobject]@{ Local=$local })
        Assert ($posResult.Success -and $posResult.LogFileCount -eq 7 -and $posResult.LogCollectionWarnings.Count -eq 0 -and
            [IO.File]::ReadAllText((Join-Path $output 'NovaTouch POS\KassaPreh.ept')) -eq 'roaming EPT with the same name' -and
            [IO.File]::ReadAllText((Join-Path $output 'NovaTouch POS\Communication.log')) -eq 'operator-two roaming' -and
            [IO.File]::ReadAllText((Join-Path $output 'NovaTouch POS\archive\Server.log')) -eq 'operator-one archive' -and
            [IO.File]::ReadAllText((Join-Path $output 'NovaTouch POS\only.log')) -eq 'profile without VirtualStore' -and
            -not (Test-Path -LiteralPath (Join-Path $output 'NovaTouch POS\logs'))) "POS combines EPT and Roaming logs directly, preserving archives and choosing the newest duplicate; local=$local"
        Assert (@(Get-ChildItem -LiteralPath $output -Filter settings.json -Recurse).Count -eq 0 -and
            @(Get-ChildItem -LiteralPath $output -Filter wrong.log -Recurse).Count -eq 0 -and
            [IO.File]::ReadAllText((Join-Path $output 'Quellen.txt')).Contains('Mehrfach vorhandene POS-Datei')) 'the POS collection records duplicate choices and excludes settings and unrelated directories'
    }
    $fiscalGuid = 'Nt.Fiscal.9ac37357-31ae-435a-bba1-fef156f222de'
    $null = FixtureFile ($roamingBase + $fiscalGuid + '\logs\Service.log') 'GUID fiscal service'
    $script:services = @([pscustomobject]@{ Name=$fiscalGuid; Kind='fiscal' })
    $guidPlan = GetLogCollectionPlan (ConvertTo-Json -InputObject $script:services -Depth 10) $script:testSystemProfile
    Assert (@($guidPlan.Files | Where-Object RelativePath -eq ('Nt.Fiskal.' + $fiscalGuid + '\Service.log')).Count -eq 1) 'fiscal instances with GUID service names resolve the exact Roaming Novacom logs directory'
}
finally {
    $absoluteRoot = [IO.Path]::GetFullPath($root)
    if (-not $absoluteRoot.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup outside test artifacts' }
    Remove-Item -LiteralPath $absoluteRoot -Recurse -Force
}
