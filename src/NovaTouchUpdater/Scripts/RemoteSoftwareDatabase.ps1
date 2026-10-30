# Runs on the computer being inspected, using its two registry views and active CPF.
function ReadSoftwareIrisRegistrations {
    foreach ($view in $(if ([Environment]::Is64BitOperatingSystem) { @('Registry64','Registry32') } else { @('Registry32') })) {
        $registry = $null; $configurations = $null
        try {
            $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Enum]::Parse([Microsoft.Win32.RegistryView], $view))
            $configurations = $registry.OpenSubKey('SOFTWARE\InterSystems\IRIS\Configurations', $false)
            if ($null -eq $configurations) { continue }
            foreach ($name in $configurations.GetSubKeyNames()) {
                $key = $null
                try {
                    $key = $configurations.OpenSubKey($name, $false)
                    $values = @{}
                    foreach ($value in @('Directory','Properties','ConfigFile','Version')) {
                        $text = [string]$key.GetValue($value)
                        if (-not $text -and $value -in @('Directory','Properties')) {
                            $child = $key.OpenSubKey($value, $false)
                            try { if ($null -ne $child) { $text = [string]$child.GetValue('') } } finally { if ($null -ne $child) { $child.Dispose() } }
                        }
                        $values[$value] = $text
                    }
                    [pscustomobject]@{ Name=$name; Directory=$values.Directory; ConfigurationFile=$(if ($values.ConfigFile) { $values.ConfigFile } else { $values.Properties }); Version=$values.Version; Error='' }
                } catch { [pscustomobject]@{ Name=$name; Directory=''; ConfigurationFile=''; Version=''; Error=$_.Exception.Message } }
                finally { if ($null -ne $key) { $key.Dispose() } }
            }
        } finally { if ($null -ne $configurations) { $configurations.Dispose() }; if ($null -ne $registry) { $registry.Dispose() } }
    }
}

