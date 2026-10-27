function GetMsiUpdateDecision {
    param([object[]]$Programs, $Package)
    $names = @{ pos='NovaTouch POS'; ntc='NovaTouch Control'; ntv='NovaTouch Voucher'; oman='NovaTouch Orderman SOL' }
    $kind = [string]$Package.Kind
    if (-not $names.ContainsKey($kind) -or (([string]$Package.ProductName).Trim() -replace '\s+', ' ') -ine $names[$kind]) { throw 'MSI-Produkt passt nicht zur gewählten Komponente.' }
    function MsiVersion([string]$value) {
        $parsed = $null
        if (-not [Version]::TryParse($value.Trim(), [ref]$parsed)) { throw "MSI-Version nicht sicher vergleichbar: '$value'" }
        # Windows Installer compares major.minor.build; it ignores a fourth field.
        return [Version]::new($parsed.Major, $parsed.Minor, [Math]::Max(0, $parsed.Build))
    }
    $target = MsiVersion ([string]$Package.ProductVersion)
    $installed = @($Programs | Where-Object { $_.Kind -eq $kind -and $_.DisplayName -ieq $names[$kind] })
    if ($installed.Count -eq 0) { return [pscustomobject]@{ Install=$false; Skipped=$true; Message=($names[$kind] + ' nicht installiert – übersprungen'); Programs=@() } }
    foreach ($program in $installed) {
        if ((MsiVersion ([string]$program.Version)) -ge $target) {
            return [pscustomobject]@{ Install=$false; Skipped=$false; Message=($names[$kind] + ': gleiche oder neuere Version installiert (' + $program.Version + ')'); Programs=$installed }
        }
    }
    [pscustomobject]@{ Install=$true; Skipped=$false; Message=($names[$kind] + ' wird aktualisiert'); Programs=$installed }
}

