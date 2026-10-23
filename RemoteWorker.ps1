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
    function SuiteDirectory([string]$exe) {
        # The registered service EXE is authoritative. Do not infer an instance
        # from command-line arguments or search other installations on disk.
        if ($exe -notmatch '^[A-Za-z]:\\') { throw "Kein absoluter Windows-Dienstpfad: $exe" }
        $cursor = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($exe))
        while ($cursor) {
            if ([IO.Path]::GetFileName($cursor.TrimEnd('\')) -ieq 'portable_python') {
                return [IO.Path]::Combine([IO.Path]::GetDirectoryName($cursor), 'apps\nt3-server-suite')
            }
            $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        }
        throw "Kein Ordner portable_python im registrierten Dienst-EXE-Pfad: $exe"
    }
    $services = @(Get-Service -ErrorAction Stop | Where-Object {
        $_.Name -match '^nt\.(payment|fiscal)' -or $_.Name -match '^nt3_server_suite'
    })
    $inventory = @()
    foreach ($svc in $services) {
        $kind = if ($svc.Name -match '^nt\.payment') { 'payment' }
            elseif ($svc.Name -match '^nt\.fiscal') { 'fiscal' } else { 'serversuite' }
        $imagePath = [Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty -LiteralPath ("HKLM:\SYSTEM\CurrentControlSet\Services\" + $svc.Name) -Name ImagePath -ErrorAction Stop).ImagePath)
        if ($imagePath -match '^\s*"([^"]+?\.exe)"') { $exe = $Matches[1] }
        elseif ($imagePath -match '^\s*(.+?\.exe)(?:\s|$)') { $exe = $Matches[1] }
        else { throw "Executable-Pfad für $($svc.Name) nicht lesbar: $imagePath" }
        $installDir = [IO.Path]::GetDirectoryName($exe)
        if ($kind -eq 'serversuite') {
            $installDir = SuiteDirectory $exe
            $versionFile = [IO.Path]::Combine($installDir, 'version.txt')
            try {
                if (Test-Path -LiteralPath $versionFile -PathType Leaf -ErrorAction Stop) {
                    $text = [string](Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8 -ErrorAction Stop)
                    $productVersion = [regex]::Match($text, '\d+\.\d+\.\d+(?:\.\d+)?').Value
                    if (-not $productVersion) { throw 'Keine gültige Server-Suite-Version enthalten.' }
                } else {
                    # A registered service can outlive its files. Keep it eligible
                    # for an update that creates the missing target directories.
                    $productVersion = 'Version unbekannt'
                }
            }
            catch { throw "Server-Suite-Version für '$($svc.Name)' aus '$versionFile' nicht lesbar: $($_.Exception.Message)" }
            # Never use the Python/service executable's version as a fallback.
            $fileVersion = ''
        } else {
            $version = (Get-Item -LiteralPath $exe -ErrorAction Stop).VersionInfo
            $productVersion = [string]$version.ProductVersion
            $fileVersion = [string]$version.FileVersion
        }
        $inventory += [pscustomobject]@{
            Name = $svc.Name
            Kind = $kind
            State = [string]$svc.Status
            StartType = [string]$svc.StartType
            ExePath = $exe
            InstallDir = $installDir
            ProductVersion = $productVersion
            FileVersion = $fileVersion
        }
    }
    return $inventory
}

function ReadSoftwareInventory {
    $release = 0L
    foreach ($path in @(
        'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full'
    )) {
        if (-not (Test-Path -LiteralPath $path -ErrorAction Stop)) { continue }
        $framework = Get-ItemProperty -LiteralPath $path -ErrorAction Stop
        if ($null -ne $framework.PSObject.Properties['Release']) {
            $release = [Math]::Max($release, [long]$framework.Release)
        }
    }

    $posFound = $false
    $posVersions = @()
    foreach ($path in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $path -ErrorAction Stop)) { continue }
        foreach ($key in Get-ChildItem -LiteralPath $path -ErrorAction Stop) {
            $program = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
            if ($null -eq $program.PSObject.Properties['DisplayName'] -or
                [string]$program.DisplayName -notmatch '^\s*NovaTouch\s+POS\s*$') { continue }
            $posFound = $true
            if ($null -ne $program.PSObject.Properties['DisplayVersion'] -and
                -not [string]::IsNullOrWhiteSpace([string]$program.DisplayVersion)) {
                $posVersions += ([string]$program.DisplayVersion).Trim()
            }
        }
    }

    # Release >= 528040 means .NET Framework 4.8 or later, including 4.8.1.
    # Reading uninstall entries avoids Win32_Product and its MSI repair side effects.
    [pscustomobject]@{
        DotNet48Installed = ($release -ge 528040)
        PosVersion = if ($posVersions.Count -gt 0) { ($posVersions | Sort-Object -Unique) -join ', ' }
            elseif ($posFound) { 'Version unbekannt' } else { 'Nicht installiert' }
    }
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
    # Suite ProductVersion comes exclusively from version.txt; FileVersion stays
    # empty. Payment/fiscal still use EXE ProductVersion, then FileVersion.
    $installed = if (-not [string]::IsNullOrWhiteSpace([string]$svc.ProductVersion)) {
        [string]$svc.ProductVersion
    } else {
        [string]$svc.FileVersion
    }
    $installedVersion = Normalize-Version $installed
    if ($svc.Kind -eq 'serversuite' -and $target -match '^\d+\.\d+\.\d+$') {
        return ($installedVersion -match ('^' + [regex]::Escape($target) + '(?:\.\d+)?$'))
    }
    return ($target -eq $installedVersion)
}