function ReadSoftwareIrisInventory {
    $registrations = @(ReadSoftwareIrisRegistrations)
    foreach ($group in @($registrations | Group-Object Name | Sort-Object Name)) {
        $registration = @($group.Group | Sort-Object @{Expression={ -not $_.Error };Descending=$true}, @{Expression={ [bool]$_.ConfigurationFile };Descending=$true}, @{Expression={ [bool]$_.Directory };Descending=$true})[0]
        $directory = [string]$registration.Directory; $cpf = [string]$registration.ConfigurationFile
        $port = 0; $namespaces = @(); $errorText = ''; $connectionError = ''
        try {
            if ($registration.Error) { throw $registration.Error }
            $directory = [Environment]::ExpandEnvironmentVariables($directory.Trim().Trim('"'))
            $cpf = [Environment]::ExpandEnvironmentVariables($cpf.Trim().Trim('"'))
            if (-not $directory -and [IO.Path]::IsPathRooted($cpf)) { $directory = [IO.Path]::GetDirectoryName($cpf) }
            if ($directory -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)') { throw 'Kein gültiger IRIS-Installationspfad in der Registrierung.' }
            $directory = [IO.Path]::GetFullPath($directory)
            if (-not $cpf) { $cpf = Join-Path $directory 'iris.cpf' } elseif (-not [IO.Path]::IsPathRooted($cpf)) { $cpf = Join-Path $directory $cpf }
            $cpf = [IO.Path]::GetFullPath($cpf)
            $file = [IO.File]::Open($cpf, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader = [IO.StreamReader]::new($file, [Text.Encoding]::UTF8, $true)
            $section = ''; $found = $false; $ports = 0; $badPort = $false
            try {
                while ($null -ne ($line = $reader.ReadLine())) {
                    $line = $line.Trim()
                    if (-not $line -or $line[0] -in @(';','#')) { continue }
                    if ($line -match '^\[([^\]]+)\]') { $section = $Matches[1].Trim(); if ($section -ieq 'Namespaces') { $found = $true }; continue }
                    $separator = $line.IndexOf('='); if ($separator -le 0) { continue }
                    $name = $line.Substring(0,$separator).Trim(); $value = $line.Substring($separator+1).Trim()
                    if ($section -ieq 'Namespaces' -and -not $name.StartsWith('%')) { $namespaces += $name }
                    if ($section -ieq 'Startup' -and $name -ieq 'DefaultPort') { $ports++; if (-not [int]::TryParse($value,[ref]$port) -or $port -lt 1 -or $port -gt 65535) { $badPort = $true } }
                }
            } finally { $reader.Dispose() }
            if (-not $found) { throw 'Der Abschnitt [Namespaces] fehlt in der IRIS-Konfigurationsdatei.' }
            if ($ports -ne 1 -or $badPort) { $port = 0; $connectionError = 'Kein gültiger [Startup] DefaultPort in der IRIS-Konfiguration.' }
        } catch { $errorText = $_.Exception.Message }
        [pscustomobject]@{ Name=$registration.Name; Directory=$directory; ConfigurationFile=$cpf; Version=$registration.Version; Namespaces=@($namespaces | Sort-Object -Unique); Error=$errorText; SuperServerPort=$port; ConnectionError=$connectionError }
    }
}

function ConvertSoftwareIrisTargets([object[]]$Inventory, [bool]$UseTls) {
    $ports = @{}
    foreach ($instance in $Inventory) {
        if ($instance.Error) { throw "IRIS-Instanz $($instance.Name): $($instance.Error)" }
        if (@($instance.Namespaces).Count -eq 0) { continue }
        if ([int]$instance.SuperServerPort -lt 1 -or [int]$instance.SuperServerPort -gt 65535) { throw "IRIS-Instanz $($instance.Name): $($instance.ConnectionError)" }
        if ($ports.ContainsKey([int]$instance.SuperServerPort)) { throw 'Mehrdeutige IRIS-Instanzen mit demselben SuperServer-Port auf dem Zielrechner.' }
        $ports[[int]$instance.SuperServerPort] = $true
        [pscustomobject]@{ Instance=$instance.Name; Directory=$instance.Directory; Port=[int]$instance.SuperServerPort; Namespaces=@($instance.Namespaces); User='_SYSTEM'; Password='SYS'; Ssl=$UseTls }
    }
}

function GetSoftwareDatabaseDestinations([object[]]$Targets) {
    foreach ($target in $Targets) {
        foreach ($namespace in @($target.Namespaces | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -and -not $_.StartsWith('%') } | Sort-Object -Unique)) {
            [pscustomobject]@{ Key=([string]$target.Instance + ':' + $target.Port + '/' + $namespace); Namespace=$namespace;
                Definition=[pscustomobject]@{ Server='127.0.0.1'; Port=[int]$target.Port; User=[string]$target.User; Password=[string]$target.Password; Ssl=[bool]$target.Ssl } }
        }
    }
}

function GetSoftwareDatabaseRemotePlan([object[]]$Packages, [object[]]$Targets, [string]$ProviderDirectory) {
    $applications = @{ ntc='Nov.WaWi'; pos='Nov.NT.POS'; ntv='Nov.Gutscheinverwaltung'; oman='Nov.Oman.SOL.Config' }
    $provider = InitializeSoftwareDatabaseProvider @($ProviderDirectory) @('IRIS')
    foreach ($destination in @(GetSoftwareDatabaseDestinations $Targets)) {
        $connection = $null
        try {
            Progress 'zzzSoftware-Vorprüfung' $destination.Key 0
            $connection = OpenSoftwareDatabase $provider $destination.Definition $destination.Namespace
            $exists = InvokeSoftwareObjectMethod $provider $connection $null '%ExistsId' 'tBool' @('User.zzzSoftware') '%Dictionary.CompiledClass'
            if (-not $exists) { Progress 'zzzSoftware' ($destination.Key + ' · keine zzzSoftware, übersprungen') 100; continue }
            $needed = @()
            foreach ($package in $Packages) {
                $null = GetMsiUpdateDecision @() $package
                if (-not $applications.ContainsKey([string]$package.Kind)) { throw 'Unbekanntes MSI-Produkt.' }
                $resolved = [pscustomobject]@{ Package=$package; Application=$applications[[string]$package.Kind] }
                $decision = GetSoftwareDatabaseUploadDecision $connection $resolved
                if ($decision.Upload) { $needed += [string]$package.Kind }
                else { Progress 'zzzSoftware' ($destination.Key + ' · ' + $package.ProductName + ' · vorhanden: ' + $decision.ExistingVersion + ' · gleich oder neuer, übersprungen') 100 }
            }
            [pscustomobject]@{ Key=$destination.Key; Namespace=$destination.Namespace; Definition=$destination.Definition; Needed=$needed; Error='' }
        } catch {
            [pscustomobject]@{ Key=$destination.Key; Namespace=$destination.Namespace; Definition=$destination.Definition; Needed=@(); Error=(GetSoftwareDatabaseError $_ @($destination.Definition.User,$destination.Definition.Password)) }
        } finally { if ($null -ne $connection) { $connection.Dispose() } }
    }
}

