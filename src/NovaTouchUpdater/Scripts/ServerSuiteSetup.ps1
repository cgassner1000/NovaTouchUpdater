# Protocol v1 is deliberately opt-in: old Inno setups ignore custom instance
# arguments and may update a different installation. Never execute those EXEs.
function AssertServerSuiteSetup {
    param([string]$Path, [string]$Hash, [string]$Version)
    if ($Hash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Server-Suite-SHA256 fehlt oder ist ungültig.' }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Server-Suite-Setup fehlt: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash -ne $Hash) { throw "Server-Suite-SHA256 stimmt nicht: $Path" }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if (([string]$info.FileDescription).Trim() -cne 'NovaTouch Server Suite Updater v1' -or ([string]$info.ProductName).Trim() -cne 'NovaTouch Server Suite') {
        throw 'Server-Suite-Setup unterstützt keine gezielten Instanzupdates. Setup mit dem aktualisierten Inno-Skript neu erstellen.'
    }
    if ([version](([string]$info.ProductVersion).Trim()) -ne [version]$Version) { throw "Server-Suite-Setup hat nicht die Zielversion ${Version}: $($info.ProductVersion)" }
}

# Self-contained except for AssertServerSuiteSetup, serialized into the existing
# authenticated session. Only the setup performs deployment/cleanup.
function InstallServerSuiteSetup {
    param([string]$Setup, [string]$Hash, [string]$Version, [string]$PendingJson, [string]$AllJson)
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    function Status([string]$phase, [string]$message) { [pscustomobject]@{ Event='progress'; Phase=$phase; Message=$message; ProgressPercent=$null } }
    function SafePath([string]$path) {
        if ($path -notmatch '^[A-Za-z]:\\' -or $path -match '["\r\n]') { throw "Ungültiger Instanzpfad: $path" }
        $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
        if ($full.Length -le 3) { throw 'Laufwerkswurzel ist kein Installationsziel.' }
        $cursor = $full
        while ($cursor) {
            if (Test-Path -LiteralPath $cursor) {
                if (((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse Point im Instanzpfad: $cursor" }
            }
            $cursor = [IO.Path]::GetDirectoryName($cursor)
        }
        return $full
    }
    AssertServerSuiteSetup $Setup $Hash $Version
    $pending = ConvertFrom-Json -InputObject $PendingJson
    $pending = @($pending)
    $all = ConvertFrom-Json -InputObject $AllJson
    $all = @($all | Where-Object Kind -eq 'serversuite')
    $current = @(Get-Service -ErrorAction Stop | Where-Object Name -Match '^nt3_server_suite' | ForEach-Object Name | Sort-Object -Unique)
    $expected = @($all | ForEach-Object Name | Sort-Object -Unique)
    if ($expected.Count -ne $current.Count -or @($expected | Where-Object { $_ -notin $current }).Count -gt 0) { throw 'Server-Suite-Dienste haben sich geändert. Bitte erneut prüfen.' }
    $plans = @()
    foreach ($svc in $pending) {
        if ($svc.Kind -ne 'serversuite' -or $svc.Name -notmatch '^nt3_server_suite(?:_.+)?$' -or
            $svc.Name -match '["\r\n\x00\\/]' -or $svc.Name -notin $expected) { throw 'Ungültige Server-Suite-Instanz.' }
        $exe = SafePath ([string]$svc.ExePath)
        $image = [Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc.Name) -Name ImagePath -ErrorAction Stop).ImagePath)
        if ($image -match '^\s*"([^"]+?\.exe)"') { $registered = $Matches[1] }
        elseif ($image -match '^\s*(.+?\.exe)(?:\s|$)') { $registered = $Matches[1] }
        else { throw "Dienstpfad nicht lesbar: $($svc.Name)" }
        if (-not $exe.Equals((SafePath $registered), [StringComparison]::OrdinalIgnoreCase)) { throw "Dienstpfad hat sich geändert: $($svc.Name)" }
        $python = [IO.Path]::GetDirectoryName($exe)
        while ($python -and [IO.Path]::GetFileName($python) -ine 'portable_python') { $python = [IO.Path]::GetDirectoryName($python) }
        if (-not $python) { throw "Kein portable_python im Dienstpfad: $($svc.Name)" }
        $root = SafePath ([IO.Path]::GetDirectoryName($python))
        $app = SafePath (Join-Path $root 'apps\nt3-server-suite')
        if (-not $app.Equals((SafePath ([string]$svc.InstallDir)), [StringComparison]::OrdinalIgnoreCase)) { throw "Instanzziel widerspricht Dienstpfad: $($svc.Name)" }
        foreach ($target in @($python, $app)) {
            if ($Setup.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Setup liegt innerhalb eines zu ersetzenden Installationsordners.' }
        }
        $plans += [pscustomobject]@{ Name=[string]$svc.Name; Root=$root }
    }
    if ($plans.Count -eq 0) { throw 'Keine Server-Suite-Instanz für Setup ausgewählt.' }
    $logRoot = Join-Path ([IO.Path]::GetTempPath()) 'NovaTouchUpdater\SetupLogs'
    [void][IO.Directory]::CreateDirectory($logRoot)
    $errors = [Collections.Generic.List[string]]::new()
    $installed = [Collections.Generic.List[string]]::new()
    $failedStarts = [Collections.Generic.List[string]]::new()
    $restore = [Collections.Generic.List[string]]::new()
    $eventLogAttempted = $false; $eventLogReady = $true; $reboot = $false
    try {
        foreach ($svc in $all) {
            if ($svc.StartType -ne 'Disabled') { $restore.Add([string]$svc.Name) }
            $service = Get-Service -Name ([WildcardPattern]::Escape([string]$svc.Name)) -ErrorAction Stop
            Status 'Dienste stoppen' ("Stoppe $($svc.Name)")
            if ($service.Status -ne 'Stopped') { Stop-Service -Name ([WildcardPattern]::Escape([string]$svc.Name)) -Force -ErrorAction Stop }
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        $eventLogAttempted = $true; $eventLogReady = $false
        Status 'Windows EventLog stoppen' 'Stoppe Windows EventLog vor der Setup-Installation'
        $eventLog = Get-Service -Name EventLog -ErrorAction Stop
        if ($eventLog.Status -ne 'Stopped') { Stop-Service -Name EventLog -ErrorAction Stop }
        $eventLog.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        foreach ($plan in $plans) {
            $log = Join-Path $logRoot (($plan.Name -replace '[^a-zA-Z0-9_.-]', '_') + '-' + [guid]::NewGuid().ToString('N') + '.log')
            try {
                Status 'Setup installieren' ("$($plan.Name): $Version nach $($plan.Root)")
                $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /RESTARTEXITCODE=3010 /UPDATER=1 /SERVICENAME="' + $plan.Name + '" /DIR="' + $plan.Root + '" /LOG="' + $log + '"'
                $process = Start-Process -FilePath $Setup -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden -ErrorAction Stop
                if ($process.ExitCode -eq 3010) { $reboot = $true }
                elseif ($process.ExitCode -ne 0) { throw "Setup-Exitcode $($process.ExitCode)" }
                $installed.Add($plan.Name)
            } catch { $errors.Add("$($plan.Name): $($_.Exception.Message); Setup-Log: $log") }
        }
    } catch { $errors.Add($_.Exception.Message) }
    finally {
        if ($eventLogAttempted) {
            try {
                Status 'Windows EventLog starten' 'Starte Windows EventLog vor den Suite-Diensten'
                Start-Service -Name EventLog -ErrorAction Stop
                (Get-Service -Name EventLog -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
                $eventLogReady = $true
            } catch { $errors.Add('Windows EventLog konnte nicht gestartet werden: ' + $_.Exception.Message) }
        }
        if ($eventLogReady) {
            foreach ($name in $restore) {
                try {
                    Status 'Dienste starten' ("Starte $name")
                    Start-Service -Name ([WildcardPattern]::Escape($name)) -ErrorAction Stop
                    (Get-Service -Name ([WildcardPattern]::Escape($name)) -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
                } catch { $failedStarts.Add($name); $errors.Add("${name}: Dienststart fehlgeschlagen: $($_.Exception.Message)") }
            }
        }
    }
    [pscustomobject]@{ Event='setup-result'; InstalledNames=@($installed | Where-Object { $eventLogReady -and $_ -notin $failedStarts });
        RebootRequired=$reboot; Errors=@($errors); Changed=($installed.Count -gt 0) }
}

function InvokeServerSuiteSetupUpdate($request, $session, $pending, $services) {
    $source = [IO.Path]::GetFullPath([string]$request.ServerSuiteSetup)
    $hash = [string]$request.ServerSuiteSha256; $version = [string]$request.ServerSuiteVersion
    Progress 'Setup prüfen' 'Server-Suite-Setup und Instanzprotokoll prüfen'
    AssertServerSuiteSetup $source $hash $version
    $direct = (TestLocalSession $session) -and [bool]$request.SkipLocalPackageCopy
    $skipTransfer = -not (TestLocalSession $session) -and [bool]$request.SkipZipTransfer
    $target = if ($direct) { $source } else {
        $directory = InvokeWorkerCommand -Session $session -ArgumentList ([string]$request.RemoteInstallPath) -ScriptBlock {
            param([string]$path)
            if ($path -notmatch '^[A-Za-z]:\\') { throw 'Absoluter Setup-Zielpfad fehlt.' }
            $full = [IO.Path]::GetFullPath($path)
            [void][IO.Directory]::CreateDirectory($full)
            $full
        }
        Join-Path $directory ([IO.Path]::GetFileName($source))
    }
    $remoteHash = InvokeWorkerCommand -Session $session -ArgumentList $target -ScriptBlock {
        param($path)
        if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash } else { '' }
    }
    if ($remoteHash -ne $hash) {
        if ($direct -or $skipTransfer) { throw "Vorhandenes Server-Suite-Setup fehlt oder SHA256 stimmt nicht: $target" }
        CopyRemoteFileWithProgress $session $source $target 'Server-Suite-Setup'
    }
    $script = 'param($setup,$hash,$version,$pendingJson,$allJson); function AssertServerSuiteSetup {' + ${function:AssertServerSuiteSetup}.ToString() + '}; function InstallServerSuiteSetup {' + ${function:InstallServerSuiteSetup}.ToString() + '}; InstallServerSuiteSetup $setup $hash $version $pendingJson $allJson'
    $argsList = @($target, $hash, $version, (ConvertTo-Json -InputObject @($pending) -Depth 12 -Compress), (ConvertTo-Json -InputObject @($services) -Depth 12 -Compress))
    $outcome = InvokeWorkerCommand -Session $session -ArgumentList $argsList -ScriptBlock ([scriptblock]::Create($script)) -ErrorAction Stop | ForEach-Object {
        if ($_.Event -eq 'progress') { Progress ([string]$_.Phase) ([string]$_.Message) } else { $_ }
    }
    $errors = @($outcome.Errors); $completed = @(); $after = @()
    try {
        Progress 'Verifikation' 'Instanzversionen und Dienststatus nach Setup prüfen'
        $after = @(InvokeWorkerCommand -Session $session -ScriptBlock ${function:ReadInventory} -ErrorAction Stop)
        foreach ($old in $pending) {
            if ($old.Name -notin @($outcome.InstalledNames)) { continue }
            $current = @($after | Where-Object Name -eq $old.Name)
            if ($current.Count -ne 1 -or -not (IsTargetVersion $current[0] $version)) { $errors += "Instanzversion nicht bestätigt: $($old.Name)"; continue }
            if ($old.StartType -ne 'Disabled' -and $current[0].State -ne 'Running') { $errors += "Dienst läuft nicht: $($old.Name)"; continue }
            $completed += [string]$old.Name
            Progress 'Instanz geprüft' ("$($old.Name): $version; $($current[0].State)")
        }
    } catch { $errors += $_.Exception.Message }
    if ($completed.Count -ne @($pending).Count -and $errors.Count -eq 0) { $errors += 'Nicht alle Server-Suite-Instanzen konnten verifiziert werden.' }
    return @{ Event='result'; Success=($errors.Count -eq 0); Changed=[bool]$outcome.Changed; RebootRequired=[bool]$outcome.RebootRequired;
        CompletedServiceNames=@($completed); Services=@($after); Error=($errors -join ' | ');
        Message=$(if ($errors.Count -gt 0) { 'Server-Suite-Setup teilweise fehlgeschlagen' } else { 'Server-Suite-Setup für alle Instanzen verifiziert' }) }
}
