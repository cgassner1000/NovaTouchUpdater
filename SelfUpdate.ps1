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
    $target = [IO.Path]::GetFullPath($settings.ExecutablePath)
    $staged = [IO.Path]::GetFullPath($settings.StagedPath)
    $backup = Join-Path $directory 'previous.exe'
    $ready = Join-Path $directory 'ready'
    $replaced = $false
    # Validate every final absolute path before moving or deleting files.
    if ([IO.Path]::GetDirectoryName($directory) -ne [IO.Path]::GetDirectoryName($target) -or
        [IO.Path]::GetFileName($directory) -notmatch '^\.NovaTouchUpdater-update-[a-f0-9]{32}$' -or
        $staged -ne (Join-Path $directory 'new.exe') -or
        [IO.Path]::GetExtension($target) -ne '.exe' -or -not [IO.File]::Exists($target) -or [IO.File]::Exists($backup)) {
        throw 'Die Pfade für den EXE-Austausch sind ungültig.'
    }
    try {
        if ((Get-Item -LiteralPath $staged).Length -ne $settings.Size -or
            (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $settings.Sha256) {
            throw 'Die vorbereitete EXE stimmt nicht mit dem geprüften Download überein.'
        }
        [IO.File]::WriteAllText($ready, 'ready')
        & $WaitForApplication $settings
        # File.Replace is atomic and retains a backup until starting succeeds.
        $lastError = $null
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try {
                [IO.File]::Replace($staged, $target, $backup, $true)
                $replaced = $true
                break
            } catch [IO.IOException] {
                $lastError = $_
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $replaced) { throw $lastError }
        & $StartApplication $target
    } catch {
        $failure = $_
        if ($replaced) {
            try {
                # PowerShell 5.1 converts $null to an empty string for this
                # overload. Use an explicit backup path for the rejected EXE.
                [IO.File]::Replace($backup, $target, (Join-Path $directory 'failed.exe'), $true)
                & $StartApplication $target
            } catch {
                throw "Update fehlgeschlagen: $failure | Wiederherstellung/Neustart: $_ | Sicherungsordner: $directory"
            }
        }
        throw $failure
    }
    # Delete only our own files; never recurse through the application directory.
    foreach ($file in @($backup, $ready, (Join-Path $directory 'SelfUpdate.ps1'), $ManifestPath)) {
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