function StartSoftwareDatabaseChild {
    param([string]$Script, [string]$RequestJson, [string]$Initializer)
    . ([scriptblock]::Create('function InitializeSoftwareDatabaseStreaming {' + $Initializer + '}'))
    InitializeSoftwareDatabaseStreaming
    if ($null -ne (Get-Variable -Name NovaTouchDatabaseRelay -Scope Global -ErrorAction SilentlyContinue)) { throw 'Ein Datenbank-Upload läuft bereits in dieser Sitzung.' }
    $systemDirectory = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { 'Sysnative' } else { 'System32' }
    $executable = Join-Path $env:SystemRoot ($systemDirectory + '\WindowsPowerShell\v1.0\powershell.exe')
    $global:NovaTouchDatabaseRelay = [NovaTouch.DatabaseStreaming.ProcessRelay]::new($executable, $Script, $RequestJson)
}

function ReadSoftwareDatabaseChild {
    param([string]$Chunk = '', [bool]$Finish = $false, [bool]$Stop = $false)
    if ($Stop) { if ($null -ne (Get-Variable -Name NovaTouchDatabaseRelay -Scope Global -ErrorAction SilentlyContinue)) { $global:NovaTouchDatabaseRelay.Dispose(); Remove-Variable NovaTouchDatabaseRelay -Scope Global }; return }
    if ($Chunk) {
        try { $global:NovaTouchDatabaseRelay.Write($Chunk) }
        catch { if (-not $global:NovaTouchDatabaseRelay.HasExited) { throw }; $global:NovaTouchDatabaseRelay.Wait() }
    }
    if ($Finish) { $global:NovaTouchDatabaseRelay.CloseInput() }
    do {
        $line = $null
        while ($global:NovaTouchDatabaseRelay.TryRead([ref]$line)) { $line | ConvertFrom-Json }
        if (-not $Finish -or $global:NovaTouchDatabaseRelay.HasExited) { break }
        [Threading.Thread]::Sleep(25)
    } while ($true)
    if ($Finish) {
        $global:NovaTouchDatabaseRelay.Wait()
        $line = $null; while ($global:NovaTouchDatabaseRelay.TryRead([ref]$line)) { $line | ConvertFrom-Json }
        if ($global:NovaTouchDatabaseRelay.ExitCode -ne 0 -and $global:NovaTouchDatabaseRelay.Error) { throw 'Datenbank-Worker fehlgeschlagen: ' + $global:NovaTouchDatabaseRelay.Error }
        $global:NovaTouchDatabaseRelay.Dispose(); Remove-Variable NovaTouchDatabaseRelay -Scope Global
    }
}

function RemoveSoftwareDatabaseRemoteStage {
    param([string]$Directory, [string]$Base)
    $full = [IO.Path]::GetFullPath($Directory).TrimEnd('\'); $parent = [IO.Path]::GetFullPath($Base).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($full) -ine $parent -or [IO.Path]::GetFileName($full) -notmatch '^\.novatouch-updater-database-[a-f0-9]{32}$') { throw 'Ungültiger Datenbank-Bereinigungspfad.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop }
}

