# Source discovery executes on the selected host; destinations always belong to the updater.
function GetLogCollectionPlan([string]$servicesJson, [string]$systemProfile = '', [string]$excludedDirectory = '') {
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $files = [Collections.Generic.List[object]]::new()
    $sources = [Collections.Generic.List[string]]::new()
    $warnings = [Collections.Generic.List[string]]::new()
    $groups = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    function NewLogGroup([string]$label) {
        $safe = ([regex]::Replace($label, '[<>:"/\\|?*\x00-\x1f]', '_')).TrimEnd([char[]]' .')
        if (-not $safe) { $safe = 'Unbenannt' }
        $name = $safe; $number = 1
        while (-not $groups.Add($name)) { $number++; $name = "$safe ($number)" }
        return $name
    }
    function AddLogFolder([string]$directory, [string]$group, [bool]$eptOnly = $false) {
        try {
            if (-not (Test-Path -LiteralPath $directory -PathType Container)) { $warnings.Add("Logordner fehlt: $directory"); return }
            $root = Get-Item -LiteralPath $directory -Force
            if ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) { $warnings.Add("Verknüpfter Logordner übersprungen: $directory"); return }
            $sources.Add("$group <= $directory")
            $prefix = $root.FullName.TrimEnd('\') + '\'
            $pending = [Collections.Generic.Stack[string]]::new()
            $pending.Push($root.FullName)
            while ($pending.Count -gt 0) {
                $current = $pending.Pop()
                try { $entries = @(Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop) }
                catch { $warnings.Add("Ordner nicht lesbar: $current - $($_.Exception.Message)"); continue }
                foreach ($entry in $entries) {
                    if ($excludedDirectory -and ($entry.FullName.Equals($excludedDirectory, [StringComparison]::OrdinalIgnoreCase) -or
                        $entry.FullName.StartsWith($excludedDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { continue }
                    if ($excludedDirectory -and [IO.Path]::GetDirectoryName($entry.FullName).Equals([IO.Path]::GetDirectoryName($excludedDirectory), [StringComparison]::OrdinalIgnoreCase)) {
                        if ($entry.PSIsContainer -and $entry.Name -match '^(?:NovaTouch LOG (?:.+ )?)?\d{4}-\d{2}-\d{2} \d{2}-\d{2}(?: \(\d+\))?$' -and
                            (Test-Path -LiteralPath (Join-Path $entry.FullName 'Sammlung.txt') -PathType Leaf)) { continue }
                        if (-not $entry.PSIsContainer -and $entry.Name -match '^NovaTouch LOG (?:.+ )?\d{4}-\d{2}-\d{2} \d{2}-\d{2}(?: \(\d+\))?\.zip(?:\.partial)?$') { continue }
                    }
                    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                        $warnings.Add("Verknüpfung übersprungen: $($entry.FullName)"); continue
                    }
                    if ($entry.PSIsContainer) { if (-not $eptOnly) { $pending.Push($entry.FullName) }; continue }
                    if ($eptOnly -and $entry.Extension -ine '.ept') { continue }
                    if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Datei außerhalb des Logordners.' }
                    $files.Add([pscustomobject]@{
                        Source = $entry.FullName
                        RelativePath = Join-Path $group $entry.FullName.Substring($prefix.Length)
                        LastWriteTimeUtc = $entry.LastWriteTimeUtc
                    })
                }
            }
        }
        catch { $warnings.Add("$directory - $($_.Exception.Message)") }
    }

    if (-not $systemProfile) { $systemProfile = Join-Path $env:SystemRoot 'System32\config\systemprofile' }
    $services = $servicesJson | ConvertFrom-Json
    foreach ($service in $services) {
        $name = [string]$service.Name
        if ($service.Kind -in @('payment', 'fiscal')) {
            # Current services use Roaming\Novacom; retain older Local layouts as fallbacks.
            $candidates = @('AppData\Roaming\Novacom', 'AppData\Local', 'Local', 'lokal') | ForEach-Object {
                Join-Path (Join-Path (Join-Path $systemProfile $_) $name) 'logs'
            }
            $directory = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1)
            if ($directory.Count -eq 0) { $warnings.Add("$name : kein Logordner unter $systemProfile gefunden"); continue }
            $label = if ($service.Kind -eq 'payment') { 'Nt.Payment.' } else { 'Nt.Fiskal.' }
            AddLogFolder $directory[0] (NewLogGroup ($label + $name))
        }
        elseif ($service.Kind -eq 'serversuite') {
            $suiteRoot = [string]$service.RegistryInstallDir
            if (-not $suiteRoot -and [string]$service.InstallDir -match '[\\/]apps[\\/]nt3-server-suite[\\/]?$') {
                $suiteRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName(([string]$service.InstallDir).TrimEnd('\')))
            }
            if (-not $suiteRoot) { $warnings.Add("$name : Server.suite-Installationsstamm nicht ermittelbar"); continue }
            AddLogFolder (Join-Path $suiteRoot 'logs') (NewLogGroup ('Server.suite.' + $name))
        }
    }

    try {
        $posGroup = NewLogGroup 'NovaTouch POS'
        $seenProfiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($profile in @(Get-CimInstance -ClassName Win32_UserProfile -ErrorAction Stop | Where-Object { -not $_.Special })) {
            $profilePath = [string]$profile.LocalPath
            if (-not $profilePath -or -not $seenProfiles.Add($profilePath)) { continue }
            foreach ($source in @(
                @{ Directory=(Join-Path $profilePath 'AppData\Local\VirtualStore\Program Files (x86)\novatouch\bin'); EptOnly=$true }
                @{ Directory=(Join-Path $profilePath 'AppData\Roaming\novacom software gmbh\novatouch\logs'); EptOnly=$false }
            )) {
                try {
                    if (-not (Test-Path -LiteralPath $source.Directory -PathType Container)) { continue }
                    AddLogFolder $source.Directory $posGroup $source.EptOnly
                }
                catch { $warnings.Add("POS-Logordner nicht lesbar: $($source.Directory) - $($_.Exception.Message)") }
            }
        }
    }
    catch { $warnings.Add("POS-Benutzerprofile konnten nicht vollständig gelesen werden: $($_.Exception.Message)") }
    $selected = [Collections.Generic.List[object]]::new()
    $posFiles = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        if (-not $file.RelativePath.StartsWith('NovaTouch POS\', [StringComparison]::OrdinalIgnoreCase)) { $selected.Add($file); continue }
        $previous = $null
        if (-not $posFiles.TryGetValue([string]$file.RelativePath, [ref]$previous)) { $posFiles.Add($file.RelativePath, $file); continue }
        $useNew = $file.LastWriteTimeUtc -gt $previous.LastWriteTimeUtc -or
            ($file.LastWriteTimeUtc -eq $previous.LastWriteTimeUtc -and [StringComparer]::OrdinalIgnoreCase.Compare($file.Source, $previous.Source) -lt 0)
        $chosen = if ($useNew) { $file } else { $previous }
        $skipped = if ($useNew) { $previous } else { $file }
        $sources.Add("Mehrfach vorhandene POS-Datei '$($file.RelativePath)': neueste Quelle '$($chosen.Source)'; weitere Quelle '$($skipped.Source)' nicht zusätzlich kopiert.")
        if ($useNew) { $posFiles[$file.RelativePath] = $file }
    }
    foreach ($file in $posFiles.Values) { $selected.Add($file) }
    return [pscustomobject]@{ Files = @($selected.ToArray()); Sources = @($sources.ToArray()); Warnings = @($warnings.ToArray()) }
}

function GetLogCollectionDestination([string]$root, [string]$relativePath) {
    if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains(':') -or @($relativePath.Split([char[]]'\/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw 'Ungültiger relativer Logdateipfad.'
    }
    $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $destination = [IO.Path]::GetFullPath((Join-Path $root $relativePath))
    if (-not $destination.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Logdateipfad außerhalb des Sammelordners.' }
    if (Test-Path -LiteralPath $destination) { throw "Zieldatei existiert bereits: $destination" }
    $parent = Split-Path -Parent $destination
    [void][IO.Directory]::CreateDirectory($parent)
    $cursor = $parent
    while ($cursor.Length -ge $prefix.Length) {
        if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Verknüpfung im Sammelordner ist nicht erlaubt.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $destination
}

function CopyLocalLogFile([string]$sourcePath, [string]$destination) {
    $source = $null; $target = $null
    try {
        $source = [IO.FileStream]::new($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $target = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        # Copy a finite snapshot, even while a service keeps appending to its log.
        $remaining = $source.Length
        $buffer = [byte[]]::new(131072)
        while ($remaining -gt 0) {
            $count = $source.Read($buffer, 0, [int][Math]::Min($remaining, $buffer.Length))
            if ($count -eq 0) { throw 'Logdatei wurde während des Kopierens gekürzt; Kopie ist unvollständig.' }
            $target.Write($buffer, 0, $count); $remaining -= $count
        }
    }
    finally { if ($null -ne $target) { $target.Dispose() }; if ($null -ne $source) { $source.Dispose() } }
}

function ReadRemoteLogFile([string]$path) {
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $source = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $length = $source.Length; $offset = [long]0
        while ($offset -lt $length) {
            $buffer = [byte[]]::new([int][Math]::Min(131072, $length - $offset))
            $count = $source.Read($buffer, 0, $buffer.Length)
            if ($count -eq 0) { throw 'Logdatei wurde während des Kopierens gekürzt.' }
            if ($count -ne $buffer.Length) {
                $shortBuffer = [byte[]]::new($count)
                [Buffer]::BlockCopy($buffer, 0, $shortBuffer, 0, $count)
                $buffer = $shortBuffer
            }
            [pscustomobject]@{ Offset = $offset; Data = $buffer }
            $offset += $count
        }
        [pscustomobject]@{ Complete = $true; Length = $length }
    }
    finally { $source.Dispose() }
}

function CopyRemoteLogFile($session, [string]$sourcePath, [string]$destination) {
    $target = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $state = @{ Length = [long]0; Complete = $false }
        # One streaming invocation per file, without temporary files on the client.
        InvokeWorkerCommand -Session $session -ScriptBlock ${function:ReadRemoteLogFile} -ArgumentList @($sourcePath) | ForEach-Object {
            if ($state.Complete) { throw 'Unerwartete Daten nach dem Dateiende.' }
            if ($_.PSObject.Properties['Complete']) {
                if (-not $_.Complete -or [long]$_.Length -ne $state.Length) { throw 'Unvollständige Logdatei-Übertragung.' }
                $state.Complete = $true
            }
            else {
                if ([long]$_.Offset -ne $state.Length) { throw 'Falsche Reihenfolge der Logdateiblöcke.' }
                $bytes = [byte[]]$_.Data
                if ($bytes.Length -eq 0 -or $bytes.Length -gt 131072) { throw 'Ungültiger Logdateiblock.' }
                $target.Write($bytes, 0, $bytes.Length)
                $state.Length += $bytes.Length
            }
        }
        if (-not $state.Complete) { throw 'Logdatei-Übertragung wurde nicht vollständig abgeschlossen.' }
    }
    finally { $target.Dispose() }
}

function CollectHostLogs($request, $session) {
    $root = [IO.Path]::GetFullPath([string]$request.LogCollectionDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $root 'Sammlung.txt') -PathType Leaf)) { throw 'Der reservierte Sammelordner fehlt.' }
    Progress 'Collect Logs' 'Logquellen werden ermittelt' 0
    $services = @(InvokeWorkerCommand -Session $session -ScriptBlock ${function:ReadInventory})
    $servicesJson = ConvertTo-Json -InputObject $services -Depth 12 -Compress
    $excludedDirectory = if (TestLocalSession $session) { $root } else { '' }
    $plan = InvokeWorkerCommand -Session $session -ScriptBlock ${function:GetLogCollectionPlan} -ArgumentList @($servicesJson, '', $excludedDirectory)
    $warnings = [Collections.Generic.List[string]]::new()
    foreach ($warning in @($plan.Warnings)) { $warnings.Add([string]$warning) }
    $report = @(('Quellen auf ' + [string]$request.Host), '') + @($plan.Sources) + @('', 'Hinweise:') + @($plan.Warnings)
    [IO.File]::WriteAllLines((Join-Path $root 'Quellen.txt'), [string[]]$report, [Text.UTF8Encoding]::new($true))
    $files = @($plan.Files); $copied = 0; $failed = 0; $index = 0
    foreach ($file in $files) {
        $destination = $null; $copyAttempted = $false
        try {
            $destination = GetLogCollectionDestination $root ([string]$file.RelativePath)
            Progress 'Collect Logs' ([string]$file.RelativePath) ([int](100 * $index / [Math]::Max(1, $files.Count)))
            $copyAttempted = $true
            if (TestLocalSession $session) { CopyLocalLogFile ([string]$file.Source) $destination }
            else { CopyRemoteLogFile $session ([string]$file.Source) $destination }
            $copied++
            try { [IO.File]::SetLastWriteTimeUtc($destination, [datetime]$file.LastWriteTimeUtc) }
            catch { $warnings.Add("Dateizeit nicht übernommen: $($file.RelativePath) - $($_.Exception.Message)") }
        }
        catch {
            $failed++; $warnings.Add("Nicht kopiert: $($file.Source) - $($_.Exception.Message)")
            if ($copyAttempted -and $destination -and (Test-Path -LiteralPath $destination -PathType Leaf)) {
                Remove-Item -LiteralPath $destination -ErrorAction SilentlyContinue
            }
        }
        $index++
    }
    $message = "$copied Logdatei(en) gesammelt"
    if ($files.Count -eq 0) { $message = 'Keine passenden Logdateien gefunden' }
    if ($failed -gt 0) { $message += "; $failed Datei(en) konnten nicht kopiert werden" }
    [IO.File]::AppendAllLines((Join-Path $root 'Quellen.txt'), [string[]](@('', $message, 'Hinweise nach dem Kopieren:') + @($warnings.ToArray())), [Text.UTF8Encoding]::new($true))
    return @{ Event = 'result'; Success = ($copied -gt 0 -and $failed -eq 0); Changed = ($copied -gt 0);
        Message = $message; LogFileCount = $copied; LogCollectionWarnings = @($warnings.ToArray()); Services = @() }
}