function ExpandMsiUpdatePackage {
    param([string]$Base, [string]$Payload, [string]$RelativeMsi, [string]$CompanionsJson)
    $ErrorActionPreference = 'Stop'
    $basePath = [IO.Path]::GetFullPath($Base).TrimEnd('\')
    if ($basePath -eq [IO.Path]::GetPathRoot($basePath).TrimEnd('\')) { throw 'Ein Laufwerksstamm ist kein MSI-Arbeitsordner.' }
    $stage = Join-Path $basePath ('.novatouch-updater-msi-' + [guid]::NewGuid().ToString('N'))
    $decodedCompanions = ConvertFrom-Json -InputObject $CompanionsJson
    $Companions = @($decodedCompanions | Where-Object { $_ -is [string] -and $_.Length -gt 0 })
    [void][IO.Directory]::CreateDirectory($stage)
    function Child([string]$relative) {
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw "Ungültiger MSI-Paketpfad: $relative" }
        $full = [IO.Path]::GetFullPath((Join-Path $stage $relative.Replace('/', '\')))
        if (-not $full.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "MSI-Paketpfad verlässt Arbeitsordner: $relative" }
        $full
    }
    try {
        if ([IO.Path]::GetExtension($Payload) -ieq '.zip') {
            Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
            $archive = [IO.Compression.ZipFile]::OpenRead($Payload)
            try {
                $bytes = 0L; $done = 0L
                foreach ($entry in $archive.Entries) { $null = Child $entry.FullName; $bytes += $entry.Length }
                $clock = [Diagnostics.Stopwatch]::StartNew()
                foreach ($entry in $archive.Entries) {
                    $target = Child $entry.FullName
                    if (-not $entry.Name) { [void][IO.Directory]::CreateDirectory($target); continue }
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                    $source = $entry.Open(); $output = $null
                    try {
                        $output = [IO.File]::Create($target); $buffer = New-Object byte[] (1024 * 1024)
                        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $output.Write($buffer, 0, $read); $done += $read
                            if ($clock.ElapsedMilliseconds -ge 200) {
                                [pscustomobject]@{ Event='progress'; Phase='MSI entpacken'; Message=($entry.FullName + " · $done / $bytes Bytes"); ProgressPercent=[int][Math]::Min(99, 100.0 * $done / [Math]::Max(1, $bytes)) }
                                $clock.Restart()
                            }
                        }
                    } finally { if ($null -ne $output) { $output.Dispose() }; $source.Dispose() }
                }
            } finally { $archive.Dispose() }
        }
        else {
            Copy-Item -LiteralPath $Payload -Destination (Child $RelativeMsi) -Force -ErrorAction Stop
            foreach ($relative in $Companions) {
                $source = [IO.Path]::GetFullPath((Join-Path $basePath $relative))
                if (-not $source.StartsWith($basePath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Ungültiger MSI-CAB-Pfad.' }
                $target = Child $relative
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
            }
        }
        $msi = Child $RelativeMsi
        if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) { throw 'Die ausgewählte MSI fehlt im Paket.' }
        [pscustomobject]@{ Event='progress'; Phase='MSI entpacken'; Message='MSI-Paket vorbereitet'; ProgressPercent=100 }
        [pscustomobject]@{ Directory=$stage; MsiPath=$msi }
    }
    catch {
        # This fresh GUID directory is always a direct child of the supplied base.
        if ([IO.Path]::GetDirectoryName($stage) -eq $basePath) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
        throw
    }
}

function InstallMsiUpdatePackage {
    param([string]$MsiPath, [string]$PackageJson, [string]$InventorySource, [string]$DecisionSource, [string]$LogPath)
    $ErrorActionPreference = 'Stop'
    $package = ConvertFrom-Json $PackageJson
    $software = & ([scriptblock]::Create($InventorySource))
    $decision = & ([scriptblock]::Create($DecisionSource)) @($software.InstalledPrograms) $package
    if (-not $decision.Install) { return [pscustomobject]@{ Changed=$false; Skipped=$decision.Skipped; RebootRequired=$false; Software=$software; Message=$decision.Message } }

    # Validate the actual client MSI again after transfer/extraction.
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $serviceNames = @()
    try {
        $database = $installer.OpenDatabase($MsiPath, 0)
        foreach ($property in @('ProductName','ProductVersion','ProductCode')) {
            $view = $null; $record = $null
            try {
                $query = 'SELECT `Value` FROM `Property` WHERE `Property` = ''' + $property + ''''
                $view = $database.OpenView($query); $view.Execute(); $record = $view.Fetch()
                if ($null -eq $record -or [string]$record.StringData(1) -ine [string]$package.$property) { throw "MSI-$property stimmt nicht mit dem ausgewählten Paket überein." }
            } finally {
                if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            }
        }
        $view = $null; $record = $null
        try {
            $view = $database.OpenView('SELECT `Name` FROM `_Tables` WHERE `Name` = ''ServiceControl''')
            $view.Execute(); $record = $view.Fetch()
            $hasServiceControl = $null -ne $record
        } finally {
            if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
        if ($hasServiceControl) {
            $view = $database.OpenView('SELECT `Name` FROM `ServiceControl`')
            try {
                $view.Execute()
                while ($null -ne ($record = $view.Fetch())) {
                    try {
                        $name = [string]$record.StringData(1)
                        # Formatted MSI names are resolved by Windows Installer itself.
                        if ($name -and $name -notmatch '[\[\]]') { $serviceNames += $name }
                    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                }
            } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
    } finally {
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }

    $associated = @()
    foreach ($name in @($serviceNames | Sort-Object -Unique)) {
        $service = Get-Service -Name ([WildcardPattern]::Escape($name)) -ErrorAction SilentlyContinue
        if ($null -ne $service) { $associated += [pscustomobject]@{ Name=$service.Name; StartType=[string]$service.StartType } }
    }
    $operationError = ''; $restartErrors = @(); $exitCode = -1
    try {
        $stopped = 0
        foreach ($service in $associated) {
            $current = Get-Service -Name ([WildcardPattern]::Escape($service.Name)) -ErrorAction Stop
            if ($current.Status -ne 'Stopped') {
                [pscustomobject]@{ Event='progress'; Phase='Dienste stoppen'; Message=('Stoppe ' + $service.Name); ProgressPercent=[int](50.0 * $stopped / [Math]::Max(1, $associated.Count)) }
                Stop-Service -Name ([WildcardPattern]::Escape($service.Name)) -Force -ErrorAction Stop
                $current.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
            }
            $stopped++
        }
        [pscustomobject]@{ Event='progress'; Phase='MSI installieren'; Message=($package.ProductName + ' ' + $package.ProductVersion + ' wird installiert'); ProgressPercent=0 }
        $arguments = '/i "' + $MsiPath + '" /qn /norestart REBOOT=ReallySuppress /L*v "' + $LogPath + '"'
        if (@($decision.Programs | Where-Object { $_.ProductCode -ieq $package.ProductCode }).Count -gt 0) { $arguments += ' REINSTALL=ALL REINSTALLMODE=vomus' }
        $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
        $exitCode = $process.ExitCode
        if ($exitCode -notin @(0,3010,1641)) { throw "MSI-Installation fehlgeschlagen (ExitCode $exitCode). Protokoll: $LogPath" }
    }
    catch { $operationError = $_.Exception.Message }
    finally {
        foreach ($service in $associated) {
            try {
                $current = Get-Service -Name ([WildcardPattern]::Escape($service.Name)) -ErrorAction SilentlyContinue
                if ($null -eq $current) { continue } # MSI can legitimately replace a service.
                if ($service.StartType -eq 'Disabled') { Set-Service -Name $current.Name -StartupType Disabled -ErrorAction Stop; if ($current.Status -ne 'Stopped') { Stop-Service -Name $current.Name -Force -ErrorAction Stop }; continue }
                [pscustomobject]@{ Event='progress'; Phase='Dienste starten'; Message=('Starte ' + $service.Name); ProgressPercent=50 }
                Start-Service -Name ([WildcardPattern]::Escape($service.Name)) -ErrorAction Stop
                $current.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
            } catch { $restartErrors += ($service.Name + ': ' + $_.Exception.Message) }
        }
    }
    $errors = @($operationError) + @($restartErrors) | Where-Object { $_ }
    if (@($errors).Count -gt 0) { throw ($errors -join ' | ') }
    $after = & ([scriptblock]::Create($InventorySource))
    $verified = & ([scriptblock]::Create($DecisionSource)) @($after.InstalledPrograms) $package
    if ($verified.Install -or $verified.Skipped) { throw ('Installierte MSI-Version wurde nicht bestätigt. Protokoll: ' + $LogPath) }
    [pscustomobject]@{ Event='progress'; Phase='MSI verifiziert'; Message=($package.ProductName + ' installiert und geprüft'); ProgressPercent=100 }
    [pscustomobject]@{ Changed=$true; Skipped=$false; RebootRequired=($exitCode -in @(3010,1641)); Software=$after; Message='MSI installiert und verifiziert' }
}

function RemoveMsiUpdateStage {
    param([string]$Directory, [string]$Base)
    $full = [IO.Path]::GetFullPath($Directory).TrimEnd('\'); $parent = [IO.Path]::GetFullPath($Base).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($full) -ine $parent -or [IO.Path]::GetFileName($full) -notmatch '^\.novatouch-updater-msi-[a-f0-9]{32}$') { throw 'Ungültiger MSI-Bereinigungspfad.' }
    Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
}

function InvokeMsiPackageUpdate {
    param($Request, $Session)
    $package = $Request.MsiPackage
    $software = InvokeWorkerCommand -Session $Session -ScriptBlock ${function:ReadSoftwareInventory} -ErrorAction Stop
    $decision = GetMsiUpdateDecision @($software.InstalledPrograms) $package
    $outcome = $null
    if (-not $decision.Install) { $outcome = [pscustomobject]@{ Changed=$false; Skipped=$decision.Skipped; RebootRequired=$false; Software=$software; Message=$decision.Message } }
    else {
        if (TestLocalSession $Session) { AssertLocalUpdateAccess }
        $base = [string]$Request.RemoteInstallPath
        InvokeWorkerCommand -Session $Session -ArgumentList $base -ScriptBlock { param($path) [void][IO.Directory]::CreateDirectory($path) } -ErrorAction Stop
        $files = @([pscustomobject]@{ LocalPath=[string]$package.PayloadPath; RelativePath=[IO.Path]::GetFileName([string]$package.PayloadPath); Sha256=[string]$package.Sha256 }) + @($package.CompanionFiles)
        $remotePayload = ''
        foreach ($file in $files) {
            $remoteFile = [IO.Path]::GetFullPath((Join-Path $base ([string]$file.RelativePath)))
            if (-not $remoteFile.StartsWith([IO.Path]::GetFullPath($base).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'MSI-Dateipfad verlässt den Arbeitsordner.' }
            $remoteHash = InvokeWorkerCommand -Session $Session -ArgumentList $remoteFile -ScriptBlock { param($path) if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { '' } } -ErrorAction Stop
            if ([string]::IsNullOrWhiteSpace([string]$file.Sha256)) { throw 'MSI-Prüfsumme fehlt.' }
            if ($remoteHash -ne $file.Sha256) {
                if ([bool]$Request.SkipZipTransfer -and -not (TestLocalSession $Session)) { throw "MSI-Übertragung deaktiviert; Paket fehlt oder Prüfsumme weicht ab: $remoteFile" }
                InvokeWorkerCommand -Session $Session -ArgumentList ([IO.Path]::GetDirectoryName($remoteFile)) -ScriptBlock { param($dir) [void][IO.Directory]::CreateDirectory($dir) } -ErrorAction Stop
                CopyRemoteFileWithProgress $Session ([string]$file.LocalPath) $remoteFile ([string]$package.ProductName)
                $remoteHash = InvokeWorkerCommand -Session $Session -ArgumentList $remoteFile -ScriptBlock { param($path) (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash } -ErrorAction Stop
                if ($remoteHash -ne $file.Sha256) { throw 'MSI-Prüfsumme nach dem Kopieren stimmt nicht überein.' }
            }
            Progress 'MSI-Paket geprüft' ([IO.Path]::GetFileName($remoteFile)) 100
            if (-not $remotePayload) { $remotePayload = $remoteFile }
        }
        Progress 'MSI entpacken' ([string]$package.ProductName) 0
        $companionsJson = ConvertTo-Json -InputObject @($package.CompanionFiles | ForEach-Object { [string]$_.RelativePath }) -Compress
        $expandArgs = @($base, $remotePayload, [string]$package.RelativeMsiPath, $companionsJson)
        $stage = InvokeWorkerCommand -Session $Session -ArgumentList $expandArgs -ScriptBlock ${function:ExpandMsiUpdatePackage} -ErrorAction Stop |
            ForEach-Object { if ($null -eq $_) { return }; if ($null -ne $_.PSObject.Properties['Event'] -and $_.Event -eq 'progress') { Progress $_.Phase $_.Message $_.ProgressPercent } else { $_ } }
        try {
            $packageJson = $package | ConvertTo-Json -Depth 6 -Compress
            $log = Join-Path $base ('MSI-' + $package.Kind + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '.log')
            $installArgs = @($stage.MsiPath, $packageJson, ${function:ReadSoftwareInventory}.ToString(), ${function:GetMsiUpdateDecision}.ToString(), $log)
            $outcome = InvokeWorkerCommand -Session $Session -ArgumentList $installArgs -ScriptBlock ${function:InstallMsiUpdatePackage} -ErrorAction Stop |
                ForEach-Object { if ($null -eq $_) { return }; if ($null -ne $_.PSObject.Properties['Event'] -and $_.Event -eq 'progress') { Progress $_.Phase $_.Message $_.ProgressPercent } else { $_ } }
        }
        finally { InvokeWorkerCommand -Session $Session -ArgumentList $stage.Directory, $base -ScriptBlock ${function:RemoveMsiUpdateStage} -ErrorAction SilentlyContinue | Out-Null }
    }
    $services = @(InvokeWorkerCommand -Session $Session -ScriptBlock ${function:ReadInventory} -ErrorAction Stop)
    return @{ Event='result'; Success=$true; Message=$outcome.Message; Changed=$outcome.Changed; Skipped=$outcome.Skipped; RebootRequired=$outcome.RebootRequired;
        Services=$services; InstalledPrograms=@($outcome.Software.InstalledPrograms); DotNet48Installed=$outcome.Software.DotNet48Installed; PosVersion=$outcome.Software.PosVersion; NtcVersion=$outcome.Software.NtcVersion }
}