function PublishStreamedSoftwarePackages([object[]]$Packages, [object[]]$Plan, [string]$ProviderDirectory, [int]$Parallelism) {
    InitializeSoftwareDatabaseStreaming
    $provider = InitializeSoftwareDatabaseProvider @($ProviderDirectory) @('IRIS')
    $applications = @{ ntc='Nov.WaWi'; pos='Nov.NT.POS'; ntv='Nov.Gutscheinverwaltung'; oman='Nov.Oman.SOL.Config' }
    foreach ($package in $Packages) {
        $null = GetMsiUpdateDecision @() $package
        if (-not $applications.ContainsKey([string]$package.Kind) -or [long]$package.Length -le 0 -or [string]$package.Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Ungültige MSI-Stream-Metadaten.' }
    }
    $destinations = @($Plan | Where-Object { -not $_.Error -and @($_.Needed).Count -gt 0 })
    $outcomes = @($Plan | Where-Object Error | ForEach-Object { [pscustomobject]@{ Key=$_.Key; Success=$false; Error=('zzzSoftware-Vorprüfung für ' + $_.Key + ' fehlgeschlagen. ' + $_.Error) } })
    $limit = [Math]::Max(1,$Parallelism)
    for ($offset=0; $offset -lt $destinations.Count; $offset += $limit) {
        $wave = @($destinations | Select-Object -Skip $offset -First $limit)
        $resolved = @()
        foreach ($package in $Packages) {
            $keys = @($wave | Where-Object { $_.Needed -contains $package.Kind } | ForEach-Object Key)
            if ($keys.Count -eq 0) { continue }
            $source = [NovaTouch.DatabaseStreaming.Source]::new([long]$package.Length, [string[]]$keys)
            $resolved += [pscustomobject]@{ Package=$package; Application=$applications[[string]$package.Kind]; Sha256=$package.Sha256; StreamSource=$source; OnlyDestinations=$keys }
        }
        $pump = [NovaTouch.DatabaseStreaming.Pump]::new([NovaTouch.DatabaseStreaming.Source[]]@($resolved.StreamSource), [long[]]@($resolved | ForEach-Object { $_.Package.Length }), [Console]::In)
        try {
            $outcomes += @(InvokeSoftwareDatabaseNamespaceUploads $provider $wave $resolved $limit)
            $pump.Complete()
        } finally { $pump.Dispose() }
    }
    $failed = @($outcomes | Where-Object { -not $_.Success } | Sort-Object Key)
    if ($failed.Count -gt 0) { throw (($failed | ForEach-Object Error) -join ' || ') }
    $changed = @($outcomes | Where-Object Changed).Count -gt 0
    Emit @{ Event='result'; Success=$true; Changed=$changed; Skipped=(-not $changed); Message='MSI-Daten über WinRM gestreamt und in zzzSoftware geprüft/gespeichert.'; Services=@() }
}

function InvokeSoftwareDatabaseChildOperation($Session, [string]$Worker, $Request, [object[]]$Resolved = @(), [object[]]$Plan = @()) {
    $json = $Request | ConvertTo-Json -Depth 12 -Compress
    InvokeWorkerCommand -Session $Session -ScriptBlock ${function:StartSoftwareDatabaseChild} -ArgumentList $Worker, $json, ${function:InitializeSoftwareDatabaseStreaming}.ToString() -ErrorAction Stop | Out-Null
    $result = $null
    try {
        if ($Request.Action -eq 'publish-msi-stream') {
            $destinations = @($Plan | Where-Object { -not $_.Error -and @($_.Needed).Count -gt 0 })
            $limit = [Math]::Max(1,[int]$Request.DatabaseUploadParallelism)
            for ($offset=0; $offset -lt $destinations.Count; $offset += $limit) {
                $wave = @($destinations | Select-Object -Skip $offset -First $limit)
                foreach ($msi in $Resolved) {
                    if (@($wave | Where-Object { $_.Needed -contains $msi.Package.Kind }).Count -eq 0) { continue }
                    $file = [IO.File]::Open($msi.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                    try {
                        $buffer = New-Object byte[] (512 * 1024)
                        while (($read = $file.Read($buffer,0,$buffer.Length)) -gt 0) {
                            $chunk = [Convert]::ToBase64String($buffer,0,$read)
                            foreach ($event in @(InvokeWorkerCommand -Session $Session -ScriptBlock ${function:ReadSoftwareDatabaseChild} -ArgumentList $chunk -ErrorAction Stop)) {
                                if ($event.Event -eq 'progress') { Progress $event.Phase $event.Message $event.ProgressPercent }
                                elseif ($event.Event -eq 'result') { if (-not $event.Success) { throw [string]$event.Error }; $result = $event }
                            }
                        }
                    } finally { $file.Dispose() }
                }
            }
        }
        foreach ($event in @(InvokeWorkerCommand -Session $Session -ScriptBlock ${function:ReadSoftwareDatabaseChild} -ArgumentList '', $true -ErrorAction Stop)) {
            if ($event.Event -eq 'progress') { Progress $event.Phase $event.Message $event.ProgressPercent }
            elseif ($event.Event -eq 'result') { $result = $event }
        }
        if ($null -eq $result) { throw 'Keine gültige Rückmeldung des Datenbank-Workers auf dem Zielrechner.' }
        if (-not $result.Success) { throw [string]$result.Error }
        return $result
    } finally { InvokeWorkerCommand -Session $Session -ScriptBlock ${function:ReadSoftwareDatabaseChild} -ArgumentList '', $false, $true -ErrorAction SilentlyContinue | Out-Null }
}

function CopySoftwareDatabaseFile($Session, [string]$Source, [string]$Target, [string]$Hash) {
    CopyRemoteFileWithProgress $Session $Source $Target ('zzzSoftware · ' + [IO.Path]::GetFileName($Target))
    $actual = InvokeWorkerCommand -Session $Session -ScriptBlock { param($path) (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash } -ArgumentList $Target -ErrorAction Stop
    if ($actual -ine $Hash) { throw 'Prüfsumme der Datenbank-Uploaddatei stimmt nach der Übertragung nicht.' }
}

function PublishRemoteSoftwarePackages($Request, $Session, [string]$WorkerPath) {
    $inventoryScript = 'function ReadSoftwareIrisRegistrations {' + ${function:ReadSoftwareIrisRegistrations}.ToString() + '}; function ReadSoftwareIrisInventory {' + ${function:ReadSoftwareIrisInventory}.ToString() + '}; ReadSoftwareIrisInventory'
    $inventory = @(InvokeWorkerCommand -Session $Session -ScriptBlock ([scriptblock]::Create($inventoryScript)) -ErrorAction Stop)
    $targets = @(ConvertSoftwareIrisTargets $inventory ([bool]$Request.IrisUseTls))
    if ($targets.Count -eq 0) { return @{ Event='result'; Success=$true; Changed=$false; Skipped=$true; Message='Keine IRIS-Namespaces auf dem Zielrechner; kein Datenbank-Upload erforderlich.'; Services=@() } }
    $stageBase = InvokeWorkerCommand -Session $Session -ScriptBlock { Join-Path ([IO.Path]::GetTempPath()) 'NovaTouchUpdater' } -ErrorAction Stop
    $stage = Join-Path $stageBase ('.novatouch-updater-database-' + [guid]::NewGuid().ToString('N'))
    $localBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'NovaTouchUpdater'))
    $localWork = Join-Path $localBase ('database-msi-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($localWork)
    try {
        InvokeWorkerCommand -Session $Session -ScriptBlock { param($path) [void][IO.Directory]::CreateDirectory($path) } -ArgumentList $stage -ErrorAction Stop | Out-Null
        $remoteWorker = Join-Path $stage 'Worker.ps1'
        foreach ($file in @(
            [pscustomobject]@{ Source=$WorkerPath; Name='Worker.ps1' },
            [pscustomobject]@{ Source=(Join-Path $Request.DatabaseProviderDirectory 'InterSystems.Data.IRISClient.dll'); Name='InterSystems.Data.IRISClient.dll' },
            [pscustomobject]@{ Source=(Join-Path $Request.DatabaseProviderDirectory 'InterSystems.Data.Bindings.dll'); Name='InterSystems.Data.Bindings.dll' },
            [pscustomobject]@{ Source=(Join-Path $Request.DatabaseProviderDirectory 'IRISClient.LICENSE.txt'); Name='IRISClient.LICENSE.txt' },
            [pscustomobject]@{ Source=(Join-Path $Request.DatabaseProviderDirectory 'Bindings.LICENSE.txt'); Name='Bindings.LICENSE.txt' }
        )) { CopySoftwareDatabaseFile $Session $file.Source (Join-Path $stage $file.Name) (Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash }
        $parallelism = [int]$Request.DatabaseUploadParallelism
        $planResult = InvokeSoftwareDatabaseChildOperation $Session $remoteWorker @{ Action='database-upload-plan'; LocalExecution=$true; MsiPackages=@($Request.MsiPackages); DatabaseTargets=$targets; DatabaseProviderDirectory=$stage }
        $plan = @($planResult.DatabasePlan)
        if ($plan.Count -eq 0) { throw 'Keine zzzSoftware in den konfigurierten IRIS-Namespaces auf dem Zielrechner gefunden. Keine Installation gestartet.' }
        $errors = @($plan | Where-Object Error)
        $needed = @($plan | ForEach-Object Needed | Sort-Object -Unique)
        if ($needed.Count -eq 0) {
            if ($errors.Count -gt 0) { throw (($errors | ForEach-Object { 'zzzSoftware-Vorprüfung für ' + $_.Key + ' fehlgeschlagen. ' + $_.Error }) -join ' || ') }
            return @{ Event='result'; Success=$true; Changed=$false; Skipped=$true; Message='zzzSoftware: kein Upload erforderlich; vorhandene Pakete sind gleich/neuer oder Namespace enthält keine zzzSoftware.'; Services=@() }
        }
        $packages = @($Request.MsiPackages | Where-Object { $needed -contains $_.Kind })
        if ([bool]$Request.SkipLocalPackageCopy) {
            $resolved = @($packages | ForEach-Object { ResolveSoftwareMsi $_ $localWork })
            $streams = @($resolved | ForEach-Object {
                $package = $_.Package | ConvertTo-Json -Depth 8 -Compress | ConvertFrom-Json
                $package.Sha256 = $_.Sha256
                $package | Add-Member -NotePropertyName Length -NotePropertyValue ([IO.FileInfo]::new($_.Path).Length) -Force
                $package
            })
            return InvokeSoftwareDatabaseChildOperation $Session $remoteWorker @{ Action='publish-msi-stream'; LocalExecution=$true; MsiPackages=$streams; DatabasePlan=$plan; DatabaseProviderDirectory=$stage; DatabaseUploadParallelism=$parallelism } $resolved $plan
        }
        $remotePackages = @()
        if (-not [bool]$Request.SkipZipTransfer) {
            InvokeWorkerCommand -Session $Session -ScriptBlock { param($path) [void][IO.Directory]::CreateDirectory($path) } -ArgumentList $Request.RemoteInstallPath -ErrorAction Stop | Out-Null
        }
        foreach ($original in $packages) {
            $package = $original | ConvertTo-Json -Depth 8 -Compress | ConvertFrom-Json
            if ([bool]$Request.SkipZipTransfer) { $package.PayloadPath = Join-Path $Request.RemoteInstallPath ([IO.Path]::GetFileName($original.PayloadPath)) }
            else {
                $msi = ResolveSoftwareMsi $original $localWork
                $package.PayloadPath = Join-Path $Request.RemoteInstallPath ($package.Kind + '-' + $package.ProductVersion + '.msi')
                $package.Sha256 = $msi.Sha256; $package.RelativeMsiPath = ''
                CopySoftwareDatabaseFile $Session $msi.Path $package.PayloadPath $msi.Sha256
            }
            $remotePackages += $package
        }
        $outcome = InvokeSoftwareDatabaseChildOperation $Session $remoteWorker @{ Action='publish-msi-packages'; LocalExecution=$true; MsiPackages=$remotePackages; DatabaseTargets=$targets; DatabaseProviderDirectory=$stage; DatabaseUploadParallelism=$parallelism }
        if ($errors.Count -gt 0) { throw (($errors | ForEach-Object { 'zzzSoftware-Vorprüfung für ' + $_.Key + ' fehlgeschlagen. ' + $_.Error }) -join ' || ') }
        return $outcome
    } finally {
        InvokeWorkerCommand -Session $Session -ScriptBlock ${function:RemoveSoftwareDatabaseRemoteStage} -ArgumentList $stage, $stageBase -ErrorAction SilentlyContinue | Out-Null
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($localWork)) -ieq $localBase -and [IO.Path]::GetFileName($localWork) -match '^database-msi-[a-f0-9]{32}$') { Remove-Item -LiteralPath $localWork -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
