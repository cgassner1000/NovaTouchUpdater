# Serialized into authenticated remote sessions by RemoteWorker.ps1.
function PrepareUpdatePackages {
    param([string]$base, [string]$kindsCsv, [string]$pZip, [string]$fZip, [string]$sZip)
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $basePath = [IO.Path]::GetFullPath($base)
    $basePrefix = $basePath.TrimEnd('\') + '\'
    $dir = [IO.Path]::GetFullPath((Join-Path $basePath ('.novatouch-updater-stage-' + [guid]::NewGuid().ToString('N'))))
    if (-not $dir.StartsWith($basePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Ungültiger temporärer Update-Pfad.' }
    New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null
    try {
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem -ErrorAction Stop
        $archives = @{ payment=$pZip; fiscal=$fZip; serversuite=$sZip }
        foreach ($kind in ($kindsCsv -split ',')) {
            if (-not $archives.ContainsKey($kind)) { throw "Unbekannte Update-Komponente: $kind" }
            $zip = [string]$archives[$kind]
            if ([string]::IsNullOrWhiteSpace($zip) -or -not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Verifizierte ZIP für $kind fehlt: $zip" }
            $dest = Join-Path $dir $kind
            New-Item -ItemType Directory -Path $dest -Force -ErrorAction Stop | Out-Null
            $destPrefix = [IO.Path]::GetFullPath($dest).TrimEnd('\') + '\'
            $archive = [IO.Compression.ZipFile]::OpenRead($zip)
            try {
                foreach ($entry in $archive.Entries) {
                    $relative = $entry.FullName.Replace('/', '\')
                    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw "Ungültiger ZIP-Eintrag: $relative" }
                    $entryPath = [IO.Path]::GetFullPath((Join-Path $dest $relative))
                    if (-not $entryPath.StartsWith($destPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "ZIP-Eintrag verlässt das Update-Verzeichnis: $relative" }
                }
            }
            finally { $archive.Dispose() }
            Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force -ErrorAction Stop
            if (@(Get-ChildItem -LiteralPath $dest -Force).Count -eq 0) { throw "ZIP ist leer: $zip" }
            if ($kind -eq 'serversuite') {
                foreach ($relative in @('apps\nt3-server-suite', 'installer\python\portable_python')) {
                    $payload = Join-Path $dest $relative
                    if (-not (Test-Path -LiteralPath $payload -PathType Container) -or
                        @(Get-ChildItem -LiteralPath $payload -Force -Recurse -File).Count -eq 0) {
                        throw "Die Server-Suite-ZIP muss den gefüllten Ordner $relative enthalten."
                    }
                }
            }
        }
        $dir
    }
    catch {
        # Only the GUID directory created directly under this configured base.
        if ($dir.StartsWith($basePrefix, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($dir) -match '^\.novatouch-updater-stage-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function InstallUpdatePackages {
    param([string]$wantedJson, [string]$stageDir, [string]$serverSuiteTarget)
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    function UpdateProgress([string]$phase, [string]$message) {
        # Stream these records through Invoke-Command while the update runs.
        [pscustomobject]@{ Event='progress'; Phase=$phase; Message=$message }
    }
    function FullPath([string]$path) {
        if ([string]::IsNullOrWhiteSpace($path) -or $path -notmatch '^[A-Za-z]:\\') { throw "Kein absoluter Windows-Pfad: '$path'" }
        $full = [IO.Path]::GetFullPath($path)
        if ($full.TrimEnd('\').Length -le [IO.Path]::GetPathRoot($full).TrimEnd('\').Length) { throw 'Ein Laufwerksstamm darf kein Installationsziel sein.' }
        $full.TrimEnd('\')
    }
    function ContainsPath([string]$parent, [string]$child) {
        $child.Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or $child.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    function NoLinks([string]$path, [bool]$tree) {
        $cursor = $path
        while ($cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Verknüpfungen/Junctions sind als Austauschpfad nicht erlaubt: $cursor" }
            $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        }
        if ($tree) {
            $pendingDirs = [Collections.Generic.Stack[string]]::new()
            $pendingDirs.Push($path)
            while ($pendingDirs.Count -gt 0) {
                foreach ($item in Get-ChildItem -LiteralPath $pendingDirs.Pop() -Force -ErrorAction Stop) {
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Verknüpfung im Austauschordner: $($item.FullName)" }
                    if ($item.PSIsContainer) { $pendingDirs.Push($item.FullName) }
                }
            }
        }
    }
    function CheckSuiteVersion([string]$source) {
        $versionFile = Join-Path $source 'version.txt'
        if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) { throw 'Die Server-Suite-ZIP enthält keine apps\nt3-server-suite\version.txt.' }
        $version = [string](Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8 -ErrorAction Stop)
        $installed = [regex]::Match($version, '\d+\.\d+\.\d+(?:\.\d+)?').Value
        $expected = [regex]::Match($serverSuiteTarget, '\d+(?:\.\d+){1,3}').Value
        $versionMatches = $expected -and ($installed -eq $expected)
        if ($expected -match '^\d+\.\d+\.\d+$') { $versionMatches = $installed -match ('^' + [regex]::Escape($expected) + '(?:\.\d+)?$') }
        if (-not $versionMatches) { throw "Server-Suite-ZIP enthält in version.txt Version '$($version.Trim())', erwartet '$serverSuiteTarget'." }
    }
    function CopyContents([string]$source, [string]$destination) {
        New-Item -ItemType Directory -Path $destination -Force -ErrorAction Stop | Out-Null
        foreach ($folder in Get-ChildItem -LiteralPath $source -Directory -Recurse -Force -ErrorAction Stop) {
            $relative = $folder.FullName.Substring($source.Length).TrimStart('\')
            New-Item -ItemType Directory -Path (Join-Path $destination $relative) -Force -ErrorAction Stop | Out-Null
        }
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse -Force -ErrorAction Stop) {
            $relative = $file.FullName.Substring($source.Length).TrimStart('\')
            $destinationFile = Join-Path $destination $relative
            if ([IO.Directory]::Exists($destinationFile)) { throw "Eine Datei kann keinen vorhandenen Ordner ersetzen: $destinationFile" }
            Copy-Item -LiteralPath $file.FullName -Destination $destinationFile -Force -ErrorAction Stop
        }
    }
    function CheckServiceExecutable([string]$exe, [string]$target, [string]$source, [string]$name) {
        if (ContainsPath $target $exe) {
            # Installation creates instance-specific service EXEs that are not
            # part of the update ZIP. Overlay copying preserves these files.
            if (Test-Path -LiteralPath $exe -PathType Leaf) { return }
            $relativeExe = $exe.Substring($target.Length).TrimStart('\')
            $payloadExe = FullPath (Join-Path $source $relativeExe)
            if (-not (ContainsPath $source $payloadExe) -or -not (Test-Path -LiteralPath $payloadExe -PathType Leaf)) {
                throw "Die Dienst-EXE für '$name' fehlt sowohl auf dem Client als auch in der ZIP: $relativeExe"
            }
        }
    }
    function PythonDirectory([string]$exe) {
        $cursor = [IO.Path]::GetDirectoryName((FullPath $exe))
        while ($cursor) {
            if ([IO.Path]::GetFileName($cursor.TrimEnd('\')) -ieq 'portable_python') { return (FullPath $cursor) }
            $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        }
        throw "Kein Ordner portable_python im registrierten Dienst-EXE-Pfad: $exe"
    }
    function CheckTarget([string]$target, [bool]$tree, [bool]$allowMissing) {
        if (Test-Path -LiteralPath $target) {
            if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "Installationsziel ist kein Ordner: $target" }
            NoLinks $target $tree
        } else {
            if (-not $allowMissing) { throw "Installationsverzeichnis fehlt: $target" }
            $ancestor = [IO.Path]::GetDirectoryName($target)
            while ($ancestor -and -not (Test-Path -LiteralPath $ancestor)) { $ancestor = [IO.Path]::GetDirectoryName($ancestor.TrimEnd('\')) }
            if (-not $ancestor -or -not (Test-Path -LiteralPath $ancestor -PathType Container)) { throw "Installationsverzeichnis nicht anlegbar: $target" }
            NoLinks $ancestor $false
        }
    }
    function AddPlan([string]$kind, [string]$source, [string]$target, [string]$expectedLeaf = '', [bool]$allowMissing = $false) {
        $source = FullPath $source
        $target = FullPath $target
        if ($expectedLeaf -and [IO.Path]::GetFileName($target) -ine $expectedLeaf) { throw "Ungültiges Austauschziel: $target" }
        if (-not (Test-Path -LiteralPath $source -PathType Container) -or @(Get-ChildItem -LiteralPath $source -Force).Count -eq 0) {
            throw "Leeres oder fehlendes Update-Verzeichnis für $kind : $source"
        }
        if ($expectedLeaf -and @(Get-ChildItem -LiteralPath $source -Force -Recurse -File).Count -eq 0) { throw "Keine Dateien im Update-Verzeichnis: $source" }
        if ((ContainsPath $target $stage) -or (ContainsPath $stage $target)) { throw 'Staging und Installationsziel dürfen nicht ineinander liegen.' }
        $parent = [IO.Path]::GetDirectoryName($target)
        CheckTarget $target ([bool]$expectedLeaf) $allowMissing
        # Every service retains its own stop/start, but shared folders are copied once.
        $plans[$kind + '|' + $target.ToLowerInvariant()] = [pscustomobject]@{
            Kind=$kind; Source=$source; Target=$target; ExpectedLeaf=$expectedLeaf; Parent=$parent; AllowMissing=$allowMissing
        }
    }

    $wanted = @()
    foreach ($entry in (ConvertFrom-Json -InputObject $wantedJson -ErrorAction Stop)) {
        if ($entry -is [array]) { foreach ($item in $entry) { $wanted += $item } } else { $wanted += $entry }
    }
    if ($wanted.Count -eq 0) { throw 'Keine Dienste an den Installationsschritt übergeben.' }
    $stage = FullPath $stageDir
    NoLinks $stage $true
    $plans = @{}
    $suiteNames = @()
    $servicesToStart = @()
    foreach ($svc in $wanted) {
        foreach ($property in @('Name','Kind','InstallDir','StartType')) {
            if ($null -eq $svc -or $null -eq $svc.PSObject.Properties[$property]) { throw "Ungültiger Dienst-Datensatz: '$property' fehlt." }
        }
        $name = [string]$svc.Name
        $kind = [string]$svc.Kind
        $valid = ($kind -in @('payment','fiscal') -and $name -match ('(?i)^nt\.' + [regex]::Escape($kind))) -or
            ($kind -eq 'serversuite' -and $name -match '(?i)^nt3_server_suite')
        if (-not $valid) { throw "Ungültige Dienst-Zuordnung: '$name' / '$kind'." }
        $source = FullPath (Join-Path $stage $kind)
        $target = FullPath ([string]$svc.InstallDir)
        if ($kind -eq 'serversuite') {
            if ($null -eq $svc.PSObject.Properties['ExePath']) { throw "Dienstpfad fehlt: $name" }
            if ([IO.Path]::GetFileName($target) -ine 'nt3-server-suite') { throw "Kein eindeutiger Ordner nt3-server-suite im Installationspfad: $target" }
            $apps = [IO.Path]::GetDirectoryName($target)
            if ([IO.Path]::GetFileName($apps) -ine 'apps') { throw "Der Server-Suite-Ordner muss direkt unter apps liegen: $target" }
            $exe = FullPath ([string]$svc.ExePath)
            $pythonTarget = PythonDirectory $exe
            $installationRoot = [IO.Path]::GetDirectoryName($pythonTarget)
            $expectedSuite = FullPath (Join-Path $installationRoot 'apps\nt3-server-suite')
            if (-not $target.Equals($expectedSuite, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Dienstpfad und Server-Suite-Installationspfad passen nicht zusammen für '$name': erwartet '$expectedSuite', ermittelt '$target'."
            }
            $pythonSource = FullPath (Join-Path $source 'installer\python\portable_python')
            $source = FullPath (Join-Path $source 'apps\nt3-server-suite')
            CheckServiceExecutable $exe $target $source $name
            CheckServiceExecutable $exe $pythonTarget $pythonSource $name
            CheckSuiteVersion $source
            AddPlan $kind $source $target 'nt3-server-suite' $true
            AddPlan $kind $pythonSource $pythonTarget 'portable_python' $true
            $suiteNames += $name
        } else {
            AddPlan $kind $source $target
        }
        if ([string]$svc.StartType -ne 'Disabled') { $servicesToStart += $name }
    }
    $allPlans = @($plans.Values)
    foreach ($suitePlan in @($allPlans | Where-Object Kind -eq 'serversuite')) {
        foreach ($other in $allPlans) {
            if ($other -eq $suitePlan) { continue }
            if ((ContainsPath $suitePlan.Target $other.Target) -or (ContainsPath $other.Target $suitePlan.Target)) { throw 'Überlappende Installationsziele; keine Dateien wurden kopiert.' }
        }
    }
    if ($suiteNames.Count -gt 0) {
        $currentNames = @(Get-Service -ErrorAction Stop | Where-Object { $_.Name -match '^nt3_server_suite' } | ForEach-Object { $_.Name })
        $expectedNames = @($suiteNames | Sort-Object -Unique)
        $actualNames = @($currentNames | Sort-Object -Unique)
        if ($expectedNames.Count -ne $actualNames.Count -or
            @($expectedNames | Where-Object { $_ -notin $actualNames }).Count -gt 0) { throw 'Server-Suite-Dienste haben sich während der Vorbereitung geändert. Bitte erneut prüfen.' }
    }

    $changes = @()
    $operationError = ''
    $restartErrors = @()
    try {
        # Stop every selected service before the first file or directory change.
        foreach ($svc in $wanted) {
            $service = Get-Service -Name ([WildcardPattern]::Escape([string]$svc.Name)) -ErrorAction Stop
            if ($service.Status -ne 'Stopped') {
                UpdateProgress 'Dienste stoppen' ("Stoppe Dienst '$($svc.Name)'")
                Stop-Service -Name ([WildcardPattern]::Escape([string]$svc.Name)) -Force -ErrorAction Stop
                $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
                UpdateProgress 'Dienste stoppen' ("Dienst '$($svc.Name)' ist gestoppt")
            }
        }
        foreach ($plan in $allPlans) {
            $component = switch ($plan.Kind) {
                'payment' { 'nt.payment' }
                'fiscal' { 'nt.fiscal' }
                'serversuite' { if ($plan.ExpectedLeaf -eq 'portable_python') { 'Server Suite / Python' } else { 'Server Suite' } }
            }
            UpdateProgress 'Update installieren' ("$component : Dateien werden installiert nach $($plan.Target)")
            if ($plan.ExpectedLeaf) {
                # Recheck the exact target before copying; never delete client folders.
                $checkedTarget = FullPath $plan.Target
                if ([IO.Path]::GetFileName($checkedTarget) -ine $plan.ExpectedLeaf -or
                    -not $checkedTarget.Equals($plan.Target, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [IO.Path]::GetDirectoryName($checkedTarget).Equals($plan.Parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Ungültiges Server-Suite-Austauschziel.' }
                CheckTarget $checkedTarget $true $plan.AllowMissing
            }
            CopyContents $plan.Source $plan.Target
            $changes += ($plan.Kind + ' => ' + $plan.Target)
        }
    }
    catch { $operationError = $_.Exception.Message }
    finally {
        foreach ($name in @($servicesToStart | Select-Object -Unique)) {
            try {
                UpdateProgress 'Dienste starten' ("Starte Dienst '$name'")
                Start-Service -Name ([WildcardPattern]::Escape($name)) -ErrorAction Stop
                (Get-Service -Name ([WildcardPattern]::Escape($name)) -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
                UpdateProgress 'Dienste starten' ("Dienst '$name' läuft")
            }
            catch { $restartErrors += "Dienst '$name' konnte nicht wieder gestartet werden: $($_.Exception.Message)" }
        }
    }
    $errors = @($operationError) + @($restartErrors) | Where-Object { $_ }
    if (@($errors).Count -gt 0) { throw ($errors -join ' | ') }
    return ,$changes
}

function RemoveUpdateStage {
    param([string]$dir, [string]$base)
    $full = [IO.Path]::GetFullPath($dir).TrimEnd('\')
    $basePath = [IO.Path]::GetFullPath($base).TrimEnd('\')
    if (-not [IO.Path]::GetDirectoryName($full).Equals($basePath, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notmatch '^\.novatouch-updater-stage-[a-f0-9]{32}$') { throw 'Ungültiger Pfad für die Update-Bereinigung.' }
    Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
}
