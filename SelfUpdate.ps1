param([string]$ManifestPath)

function Invoke-AppSelfUpdate {
    param(
        [Parameter(Mandatory=$true)][string]$ManifestPath,
        [scriptblock]$WaitForApplication = {
            param($settings)
            $app = Get-Process -Id $settings.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $app -and $app.StartTime.ToUniversalTime().Ticks -eq $settings.ProcessStartTimeTicks) {
                if (-not $app.WaitForExit(120000)) { throw 'Der laufende Updater wurde nicht beendet. Die alte EXE bleibt erhalten.' }
            }
        },
        [scriptblock]$StartApplication = {
            param($path)
            Start-Process -FilePath $path -WorkingDirectory ([IO.Path]::GetDirectoryName($path)) -WindowStyle Normal -ErrorAction Stop | Out-Null
        }
    )
    $ErrorActionPreference = 'Stop'
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ManifestPath))
    $settings = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $original = [IO.Path]::GetFullPath($settings.OriginalExecutablePath)
    $target = [IO.Path]::GetFullPath($settings.ExecutablePath)
    $staged = [IO.Path]::GetFullPath($settings.StagedPath)
    $backup = Join-Path $directory 'previous.exe'
    $targetBackup = Join-Path $directory 'previous-target.exe'
    $ready = Join-Path $directory 'ready'
    $samePath = [string]::Equals($original, $target, [StringComparison]::OrdinalIgnoreCase)
    $originalMoved = $false
    $installed = $false
    # Validate every final absolute path before moving or deleting files.
    if ([IO.Path]::GetDirectoryName($directory) -ne [IO.Path]::GetDirectoryName($target) -or
        [IO.Path]::GetDirectoryName($original) -ne [IO.Path]::GetDirectoryName($target) -or
        [IO.Path]::GetFileName($directory) -notmatch '^\.NovaTouchUpdater-update-[a-f0-9]{32}$' -or
        $staged -ne (Join-Path $directory 'new.exe') -or
        [IO.Path]::GetFileName($target) -ne 'NovaTouchUpdater.exe' -or [IO.Directory]::Exists($target) -or
        [IO.Path]::GetExtension($original) -ne '.exe' -or -not [IO.File]::Exists($original) -or
        [IO.File]::Exists($backup) -or [IO.File]::Exists($targetBackup)) {
        throw 'Die Pfade für den EXE-Austausch sind ungültig.'
    }
    try {
        if ((Get-Item -LiteralPath $staged).Length -ne $settings.Size -or
            (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $settings.Sha256) {
            throw 'Die vorbereitete EXE stimmt nicht mit dem geprüften Download überein.'
        }
        [IO.File]::WriteAllText($ready, 'ready')
        & $WaitForApplication $settings
        # Retain both the running EXE and any existing fixed-name EXE until
        # starting succeeds. Subsequent updates replace the fixed path atomically.
        $lastError = $null
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try {
                if ($samePath) {
                    [IO.File]::Replace($staged, $target, $backup, $true)
                } else {
                    if (-not $originalMoved) {
                        [IO.File]::Move($original, $backup)
                        $originalMoved = $true
                    }
                    if ([IO.File]::Exists($target)) {
                        [IO.File]::Replace($staged, $target, $targetBackup, $true)
                    } else {
                        [IO.File]::Move($staged, $target)
                    }
                }
                $installed = $true
                break
            } catch [IO.IOException] {
                $lastError = $_
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $installed) { throw $lastError }
        & $StartApplication $target
    } catch {
        $failure = $_
        if ($installed -or $originalMoved) {
            try {
                # PowerShell 5.1 converts $null to an empty string for this
                # overload. Use an explicit backup path for the rejected EXE.
                if ($samePath) {
                    [IO.File]::Replace($backup, $target, (Join-Path $directory 'failed.exe'), $true)
                } else {
                    if ($installed) {
                        if ([IO.File]::Exists($targetBackup)) {
                            [IO.File]::Replace($targetBackup, $target, (Join-Path $directory 'failed.exe'), $true)
                        } else {
                            [IO.File]::Move($target, (Join-Path $directory 'failed.exe'))
                        }
                    }
                    if ($originalMoved) { [IO.File]::Move($backup, $original) }
                }
                & $StartApplication $original
            } catch {
                throw "Update fehlgeschlagen: $failure | Wiederherstellung/Neustart: $_ | Sicherungsordner: $directory"
            }
        }
        throw $failure
    }
    # Delete only our own files; never recurse through the application directory.
    foreach ($file in @($backup, $targetBackup, $ready, (Join-Path $directory 'SelfUpdate.ps1'), $ManifestPath)) {
        try { [IO.File]::Delete($file) } catch { }
    }
    try { [IO.Directory]::Delete($directory) } catch { }
}

if ($ManifestPath) {
    try { Invoke-AppSelfUpdate -ManifestPath $ManifestPath }
    catch {
        $logPath = Join-Path ([IO.Path]::GetDirectoryName($ManifestPath)) 'update.log'
        $message = "Die Aktualisierung konnte nicht abgeschlossen werden.`r`n$($_.Exception.Message)`r`n`r`nProtokoll: $logPath"
        try { [IO.File]::WriteAllText($logPath, $message) } catch { }
        # Before readiness, the still-running parent displays the error itself.
        if ([IO.File]::Exists((Join-Path ([IO.Path]::GetDirectoryName($ManifestPath)) 'ready'))) {
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show($message, 'NovaTouch Updater - Aktualisierung', 'OK', 'Error') | Out-Null
        }
        exit 1
    }
}