function SelectUpdateServices($services, $targets, [bool]$forceUpdate) {
    $pending = @()
    foreach ($svc in $services) {
        if (-not $targets.ContainsKey($svc.Kind)) { continue }
        if (-not $forceUpdate -and (IsTargetVersion $svc $targets[$svc.Kind])) { continue }
        $pending += $svc
    }
    # An instance already on target may share its directory with an older one.
    # Stop and verify ALL suite services whenever any suite instance needs update.
    if (@($pending | Where-Object Kind -eq 'serversuite').Count -gt 0) {
        $pending = @($pending | Where-Object Kind -ne 'serversuite') + @($services | Where-Object Kind -eq 'serversuite')
    }
    $pending
}

$software = $null
try {
    $requestLine = [Console]::In.ReadLine()
    if ([string]::IsNullOrWhiteSpace($requestLine)) { throw 'Keine Anforderung erhalten.' }
    $req = $requestLine | ConvertFrom-Json
    $sessionOptions = New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 180000
    $sessionArgs = @{
        ComputerName = [string]$req.Host
        Port = 5985
        SessionOption = $sessionOptions
        ErrorAction = 'Stop'
    }
    if ([bool]$req.UseSsl) {
        $httpsPort = 5986
        if ($null -ne $req.PSObject.Properties['HttpsPort']) { $httpsPort = [int]$req.HttpsPort }
        if ($httpsPort -lt 1 -or $httpsPort -gt 65535) { throw 'HTTPS-Port muss zwischen 1 und 65535 liegen.' }
        $sessionArgs.UseSSL = $true
        $sessionArgs.Port = $httpsPort
    }
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
        if ($req.Action -eq 'transfer') {
            $remotePath = [string]$req.RemoteInstallPath
            if ([string]::IsNullOrWhiteSpace($remotePath)) { throw 'RemoteInstallPath wurde nicht übergeben.' }
            $remoteInfo = Invoke-Command -Session $session -ArgumentList $remotePath -ErrorAction Stop -ScriptBlock {
                param([string]$dir)
                $computer = [Environment]::MachineName
                $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
                try {
                    if ($dir -notmatch '^[A-Za-z]:\\') { throw "Ungueltiger absoluter Windows-Zielpfad: $dir" }
                    $fullPath = [IO.Path]::GetFullPath($dir)
                    $driveRoot = [IO.Path]::GetPathRoot($fullPath)
                    if (-not [IO.Directory]::Exists($driveRoot)) { throw "Laufwerk nicht verfuegbar: $driveRoot" }
                    [void][IO.Directory]::CreateDirectory($fullPath)
                    if (-not [IO.Directory]::Exists($fullPath)) { throw "Zielordner konnte nicht erstellt werden: $fullPath" }
                    [pscustomobject]@{ ComputerName = $computer; Account = $account; Directory = $fullPath }
                }
                catch {
                    throw "Remote-Zielordner konnte nicht erstellt/verifiziert werden auf ${computer} (${account}), Pfad '${dir}': $($_.Exception.Message)"
                }
            }
            $remoteHost = [string]$remoteInfo.ComputerName
            $remotePath = [string]$remoteInfo.Directory
            $files = @($req.TransferFiles)
            if ($files.Count -eq 0) { throw 'Keine Dateien zur Übertragung angegeben.' }
            $copied = 0
            foreach ($file in $files) {
                $localFile = [string]$file.LocalPath
                $expectedHash = [string]$file.Sha256
                if (-not (Test-Path -LiteralPath $localFile -PathType Leaf)) { throw "Lokale Datei fehlt auf dem Updater-PC: $localFile" }
                if ([string]::IsNullOrWhiteSpace($expectedHash)) { throw "SHA256-Pruefsumme fehlt fuer $localFile" }
                $remoteFile = Join-Path $remotePath (Split-Path -Path $localFile -Leaf)
                $remoteHash = Invoke-Command -Session $session -ArgumentList $remoteFile -ErrorAction Stop -ScriptBlock {
                    param([string]$path)
                    if (Test-Path -LiteralPath $path -PathType Leaf) {
                        (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
                    } else { '' }
                }
                if ($remoteHash -ne $expectedHash) {
                    Progress 'Übertragung' ("Kopiere $(Split-Path -Path $localFile -Leaf) nach $remoteHost : $remoteFile")
                    try {
                        Copy-Item -LiteralPath $localFile -Destination $remoteFile -ToSession $session -Force -ErrorAction Stop
                    }
                    catch {
                        throw "Dateiübertragung fehlgeschlagen ($remoteHost; $remoteFile): $($_.Exception.Message)"
                    }
                    $remoteHash = Invoke-Command -Session $session -ArgumentList $remoteFile -ErrorAction Stop -ScriptBlock {
                        param([string]$path)
                        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Datei fehlt nach der Übertragung: $path" }
                        (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
                    }
                    if ($remoteHash -ne $expectedHash) { throw "SHA256-Pruefung nach der Übertragung fehlgeschlagen ($remoteHost; $remoteFile)" }
                    $copied++
                }
                Progress 'Übertragung' ("Datei verifiziert: $remoteHost : $remoteFile")
            }
            Emit @{ Event='result'; Success=$true; Message="$($files.Count) Datei(en) geprüft; $copied übertragen; keine Installation ausgeführt."; Changed=($copied -gt 0); Services=@() }
            exit 0
        }

        if ($req.Action -eq 'scan') {
            Progress 'Softwareprüfung' '.NET Framework 4.8 und NovaTouch POS werden geprüft'
            $software = Invoke-Command -Session $session -ScriptBlock ${function:ReadSoftwareInventory} -ErrorAction Stop
        }
        Progress 'Versionen' 'Dienste und installierte Versionen werden ausgelesen'
        $services = @(Invoke-Command -Session $session -ScriptBlock ${function:ReadInventory} -ErrorAction Stop)
        if ($req.Action -eq 'scan') {
            Emit @{ Event='result'; Success=$true; Message='Versionen und Software geprüft'; Services=$services;
                DotNet48Installed=$software.DotNet48Installed; PosVersion=$software.PosVersion }
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
        if ($null -ne $req.PSObject.Properties['ServerSuiteZip'] -and -not [string]::IsNullOrWhiteSpace([string]$req.ServerSuiteZip)) {
            if ([string]::IsNullOrWhiteSpace([string]$req.ServerSuiteVersion)) { throw 'Server-Suite-ZIP ausgewählt, aber Zielversion fehlt.' }
            $targets.serversuite = [string]$req.ServerSuiteVersion
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
        $pending = @(SelectUpdateServices $services $targets $forceUpdate)
        $suiteInstances = @($pending | Where-Object Kind -eq 'serversuite')
        if ($suiteInstances.Count -gt 0) {
            Progress 'Server-Suite-Instanzen' ("Alle $($suiteInstances.Count) erkannten Instanzen werden berücksichtigt.")
            foreach ($instance in $suiteInstances) {
                Progress 'Instanz vorgesehen' ("$($instance.Name): Dienst-EXE=$($instance.ExePath); Suite=$($instance.InstallDir)")
            }
        }
        if ($pending.Count -eq 0) {
            $noneInstalled = @($services | Where-Object { $targets.ContainsKey($_.Kind) }).Count -eq 0
            Emit @{ Event='result'; Success=$true; Message=$(if ($noneInstalled) { 'Keine ausgewählten Dienste installiert - übersprungen' } else { 'Bereits auf Zielversion' }); Services=$services; Changed=$false; Skipped=$noneInstalled }
            exit 0
        }

        $skipZipTransfer = [bool]$req.SkipZipTransfer
        if ($skipZipTransfer) {
            Progress 'ZIP-Prüfung' 'Vorhandene ZIP-Dateien auf dem Host werden geprüft; Übertragung ist deaktiviert'
        } else {
            Progress 'Übertragung' 'ZIP-Dateien werden auf den Host übertragen und geprüft'
        }
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
        $remoteZipFiles = @{ payment = ''; fiscal = ''; serversuite = '' }
        foreach ($kind in @('payment','fiscal','serversuite')) {
            if (-not @($pending | Where-Object Kind -eq $kind).Count) { continue }
            $zip = if ($kind -eq 'payment') { [string]$req.PaymentZip } elseif ($kind -eq 'fiscal') { [string]$req.FiscalZip } else { [string]$req.ServerSuiteZip }
            $expectedHash = if ($kind -eq 'payment') { [string]$req.PaymentSha256 } elseif ($kind -eq 'fiscal') { [string]$req.FiscalSha256 } else { [string]$req.ServerSuiteSha256 }
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
                if ($skipZipTransfer) {
                    throw "ZIP-Übertragung deaktiviert: '$remoteFile' fehlt auf $remoteHost oder stimmt nicht mit der ausgewählten lokalen ZIP überein (SHA256)."
                }
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
            $verifiedMessage = if ($skipZipTransfer) { "Vorhandene ZIP verifiziert: $remoteHost : $remoteFile" } else { "ZIP verifiziert: $remoteHost : $remoteFile" }
            Progress 'ZIP-Prüfung' $verifiedMessage
        }

        # Prepare and expand away from running services; only stop after successful extraction.
        Progress 'Vorbereitung' 'Archive werden in ein temporäres Verzeichnis entpackt'
        $kindsCsv = (@($pending | Select-Object -ExpandProperty Kind -Unique) -join ',')
        # Kein Cast direkt in -ArgumentList: Windows PowerShell wertet
        # [string]$req.PaymentZip dort sonst als Stringdarstellung von $req aus.
        # Die Werte kommen direkt aus der erfolgreichen Remote-SHA256-Pruefung.
        $paymentRemoteZip = [string]$remoteZipFiles['payment']
        $fiscalRemoteZip = [string]$remoteZipFiles['fiscal']
        $serverSuiteRemoteZip = [string]$remoteZipFiles['serversuite']
        $stageArgs = @($remotePath, $kindsCsv, $paymentRemoteZip, $fiscalRemoteZip, $serverSuiteRemoteZip)
        $stage = Invoke-Command -Session $session -ArgumentList $stageArgs -ErrorAction Stop -ScriptBlock ${function:PrepareUpdatePackages}

        $updateError = $null
        try {
            $pendingNames = (@($pending | ForEach-Object { [string]$_.Name }) -join ', ')
            Progress 'Update vorbereiten' ("Prüfe Dateien und Installationsziele für: " + $pendingNames)
            $wantedJson = ConvertTo-Json -InputObject @($pending) -Depth 12 -Compress
            $suiteTarget = if ($targets.ContainsKey('serversuite')) { [string]$targets.serversuite } else { '' }
            Invoke-Command -Session $session -ArgumentList $wantedJson, $stage, $suiteTarget -ErrorAction Stop -ScriptBlock ${function:InstallUpdatePackages} |
                ForEach-Object {
                    if ($null -ne $_ -and $null -ne $_.PSObject.Properties['Event'] -and $_.Event -eq 'progress') {
                        Progress ([string]$_.Phase) ([string]$_.Message)
                    }
                }
            Progress 'Verifikation' 'Versionen und Dienststatus werden erneut kontrolliert'
            $after = @(Invoke-Command -Session $session -ScriptBlock ${function:ReadInventory} -ErrorAction Stop)
            $mistakes = @()
            foreach ($oldSvc in $pending) {
                $previousErrors = $mistakes.Count
                $current = @($after | Where-Object Name -eq $oldSvc.Name | Select-Object -First 1)
                if ($current.Count -ne 1) { $mistakes += "Dienst verschwunden: $($oldSvc.Name)"; continue }
                if (-not (IsTargetVersion $current[0] $targets[$oldSvc.Kind])) {
                    if ($oldSvc.Kind -eq 'serversuite') {
                        $mistakes += "Falsche Version $($oldSvc.Name): version.txt=$($current[0].ProductVersion), erwartet $($targets[$oldSvc.Kind])"
                    } else {
                        $mistakes += "Falsche Version $($oldSvc.Name): Product=$($current[0].ProductVersion), File=$($current[0].FileVersion), erwartet $($targets[$oldSvc.Kind])"
                    }
                }
                if ($oldSvc.StartType -ne 'Disabled' -and $current[0].State -ne 'Running') {
                    $mistakes += "Dienst läuft nicht: $($oldSvc.Name)"
                }
                if ($oldSvc.Kind -eq 'serversuite' -and $mistakes.Count -eq $previousErrors) {
                    Progress 'Instanz geprüft' ("$($oldSvc.Name): version.txt=$($current[0].ProductVersion); Status=$($current[0].State); Suite=$($current[0].InstallDir)")
                }
            }
            if ($mistakes.Count -gt 0) { throw ($mistakes -join ' | ') }
            Emit @{ Event='result'; Success=$true; Message=$(if ($forceUpdate) { 'Force Update installiert und verifiziert' } else { 'Update installiert und verifiziert' }); Services=$after; Changed=$true }
        }
        catch { $updateError = $_.Exception.Message; throw }
        finally {
            Invoke-Command -Session $session -ArgumentList $stage, $remotePath -ScriptBlock ${function:RemoveUpdateStage} -ErrorAction SilentlyContinue | Out-Null
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
    $result = @{ Event='result'; Success=$false; Message='Fehler'; Error=$errorText; Services=@() }
    if ($null -ne $software) {
        $result.DotNet48Installed = $software.DotNet48Installed
        $result.PosVersion = $software.PosVersion
    }
    Emit $result
    exit 1
}
