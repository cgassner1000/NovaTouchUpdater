# Execution is local on the operator PC. Only authenticated PS remoting actions run on hosts.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)

function Emit([hashtable]$message) {
    [Console]::Out.WriteLine(($message | ConvertTo-Json -Compress -Depth 12))
    [Console]::Out.Flush()
}

function Progress([string]$phase, [string]$message) {
    Emit @{ Event='progress'; Phase=$phase; Message=$message }
}

function ReadInventory {
    $services = @(Get-Service | Where-Object { $_.Name -match '^nt\.(payment|fiscal)' })
    $inventory = @()
    foreach ($svc in $services) {
        $kind = if ($svc.Name -match '^nt\.payment') { 'payment' } else { 'fiscal' }
        $imagePath = (Get-ItemProperty -LiteralPath ("HKLM:\SYSTEM\CurrentControlSet\Services\" + $svc.Name) -Name ImagePath -ErrorAction Stop).ImagePath
        if ($imagePath -match '^\s*"([^"]+?\.exe)"') { $exe = $Matches[1] }
        elseif ($imagePath -match '^\s*(.+?\.exe)(?:\s|$)') { $exe = $Matches[1] }
        else { throw "Executable-Pfad für $($svc.Name) nicht lesbar: $imagePath" }
        $version = (Get-Item -LiteralPath $exe -ErrorAction Stop).VersionInfo
        $inventory += [pscustomobject]@{
            Name = $svc.Name
            Kind = $kind
            State = [string]$svc.Status
            StartType = [string]$svc.StartType
            ExePath = $exe
            InstallDir = [IO.Path]::GetDirectoryName($exe)
            ProductVersion = [string]$version.ProductVersion
            FileVersion = [string]$version.FileVersion
        }
    }
    return $inventory
}

function Normalize-Version([string]$version) {
    if ([string]::IsNullOrWhiteSpace($version)) { return '' }
    $m = [regex]::Match($version, '\d+(?:\.\d+){1,3}')
    if ($m.Success) { return $m.Value }
    return $version.Trim()
}
function IsTargetVersion($svc, [string]$expected) {
    if ([string]::IsNullOrWhiteSpace($expected)) { throw 'Zielversion fehlt.' }
    $target = Normalize-Version $expected
    # Dieselbe IST-Versionsquelle wie in der GUI: ProductVersion, ersatzweise FileVersion.
    $installed = if (-not [string]::IsNullOrWhiteSpace([string]$svc.ProductVersion)) {
        [string]$svc.ProductVersion
    } else {
        [string]$svc.FileVersion
    }
    return ($target -eq (Normalize-Version $installed))
}

try {
    $requestLine = [Console]::In.ReadLine()
    if ([string]::IsNullOrWhiteSpace($requestLine)) { throw 'Keine Anforderung erhalten.' }
    $req = $requestLine | ConvertFrom-Json
    $sessionOptions = New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 180000
    $sessionArgs = @{
        ComputerName = [string]$req.Host
        SessionOption = $sessionOptions
        ErrorAction = 'Stop'
    }
    if ([bool]$req.UseSsl) { $sessionArgs.UseSSL = $true }
    if (-not [string]::IsNullOrWhiteSpace([string]$req.Username)) {
        $secure = ConvertTo-SecureString -String ([string]$req.Password) -AsPlainText -Force
        $sessionArgs.Credential = New-Object System.Management.Automation.PSCredential([string]$req.Username, $secure)
    }

    Progress 'Verbindung' 'WinRM-Verbindung wird aufgebaut'
    $session = $null
    try {
        $session = New-PSSession @sessionArgs
        if ($req.Action -eq 'probe') {
            Emit @{ Event='result'; Success=$true; Message='WinRM erreichbar und Anmeldung erfolgreich'; Services=@() }
            exit 0
        }

        Progress 'Versionen' 'Dienste und installierte Versionen werden ausgelesen'
        $services = @(Invoke-Command -Session $session -ScriptBlock ${function:ReadInventory})
        if ($services.Count -eq 0) { throw 'Keine nt.payment-/nt.fiscal-Dienste gefunden.' }
        if ($req.Action -eq 'scan') {
            Emit @{ Event='result'; Success=$true; Message='Versionen gelesen'; Services=$services }
            exit 0
        }
        if ($req.Action -ne 'update') { throw ('Unbekannte Aktion: ' + $req.Action) }

        # Nur Komponenten mit explizit gewaehlter ZIP und Zielversion koennen aktualisiert werden.
        $targets = @{}
        if (-not [string]::IsNullOrWhiteSpace([string]$req.PaymentZip)) {
            if ([string]::IsNullOrWhiteSpace([string]$req.PaymentVersion)) { throw 'Payment-ZIP ausgewählt, aber Payment-Zielversion fehlt.' }
            $targets.payment = [string]$req.PaymentVersion
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$req.FiscalZip)) {
            if ([string]::IsNullOrWhiteSpace([string]$req.FiscalVersion)) { throw 'Fiscal-ZIP ausgewählt, aber Fiscal-Zielversion fehlt.' }
            $targets.fiscal = [string]$req.FiscalVersion
        }
        if ($targets.Count -eq 0) { throw 'Keine Update-ZIP ausgewählt.' }

        # Force Update setzt ausschliesslich den IST-/Zielversionsvergleich ausser Kraft.
        # Komponenten ohne ausgewaehltes ZIP bleiben IMMER unangetastet.
        $forceUpdate = $false
        if ($null -ne $req.PSObject.Properties['ForceUpdate']) {
            $forceUpdate = [bool]$req.ForceUpdate
        }
        if ($forceUpdate) {
            Progress 'Force Update' 'Versionsvergleich übersprungen; nur ausgewählte Komponenten werden aktualisiert'
        }

        # Filter VOR ZIP-Uebertragung, Entpacken und jeglichem Dienst-Stopp.
        $pending = @()
        foreach ($svc in $services) {
            if (-not $targets.ContainsKey($svc.Kind)) { continue }
            if (-not $forceUpdate -and (IsTargetVersion $svc $targets[$svc.Kind])) { continue }
            $pending += $svc
        }
        if ($pending.Count -eq 0) {
            Emit @{ Event='result'; Success=$true; Message='Bereits auf Zielversion / keine ausgewählten Dienste vorhanden'; Services=$services; Changed=$false }
            exit 0
        }

        Progress 'Übertragung' 'ZIP-Dateien werden auf den Host übertragen und geprüft'
        $remotePath = [string]$req.RemoteInstallPath
        if ([string]::IsNullOrWhiteSpace($remotePath)) { throw 'RemoteInstallPath wurde nicht übergeben.' }

        # Verzeichnis im Dateisystem des Hosts der bestehenden WinRM-Session anlegen.
        # System.IO umgeht eine moegliche falsche Provider-/Test-Path-Aufloesung.
        $remoteInfo = Invoke-Command -Session $session -ArgumentList $remotePath -ErrorAction Stop -ScriptBlock {
            param([string]$dir)
            $computer = [Environment]::MachineName
            $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            try {
                if ($dir -notmatch '^[A-Za-z]:\\') { throw "Ungueltiger absoluter Windows-Zielpfad: $dir" }
                $fullPath = [IO.Path]::GetFullPath($dir)
                $driveRoot = [IO.Path]::GetPathRoot($fullPath)
                if (-not [IO.Directory]::Exists($driveRoot)) {
                    throw "Laufwerk nicht verfuegbar: $driveRoot"
                }
                [void][IO.Directory]::CreateDirectory($fullPath)
                if (-not [IO.Directory]::Exists($fullPath)) {
                    throw "CreateDirectory lieferte keinen Fehler, aber der Ordner existiert nicht: $fullPath"
                }
                [pscustomobject]@{ ComputerName = $computer; Account = $account; Directory = $fullPath }
            }
            catch {
                throw "Remote-Ordner konnte nicht erstellt/verifiziert werden auf ${computer} (${account}), Pfad '${dir}': $($_.Exception.Message)"
            }
        }
        $remoteHost = [string]$remoteInfo.ComputerName
        $remotePath = [string]$remoteInfo.Directory
        Progress 'Übertragung' ("Remote-Ordner vorhanden auf $remoteHost als $($remoteInfo.Account): $remotePath")

        # Die exakten, auf dem Remote-Host verifizierten Pfade fuer das Entpacken merken.
        $remoteZipFiles = @{ payment = ''; fiscal = '' }
        foreach ($kind in @('payment','fiscal')) {
            if (-not @($pending | Where-Object Kind -eq $kind).Count) { continue }
            $zip = if ($kind -eq 'payment') { [string]$req.PaymentZip } else { [string]$req.FiscalZip }
            $expectedHash = if ($kind -eq 'payment') { [string]$req.PaymentSha256 } else { [string]$req.FiscalSha256 }
            if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Lokale ZIP fehlt auf dem Updater-PC: $zip" }
            if ([string]::IsNullOrWhiteSpace($expectedHash)) { throw "Lokale SHA256-Pruefsumme fehlt fuer $kind" }
            $remoteFile = Join-Path $remotePath (Split-Path -Path $zip -Leaf)
            $remoteHash = Invoke-Command -Session $session -ArgumentList $remoteFile -ErrorAction Stop -ScriptBlock {
                param([string]$path)
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
                } else { '' }
            }
            if ($remoteHash -ne $expectedHash) {
                Progress 'Übertragung' ("Kopiere $kind : $zip => $remoteHost : $remoteFile")
                try {
                    Copy-Item -LiteralPath $zip -Destination $remoteFile -ToSession $session -Force -ErrorAction Stop
                } catch {
                    throw "ZIP-Transfer fehlgeschlagen ($kind; $remoteHost; $remoteFile): $($_.Exception.Message)"
                }
                $remoteHash = Invoke-Command -Session $session -ArgumentList $remoteFile -ErrorAction Stop -ScriptBlock {
                    param([string]$path)
                    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                        throw "ZIP fehlt auf $env:COMPUTERNAME nach Copy-Item: $path"
                    }
                    (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
                }
            }
            if ($remoteHash -ne $expectedHash) { throw "SHA256-Pruefung fehlgeschlagen ($remoteHost; $remoteFile)" }
            $remoteZipFiles[$kind] = [string]$remoteFile
            Progress 'Übertragung' ("ZIP verifiziert: $remoteHost : $remoteFile")
        }

        # Prepare and expand away from running services; only stop after successful extraction.
        Progress 'Vorbereitung' 'Archive werden in ein temporäres Verzeichnis entpackt'
        $kindsCsv = (@($pending | Select-Object -ExpandProperty Kind -Unique) -join ',')
        # Kein Cast direkt in -ArgumentList: Windows PowerShell wertet
        # [string]$req.PaymentZip dort sonst als Stringdarstellung von $req aus.
        # Die Werte kommen direkt aus der erfolgreichen Remote-SHA256-Pruefung.
        $paymentRemoteZip = [string]$remoteZipFiles['payment']
        $fiscalRemoteZip = [string]$remoteZipFiles['fiscal']
        $stageArgs = @($remotePath, $kindsCsv, $paymentRemoteZip, $fiscalRemoteZip)
        $stage = Invoke-Command -Session $session -ArgumentList $stageArgs -ErrorAction Stop -ScriptBlock {
            param([string]$base, [string]$kindsCsv, [string]$pZip, [string]$fZip)
            $dir = Join-Path $base ('.novatouch-updater-stage-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null
            try {
                foreach ($kind in ($kindsCsv -split ',')) {
                    $zipOnHost = if ($kind -eq 'payment') { $pZip } else { $fZip }
                    if ([string]::IsNullOrWhiteSpace($zipOnHost)) {
                        throw "Kein verifizierter Remote-ZIP-Pfad fuer $kind uebergeben."
                    }
                    $dest = Join-Path $dir $kind
                    New-Item -ItemType Directory -Path $dest -Force -ErrorAction Stop | Out-Null
                    if (-not (Test-Path -LiteralPath $zipOnHost -PathType Leaf)) {
                        throw "Remote-ZIP fehlt vor dem Entpacken auf ${env:COMPUTERNAME}: '$zipOnHost'"
                    }
                    Expand-Archive -LiteralPath $zipOnHost -DestinationPath $dest -Force -ErrorAction Stop
                    if (-not @(Get-ChildItem -LiteralPath $dest -Force).Count) { throw "ZIP ist leer: $zipOnHost" }
                }
                $dir
            }
            catch {
                Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
                throw
            }
        }

        $updateError = $null
        try {
            $pendingNames = (@($pending | ForEach-Object { [string]$_.Name }) -join ', ')
            Progress 'Dienste stoppen' ("Betroffene Dienste: " + $pendingNames)
            $wantedJson = ConvertTo-Json -InputObject @($pending) -Depth 12 -Compress
            $installResult = Invoke-Command -Session $session -ArgumentList $wantedJson, $stage -ScriptBlock {
                param($wantedJson,$stageDir)
                # PowerShell 5.1 kann ein JSON-Array als EIN verschachteltes
                # Array-Objekt zurueckgeben. Vor jedem Dienstzugriff aufloesen
                # und jeden Datensatz validieren (insb. Eigenschaft Name).
                $decoded = ConvertFrom-Json -InputObject $wantedJson -ErrorAction Stop
                $wanted = @()
                foreach ($entry in $decoded) {
                    if ($entry -is [array]) {
                        foreach ($item in $entry) { $wanted += $item }
                    } else {
                        $wanted += $entry
                    }
                }
                if ($wanted.Count -eq 0) { throw 'Keine Dienste an den Installationsschritt uebergeben.' }

                $servicesToStart = @()
                foreach ($svc in $wanted) {
                    if ($null -eq $svc) { throw 'Leerer Dienst-Datensatz beim Installieren.' }
                    foreach ($property in @('Name', 'Kind', 'InstallDir', 'StartType')) {
                        if ($null -eq $svc.PSObject.Properties[$property]) {
                            throw "Ungueltiger Dienst-Datensatz: Eigenschaft '$property' fehlt (Typ: $($svc.GetType().FullName))."
                        }
                    }
                    $name = [string]$svc.Name
                    $kind = [string]$svc.Kind
                    if ([string]::IsNullOrWhiteSpace($name) -or $kind -notin @('payment', 'fiscal') -or
                        $name -notmatch ('(?i)^nt\.' + [regex]::Escape($kind) + '(?:\.|$)')) {
                        throw "Ungueltige Dienst-Zuordnung: Name='$name', Kind='$kind'."
                    }
                    if ([string]::IsNullOrWhiteSpace([string]$svc.InstallDir)) {
                        throw "Installationsverzeichnis fuer '$name' fehlt."
                    }
                    if ([string]$svc.StartType -ne 'Disabled') { $servicesToStart += $name }
                }
                $changed = @()
                try {
                    foreach ($svc in $wanted) {
                        $service = Get-Service -Name $svc.Name -ErrorAction Stop
                        if ($service.Status -ne 'Stopped') {
                            Stop-Service -Name $svc.Name -Force -ErrorAction Stop
                            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
                        }
                    }
                    $installPaths = @($wanted | Group-Object -Property Kind,InstallDir)
                    foreach ($group in $installPaths) {
                        $svc = $group.Group[0]
                        $src = Join-Path $stageDir $svc.Kind
                        if (-not (Test-Path -LiteralPath $svc.InstallDir -PathType Container)) { throw "Installationsverzeichnis fehlt: $($svc.InstallDir)" }
                        $items = @(Get-ChildItem -LiteralPath $src -Force)
                        if ($items.Count -eq 0) { throw "Leeres Archiv für $($svc.Kind)" }
                        # Copy by relative path so ZIP structures behave exactly like Expand-Archive -DestinationPath.
                        foreach ($folder in @(Get-ChildItem -LiteralPath $src -Directory -Recurse -Force)) {
                            $relative = $folder.FullName.Substring($src.Length).TrimStart('\')
                            $destination = Join-Path $svc.InstallDir $relative
                            New-Item -ItemType Directory -Path $destination -Force -ErrorAction Stop | Out-Null
                        }
                        foreach ($file in @(Get-ChildItem -LiteralPath $src -File -Recurse -Force)) {
                            $relative = $file.FullName.Substring($src.Length).TrimStart('\')
                            $destination = Join-Path $svc.InstallDir $relative
                            $parent = Split-Path $destination -Parent
                            if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
                                New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null
                            }
                            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force -ErrorAction Stop
                        }
                        $changed += ($svc.Kind + ' => ' + $svc.InstallDir)
                    }
                }
                finally {
                    $restartErrors = @()
                    foreach ($name in $servicesToStart) {
                        try {
                            Start-Service -Name $name -ErrorAction Stop
                            (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
                        } catch { $restartErrors += "Dienst ${name} konnte nicht wieder gestartet werden: $($_.Exception.Message)" }
                    }
                    if ($restartErrors.Count -gt 0) { throw ($restartErrors -join ' | ') }
                }
                return ,$changed
            }
            Progress 'Verifikation' 'Versionen und Dienststatus werden erneut kontrolliert'
            $after = @(Invoke-Command -Session $session -ScriptBlock ${function:ReadInventory})
            $mistakes = @()
            foreach ($oldSvc in $pending) {
                $current = @($after | Where-Object Name -eq $oldSvc.Name | Select-Object -First 1)
                if ($current.Count -ne 1) { $mistakes += "Dienst verschwunden: $($oldSvc.Name)"; continue }
                if (-not (IsTargetVersion $current[0] $targets[$oldSvc.Kind])) {
                    $mistakes += "Falsche Version $($oldSvc.Name): Product=$($current[0].ProductVersion), File=$($current[0].FileVersion), erwartet $($targets[$oldSvc.Kind])"
                }
                if ($oldSvc.StartType -ne 'Disabled' -and $current[0].State -ne 'Running') {
                    $mistakes += "Dienst läuft nicht: $($oldSvc.Name)"
                }
            }
            if ($mistakes.Count -gt 0) { throw ($mistakes -join ' | ') }
            Emit @{ Event='result'; Success=$true; Message=$(if ($forceUpdate) { 'Force Update installiert und verifiziert' } else { 'Update installiert und verifiziert' }); Services=$after; Changed=$true }
        }
        catch { $updateError = $_.Exception.Message; throw }
        finally {
            Invoke-Command -Session $session -ArgumentList $stage -ScriptBlock {
                param($dir)
                Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
            } -ErrorAction SilentlyContinue | Out-Null
        }
    }
    finally { if ($null -ne $session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue } }
}
catch {
    $errorText = $_.Exception.Message
    # Bei Remote-Fehlern nach Moeglichkeit die Skriptposition mitliefern.
    if ($_.InvocationInfo -and -not [string]::IsNullOrWhiteSpace([string]$_.InvocationInfo.PositionMessage)) {
        $position = ([string]$_.InvocationInfo.PositionMessage -replace '[\r\n]+', ' ').Trim()
        $errorText += " | Position: $position"
    }
    Emit @{ Event='result'; Success=$false; Message='Fehler'; Error=$errorText; Services=@() }
    exit 1
}
