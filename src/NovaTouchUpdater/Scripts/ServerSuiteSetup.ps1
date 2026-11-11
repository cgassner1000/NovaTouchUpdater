# Validate package identity, supported instance-selection protocol and version.
function AssertServerSuiteSetup {
    param([string]$Path, [string]$Hash, [string]$Version)
    if ($Hash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Server-Suite-SHA256 fehlt oder ist ungültig.' }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Server-Suite-Setup fehlt: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash -ne $Hash) { throw "Server-Suite-SHA256 stimmt nicht: $Path" }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if (([string]$info.ProductName).Trim() -ine 'NovaTouch Server Suite') {
        throw 'Die ausgewählte EXE ist kein NovaTouch-Server-Suite-Setup.'
    }
    if (([string]$info.FileDescription).Trim() -notmatch '^NovaTouch Server Suite (?:Updater|Silent Update) v[1-9][0-9]*$') {
        throw 'Dieses Server-Suite-Setup hat keine unterstützte Kennung ab v1. Ein zuverlässiges stilles Update mehrerer Instanzen ist damit nicht möglich. Bitte für diese Version das passende Server-Suite-ZIP-Paket verwenden.'
    }
    if ([version](([string]$info.ProductVersion).Trim()) -ne [version]$Version) { throw "Server-Suite-Setup hat nicht die Zielversion ${Version}: $($info.ProductVersion)" }
}

# Self-contained except for AssertServerSuiteSetup and ReadInventory, serialized into the existing
# authenticated session. The setup owns the entire instance update and service lifecycle.
function InstallServerSuiteSetup {
    param([string]$Setup, [string]$Hash, [string]$Version, [string]$PendingJson, [string]$AllJson, [int]$Parallelism = 1)
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
    $currentInstances = @(ReadInventory | Where-Object Kind -eq 'serversuite')
    $current = @($currentInstances | ForEach-Object Name | Sort-Object -Unique)
    $expected = @($all | ForEach-Object Name | Sort-Object -Unique)
    if ($expected.Count -ne $current.Count -or @($expected | Where-Object { $_ -notin $current }).Count -gt 0) { throw 'Server-Suite-Instanzen in Registry/Diensten haben sich geändert. Bitte erneut prüfen.' }
    $plans = @()
    foreach ($svc in $pending) {
        if ($svc.Kind -ne 'serversuite' -or $svc.Name -notmatch '^nt3_server_suite(?:_.+)?$' -or
            $svc.Name -match '["\r\n\x00\\/]' -or $svc.Name -notin $expected) { throw 'Ungültige Server-Suite-Instanz.' }
        $currentInstance=@($currentInstances | Where-Object Name -eq $svc.Name)
        if ($currentInstance.Count -ne 1) { throw "Server-Suite-Instanz nicht eindeutig: $($svc.Name)" }
        $actual=$currentInstance[0]
        if ($null -ne $actual.PSObject.Properties['RegistryInstallDir'] -and $actual.RegistryInstallDir) {
            $root=SafePath ([string]$actual.RegistryInstallDir)
        } else {
            # Legacy services may not yet have an installer registration.
            $exe=SafePath ([string]$actual.ExePath)
            if (-not $exe.Equals((SafePath ([string]$svc.ExePath)),[StringComparison]::OrdinalIgnoreCase)) { throw "Dienstpfad hat sich geändert: $($svc.Name)" }
            $python=[IO.Path]::GetDirectoryName($exe)
            while ($python -and [IO.Path]::GetFileName($python) -ine 'portable_python') { $python=[IO.Path]::GetDirectoryName($python) }
            if (-not $python) { throw "Kein portable_python im Dienstpfad: $($svc.Name)" }
            $root=SafePath ([IO.Path]::GetDirectoryName($python))
        }
        $python=SafePath (Join-Path $root 'portable_python')
        $app = SafePath (Join-Path $root 'apps\nt3-server-suite')
        if (-not $app.Equals((SafePath ([string]$svc.InstallDir)), [StringComparison]::OrdinalIgnoreCase)) { throw "Instanzziel widerspricht Registry-/Dienstpfad: $($svc.Name)" }
        foreach ($target in @($python, $app)) {
            if ($Setup.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Setup liegt innerhalb eines zu ersetzenden Installationsordners.' }
        }
        $plans += [pscustomobject]@{ Name=[string]$svc.Name; Root=$root; Attempt=0; RequireRunning=($svc.StartType -ne 'Disabled') }
    }
    if ($plans.Count -eq 0) { throw 'Keine Server-Suite-Instanz für Setup ausgewählt.' }
    $logRoot = Join-Path ([IO.Path]::GetTempPath()) 'NovaTouchUpdater\SetupLogs'
    [void][IO.Directory]::CreateDirectory($logRoot)
    $errors = [Collections.Generic.List[string]]::new()
    $installed = [Collections.Generic.List[string]]::new()
    $reboot = $false
    $changed = $false
    $limit = [Math]::Min($plans.Count, [Math]::Max(1, [Math]::Min(100, $Parallelism)))
    $queue = [Collections.Generic.List[object]]::new()
    foreach ($plan in $plans) { $queue.Add($plan) }
    $active = [Collections.Generic.List[object]]::new()
    function FailedSetup($plan, [string]$detail) {
        if ($plan.Attempt -lt 3) {
            Status 'Setup fehlgeschlagen' $detail
            Status 'Setup wiederholen' ("$($plan.Name) · Installation wird erneut versucht (Versuch $($plan.Attempt+1) von 3)")
            $queue.Add($plan)
        } else { $errors.Add($detail) }
    }
    Status 'Setup planen' ("Server Suite · $($plans.Count) Instanz(en) vorbereiten")
    try {
        while ($queue.Count -gt 0 -or $active.Count -gt 0) {
            for ($index = 0; $index -lt $queue.Count -and $active.Count -lt $limit;) {
                $plan = $queue[$index]
                # An installer deletes and refills its deployment directories.
                # Equal or nested roots must never be modified concurrently.
                $conflicts = @($active | Where-Object {
                    $plan.Root.Equals($_.Plan.Root, [StringComparison]::OrdinalIgnoreCase) -or
                    $plan.Root.StartsWith($_.Plan.Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
                    $_.Plan.Root.StartsWith($plan.Root + '\', [StringComparison]::OrdinalIgnoreCase)
                })
                if ($conflicts.Count -gt 0) { $index++; continue }
                $queue.RemoveAt($index)
                $plan.Attempt++
                $log = Join-Path $logRoot (($plan.Name -replace '[^a-zA-Z0-9_.-]', '_') + '-' + [guid]::NewGuid().ToString('N') + '.log')
                try {
                    Status 'Setup installieren' ("$($plan.Name) · Version $Version installieren")
                    # Supported setups select the exact service and run their normal update.
                    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL /RESTARTEXITCODE=3010 /SERVICENAME="' + $plan.Name + '" /DIR="' + $plan.Root + '" /LOG="' + $log + '"'
                    $process = Start-Process -FilePath $Setup -ArgumentList $arguments -PassThru -WindowStyle Hidden -ErrorAction Stop
                    $active.Add([pscustomobject]@{ Plan=$plan; Log=$log; Process=$process })
                    # Retain the native handle so ExitCode remains available even
                    # if a fast setup exits before the next polling pass.
                    $null = $process.Handle
                } catch { FailedSetup $plan "$($plan.Name): $($_.Exception.Message); Setup-Log: $log" }
            }
            foreach ($job in $active.ToArray()) {
                if (-not $job.Process.HasExited) { continue }
                $plan = $job.Plan
                try {
                    $job.Process.WaitForExit()
                    $code = $job.Process.ExitCode
                    if ($code -eq 3010) { $reboot = $true }
                    elseif ($code -ne 0) { throw "Setup-Exitcode $code" }
                    $changed = $true
                    foreach ($relative in @('portable_python\python.exe','apps\nt3-server-suite\src\apps\pos_service\script\pos_service_winservice.py')) {
                        $required = Join-Path $plan.Root $relative
                        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Startdatei fehlt für $($plan.Name): $required" }
                    }
                    # Observe the installer-started service without service control.
                    $service = Get-Service -Name ([WildcardPattern]::Escape($plan.Name)) -ErrorAction Stop
                    if ($plan.RequireRunning -or $service.StartType -ne 'Disabled') {
                        Status 'Setup prüfen' ("$($plan.Name): Warte auf den vom Setup gestarteten Dienst")
                        try { $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(60)) }
                        catch { throw "Dienst läuft nicht: $($plan.Name); $($_.Exception.Message)" }
                    }
                    $installed.Add($plan.Name)
                    Status 'Setup abgeschlossen' ("$($plan.Name) · $($installed.Count) von $($plans.Count) Instanzen installiert")
                } catch { FailedSetup $plan "$($plan.Name): $($_.Exception.Message); Setup-Log: $($job.Log)" }
                finally { $job.Process.Dispose(); [void]$active.Remove($job) }
            }
            if ($active.Count -gt 0) { Start-Sleep -Milliseconds 100 }
        }
    } finally {
        # Let already started installers finish before temporary packages are removed.
        foreach ($job in $active) {
            try { $job.Process.WaitForExit() } finally { $job.Process.Dispose() }
        }
    }
    [pscustomobject]@{ Event='setup-result'; InstalledNames=@($installed);
        RebootRequired=$reboot; Errors=@($errors); Changed=$changed; AutomaticRetriesExhausted=($errors.Count -gt 0) }
}

function InvokeServerSuiteSetupUpdate($request, $session, $pending, $services) {
    $source = [IO.Path]::GetFullPath([string]$request.ServerSuiteSetup)
    $hash = [string]$request.ServerSuiteSha256; $version = [string]$request.ServerSuiteVersion
    Progress 'Setup prüfen' 'Server-Suite-Paket und Instanzziel prüfen'
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
    $parallelism = if ($null -ne $request.PSObject.Properties['Parallelism']) { [int]$request.Parallelism } else { 1 }
    $script = 'param($setup,$hash,$version,$pendingJson,$allJson,$parallelism); function ReadInventory {' + ${function:ReadInventory}.ToString() + '}; function AssertServerSuiteSetup {' + ${function:AssertServerSuiteSetup}.ToString() + '}; function InstallServerSuiteSetup {' + ${function:InstallServerSuiteSetup}.ToString() + '}; InstallServerSuiteSetup $setup $hash $version $pendingJson $allJson $parallelism'
    $argsList = @($target, $hash, $version, (ConvertTo-Json -InputObject @($pending) -Depth 12 -Compress), (ConvertTo-Json -InputObject @($services) -Depth 12 -Compress), $parallelism)
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
            if (($old.StartType -ne 'Disabled' -or $current[0].StartType -ne 'Disabled') -and $current[0].State -ne 'Running') { $errors += "Dienst läuft nicht: $($old.Name)"; continue }
            $completed += [string]$old.Name
            Progress 'Instanz geprüft' ("$($old.Name): $version; $($current[0].State)")
        }
    } catch { $errors += $_.Exception.Message }
    if ($completed.Count -ne @($pending).Count -and $errors.Count -eq 0) { $errors += 'Nicht alle Server-Suite-Instanzen konnten verifiziert werden.' }
    return @{ Event='result'; Success=($errors.Count -eq 0); Changed=[bool]$outcome.Changed; RebootRequired=[bool]$outcome.RebootRequired;
        CompletedServiceNames=@($completed); Services=@($after); Error=($errors -join ' | '); AutomaticRetriesExhausted=[bool]$outcome.AutomaticRetriesExhausted;
        Message=$(if ($errors.Count -gt 0) { 'Server-Suite-Setup teilweise fehlgeschlagen' } else { 'Server-Suite-Setup für alle Instanzen verifiziert' }) }
}
