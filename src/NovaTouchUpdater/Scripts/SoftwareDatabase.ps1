# The on-disk contract follows AP.Sys.XML.Einstellungen / DB.SQL.Verbindung.
# The upload contract follows AP.Sys.Update.InstallMSI.ImportSetupFileSoftware:
# User.zzzSoftware, Application=<product>, Ordner=Setup, Pfad=0, Dateiname=MSI.
# No application assemblies or database credentials are bundled in the updater.

function ReadSoftwareSettingValue($Node, [string]$Name) {
    $child = @($Node.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.LocalName -ieq $Name })
    if ($child.Count -gt 1) { throw "Mehrdeutiger Datenbank-Eintrag: $Name" }
    if ($child.Count -eq 1) { return $child[0].InnerText.Trim() }
    return ''
}

function DecodeSoftwareCredential([string]$Text) {
    # Compatibility with AP.Sys.Security.Cryptography.DeCrypt (*User2/*Passwort2).
    if ($Text -notmatch '^(?:[0-9a-fA-F]{32})+$') { return $Text }
    $aes = [Security.Cryptography.Aes]::Create()
    $decryptor = $null
    try {
        $aes.Key = [Text.Encoding]::UTF8.GetBytes('ViBaGeneralRijndaelKeyA!B$%OP98#')
        $aes.IV = [Text.Encoding]::UTF8.GetBytes('VectViBaRijndael')
        $bytes = New-Object byte[] ($Text.Length / 2)
        for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($Text.Substring($i * 2, 2), 16) }
        $decryptor = $aes.CreateDecryptor()
        return [Text.Encoding]::UTF8.GetString($decryptor.TransformFinalBlock($bytes, 0, $bytes.Length)).TrimEnd([char]0)
    }
    catch { return $Text } # The application also accepts unencrypted legacy values.
    finally { if ($null -ne $decryptor) { $decryptor.Dispose() }; $aes.Dispose() }
}

function ReadSoftwareCredential($Node, [string]$SqlName, [string]$DatabaseName) {
    foreach ($name in @(($SqlName + '2'), $SqlName, ($DatabaseName + '2'), $DatabaseName)) {
        $value = ReadSoftwareSettingValue $Node $name
        if ($value) { return DecodeSoftwareCredential $value }
    }
    return ''
}

function ReadSoftwareDsn([string]$Dsn) {
    $values = @{}
    if (-not $Dsn) { return $values }
    if ($Dsn.Contains('=')) {
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        try {
            $builder.set_ConnectionString($Dsn)
            foreach ($key in $builder.Keys) { $values[[string]$key] = [string]$builder[$key] }
        } catch { throw 'SQLDSN enthält keine gültige Verbindungsdefinition.' }
    } else {
        # Read both ODBC views: an installed NTC/POS may be a 32-bit application.
        foreach ($hive in @('HKCU:', 'HKLM:')) {
            foreach ($view in @('SOFTWARE\ODBC\ODBC.INI', 'SOFTWARE\WOW6432Node\ODBC\ODBC.INI')) {
                $path = "$hive\$view\$Dsn"
                if (-not (Test-Path -LiteralPath $path)) { continue }
                $entry = Get-ItemProperty -LiteralPath $path
                foreach ($name in @('Server','Port','Namespace','Database','UID','PWD')) {
                    if ($null -ne $entry.PSObject.Properties[$name] -and -not $values.ContainsKey($name)) { $values[$name] = [string]$entry.$name }
                }
            }
        }
    }
    return $values
}

function ReadSoftwareXmlConnections([string[]]$Paths) {
    $seen = @{}
    foreach ($path in $Paths) {
        $reader = $null
        try {
            $settings = New-Object Xml.XmlReaderSettings
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($path, $settings)
            $doc = New-Object Xml.XmlDocument
            $doc.XmlResolver = $null
            $doc.Load($reader)
            $nodes = @($doc.SelectNodes('//*') | Where-Object { $_.LocalName -match '^C\d+$' -and $_.ParentNode.LocalName -ieq 'DB' })
            foreach ($node in $nodes) {
                $dsn = ReadSoftwareSettingValue $node 'SQLDSN'
                $dsnValues = ReadSoftwareDsn $dsn
                $server = ReadSoftwareSettingValue $node 'Server'
                if (-not $server) { $server = [string]$dsnValues['Server'] }
                if (-not $server) { continue } # Ignore empty, unused connection slots.
                $portText = ReadSoftwareSettingValue $node 'DatabasePort'
                if (-not $portText) { $portText = [string]$dsnValues['Port'] }
                if (-not $portText) { $portText = '1972' }
                $port = 0
                if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) { throw 'Ungültiger DatabasePort.' }
                $namespace = ReadSoftwareSettingValue $node 'Database'
                if (-not $namespace) { $namespace = [string]$dsnValues['Namespace'] }
                if (-not $namespace) { $namespace = [string]$dsnValues['Database'] }
                if (-not $namespace) { $namespace = 'USER' }
                $user = ReadSoftwareCredential $node 'SQLUser' 'DatabaseUser'
                $password = ReadSoftwareCredential $node 'SQLPasswort' 'DatabasePasswort'
                if (-not $user) { $user = [string]$dsnValues['UID'] }
                if (-not $password) { $password = [string]$dsnValues['PWD'] }
                if (-not $user -or -not $password) { throw 'SQLUser/SQLPasswort fehlen in einer konfigurierten Verbindung.' }
                $ssl = (ReadSoftwareSettingValue $node 'SSL') -match '^(true|1|ja)$'
                # Keep distinct accounts: one account may not have access to every namespace.
                $key = (@($server.ToLowerInvariant(), $port, $namespace.ToUpperInvariant(), $user, $password, $ssl) | ConvertTo-Json -Compress)
                if ($seen.ContainsKey($key)) { continue }
                $seen[$key] = $true
                [pscustomobject]@{ Server=$server; Port=$port; Namespace=$namespace; User=$user; Password=$password; Ssl=$ssl }
            }
        }
        catch { throw "Verbindungen aus '$path' konnten nicht sicher gelesen werden. Bitte DB-Einträge, SQL-Zugangsdaten und XML prüfen." }
        finally { if ($null -ne $reader) { $reader.Dispose() } }
    }
}

function ReadSoftwareIniConnection([string]$Path) {
    $values = @{}; $section = ''
    # StreamReader detects UTF-8/UTF-16 BOMs; legacy POS files may use ANSI.
    foreach ($raw in [IO.File]::ReadAllLines($Path, [Text.Encoding]::Default)) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith(';') -or $line.StartsWith('#')) { continue }
        if ($line -match '^\[([^\]]+)\]\s*(?:[;#].*)?$') { $section = $Matches[1].Trim(); continue }
        if ($section -ine 'NOVACOM') { continue }
        $separator = $line.IndexOf('=')
        if ($separator -le 0) { continue }
        $key = $line.Substring(0, $separator).Trim()
        if ($key -notin @('SYSTEM','SERVER','USER','PW','SSL')) { continue }
        if ($values.ContainsKey($key)) { throw 'Mehrdeutiger NOVACOM-Eintrag.' }
        $values[$key] = $line.Substring($separator + 1).Trim()
    }
    if ($values['SYSTEM'] -and $values['SYSTEM'] -notin @('CACHE','IRIS')) { throw 'Kein unterstütztes NOVACOM-Datenbanksystem.' }
    if ([string]$values['SERVER'] -notmatch '^cn_iptcp:(?<server>.+)\[(?<port>\d+)\]:(?<namespace>[^:\s]+)$') { throw 'Ungültiger NOVACOM-SERVER.' }
    $server = $Matches['server'].Trim(); $namespace = $Matches['namespace']; $port = 0
    if (-not [int]::TryParse($Matches['port'], [ref]$port) -or $port -lt 1 -or $port -gt 65535 -or -not $server) { throw 'Ungültiger NOVACOM-Port oder Server.' }
    $user = DecodeSoftwareCredential ([string]$values['USER'])
    $password = DecodeSoftwareCredential ([string]$values['PW'])
    if (-not $user -or -not $password) { throw 'NOVACOM-USER/PW fehlen.' }
    if ($values['SSL'] -and $values['SSL'] -notmatch '^(0|1|true|false)$') { throw 'Ungültiger NOVACOM-SSL-Wert.' }
    [pscustomobject]@{ Server=$server; Port=$port; Namespace=$namespace; User=$user; Password=$password; Ssl=($values['SSL'] -match '^(1|true)$') }
}

function ReadSoftwareConnections([string[]]$Paths) {
    $seen = @{}
    foreach ($path in $Paths) {
        try {
            $connections = if ([IO.Path]::GetExtension($path) -ieq '.ini') { @(ReadSoftwareIniConnection $path) }
                else { @(ReadSoftwareXmlConnections @($path)) }
            foreach ($connection in $connections) {
                $key = (@($connection.Server.ToLowerInvariant(), $connection.Port, $connection.Namespace.ToUpperInvariant(), $connection.User, $connection.Password, $connection.Ssl) | ConvertTo-Json -Compress)
                if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $connection }
            }
        } catch { throw "Datenbankverbindungen aus '$path' konnten nicht sicher gelesen werden. XML-DB-Einträge bzw. INI-Abschnitt [NOVACOM] mit SERVER, USER, PW und SSL prüfen." }
    }
}

function FindSoftwareDatabaseConfiguration {
    $names = @{ 'novacom ntcontrol updateservice'='ntc'; 'novacom pos updateservice'='pos' }
    $filter = "Name='novacom ntcontrol updateservice' OR Name='novacom pos updateservice' OR DisplayName='novacom ntcontrol updateservice' OR DisplayName='novacom pos updateservice'"
    # Local CIM uses COM, without WinRM. Do not filter on running/start state.
    $services = @(Get-CimInstance -ClassName Win32_Service -Filter $filter -ErrorAction Stop)
    $directories = @{}; $paths = @{}; $checkedPaths = @{}; $sources = @()
    foreach ($service in $services) {
        $name = [string]$service.Name
        $display = if ($null -ne $service.PSObject.Properties['DisplayName']) { [string]$service.DisplayName } else { '' }
        $kind = if ($names.ContainsKey($name)) { $names[$name] } elseif ($names.ContainsKey($display)) { $names[$display] } else { continue }
        $command = [Environment]::ExpandEnvironmentVariables([string]$service.PathName)
        if ($command -match '^\s*"([^"\r\n]+\.exe)"(?:\s|$)' -or $command -match '^\s*([^"\r\n]+?\.exe)(?:\s|$)') { $exe = $Matches[1] }
        else { throw "EXE-Pfad des Update-Dienstes '$name' ist nicht lesbar." }
        if (-not [IO.Path]::IsPathRooted($exe)) { throw "EXE-Pfad des Update-Dienstes '$name' ist nicht absolut." }
        $exe = [IO.Path]::GetFullPath($exe)
        $bin = [IO.Path]::GetDirectoryName($exe); $root = [IO.Path]::GetDirectoryName($bin)
        if (-not $root) { throw "Installationsordner des Update-Dienstes '$name' ist nicht bestimmbar." }
        $path = Join-Path $root $(if ($kind -eq 'ntc') { 'Einstellungen\einstellung-Cache.xml' } else { 'novacom.ini' })
        $directories[$bin] = $true
        $checkedPaths[$path] = $true
        $sources += [pscustomobject]@{ Name=$name; ExePath=$exe; ConfigPath=$path }
        if (Test-Path -LiteralPath $path -PathType Leaf) { $paths[$path] = $true }
    }
    [pscustomobject]@{ Paths=@($paths.Keys | Sort-Object); Directories=@($directories.Keys | Sort-Object); CheckedPaths=@($checkedPaths.Keys | Sort-Object); Services=$sources }
}

function GetSoftwareDatabaseProviderDirectories([string]$InstallationDirectory) {
    $root = Join-Path $InstallationDirectory 'dev\dotnet\bin'
    $versions = @{}
    # Windows PowerShell runs on .NET Framework: do not load net8.0/net10.0
    # assemblies into its CLR. Newer IRIS releases ship v4.6.2 instead of v4.5.
    foreach ($name in @('v4.6.2', 'v4.5', 'v4.0.30319')) { $versions[$name] = Join-Path $root $name }
    if (Test-Path -LiteralPath $root -PathType Container) {
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction Stop) {
            if ($directory.Name -match '^v4\.\d+(?:\.\d+){0,2}$') { $versions[$directory.Name] = $directory.FullName }
        }
    }
    $versions.Keys | Sort-Object { [version]$_.Substring(1) } -Descending | ForEach-Object { $versions[$_] }
}

function GetSoftwareDatabaseProviderError($Exception) {
    $cause = $Exception.GetBaseException()
    $text = $cause.GetType().Name + ': ' + $cause.Message + (' (HRESULT 0x{0:X8})' -f $cause.HResult)
    if ($cause -is [Reflection.ReflectionTypeLoadException]) {
        foreach ($loader in @($cause.LoaderExceptions | Select-Object -First 8)) {
            if ($null -ne $loader) { $text += ' | ' + $loader.GetType().Name + ': ' + $loader.Message }
        }
    }
    return ($text -replace '[\r\n]+', ' ').Trim()
}

function InitializeSoftwareDatabaseProvider([string[]]$Directories) {
    # Use a provider shipped with the installation. Dynamic binding avoids
    # loading AP.Sys and its UI/configuration side effects or distributing its DLLs.
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($family in @('IRIS', 'Cache')) {
        foreach ($directory in $Directories) {
            $file = Join-Path $directory "InterSystems.Data.${family}Client.dll"
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
            try {
                $bindingFile = Join-Path $directory 'InterSystems.Data.Bindings.dll'
                if ($family -eq 'IRIS' -and -not (Test-Path -LiteralPath $bindingFile -PathType Leaf)) {
                    throw [IO.FileNotFoundException]::new('Die für Objektzugriffe benötigte InterSystems.Data.Bindings.dll fehlt.', $bindingFile)
                }
                # Load the client first, then its bindings from the same directory.
                $assembly = [Reflection.Assembly]::LoadFrom($file)
                $bindings = if ($family -eq 'IRIS') { [Reflection.Assembly]::LoadFrom($bindingFile) } else { $assembly }
                $connection = if ($family -eq 'IRIS') { 'InterSystems.Data.IRISClient.BindingsConnection' } else { 'InterSystems.Data.CacheClient.CacheConnection' }
                $types = 'InterSystems.Data.' + $family + 'Types.'
                # Resolve on the loaded assemblies, not PowerShell's global type
                # cache, which may retain a failed lookup or a different version.
                $connectionType = $bindings.GetType($connection, $true)
                $objectType = $bindings.GetType(($types + $family + 'Object'), $true)
                $clientType = $bindings.GetType(($types + 'ClientTypeId'), $true)
                $binaryStreamType = $bindings.GetType(($types + $family + 'BinaryStream'), $true)
                $builderType = $assembly.GetType(('InterSystems.Data.' + $family + 'Client.' + $family + 'ConnectionStringBuilder'), $true)
                Progress 'IRIS-Provider' ("$family aus '$directory' geladen; " + $(if ([Environment]::Is64BitProcess) { '64' } else { '32' }) + '-Bit Windows PowerShell.')
                return [pscustomobject]@{ ConnectionType=$connectionType; ObjectType=$objectType; ClientType=$clientType; BuilderType=$builderType; BinaryStreamType=$binaryStreamType }
            } catch {
                $failure = "$family '$directory': " + (GetSoftwareDatabaseProviderError $_.Exception)
                $failures.Add($failure)
                Progress 'IRIS-Provider' ($failure + ' Weiteren Provider prüfen.')
            }
        }
    }
    $details = if ($failures.Count -gt 0) { $failures -join ' | ' } else { 'Keine Client-DLL gefunden. Gesuchte Verzeichnisse: ' + ($Directories -join ', ') }
    $runtime = $(if ([Environment]::Is64BitProcess) { '64' } else { '32' }) + '-Bit Windows PowerShell, CLR ' + [Environment]::Version
    throw ("Kein ladbarer InterSystems-.NET-Framework-Provider für Objektzugriffe gefunden ($runtime). " + $details)
}

function OpenSoftwareDatabase($Provider, $Definition, [string]$Namespace) {
    $builder = [Activator]::CreateInstance($Provider.BuilderType)
    # Call CLR setters explicitly: PowerShell treats connection-string builders
    # as dictionaries, where property assignments may instead create keywords.
    $builder.set_Server($Definition.Server); $builder.set_Port($Definition.Port)
    $builder.set_Namespace($Namespace); $builder.set_User($Definition.User); $builder.set_Password($Definition.Password)
    $builder.set_Ssl($Definition.Ssl); $builder.set_Pooling($false); $builder.set_ConnectionTimeout(15)
    $connection = [Activator]::CreateInstance($Provider.ConnectionType)
    try { $connection.ConnectionString = $builder.get_ConnectionString(); $connection.Open(); return $connection }
    catch { $connection.Dispose(); throw 'Datenbankverbindung fehlgeschlagen. Server, Port, SQL-Zugang und Berechtigungen prüfen.' }
}

function InvokeSoftwareObjectMethod($Provider, $Connection, $Object, [string]$Method, [string]$ReturnType, [object[]]$Arguments = @(), [string]$Class = '') {
    $signature = $Connection.GetMtdSignature()
    try {
        $signature.SetReturnType($Connection, [Enum]::Parse($Provider.ClientType, $ReturnType))
        foreach ($argument in $Arguments) { $signature.Add([string]$argument, $false) }
        if ($Class) { $type = $Provider.ObjectType; $type::RunClassMethod($Connection, $Class, $Method, $signature) }
        else { $Object.RunMethod($Method, $signature) }
        $value = $signature.ReturnValue.Value
        if ($ReturnType -eq 'tStatus') { if (-not $value.IsOK) { throw 'InterSystems-Operation lieferte einen Fehlerstatus.' }; return }
        return $value
    } finally { $signature.Clear() }
}

function NewSoftwareDatabaseObject($Provider, $Connection, [string]$Class, [object[]]$Arguments = @()) {
    [Activator]::CreateInstance($Provider.ObjectType, [object[]]@($Connection, $Class, [object[]]$Arguments))
}

function GetSoftwareDatabaseNamespaces($Provider, $Connection) {
    $result = NewSoftwareDatabaseObject $Provider $Connection '%ResultSet' @('%SYS.Namespace:List')
    try {
        InvokeSoftwareObjectMethod $Provider $Connection $result 'Execute' 'tStatus'
        while (InvokeSoftwareObjectMethod $Provider $Connection $result 'Next' 'tInt') {
            $name = InvokeSoftwareObjectMethod $Provider $Connection $result 'Get' 'tString' @('Nsp')
            if (-not [string]::IsNullOrWhiteSpace($name)) { [string]$name }
        }
    } finally { $result.Close() }
}

function SetSoftwareDatabaseProperty($Connection, $Object, [string]$Name, [string]$Value) {
    $signature = $Connection.GetMtdSignature()
    try { $signature.Add($Value, $false); $Object.SetProperty($Name, $signature) }
    finally { $signature.Clear() }
}

function InvokeSoftwareDatabaseSql($Connection, [string]$Sql, [string[]]$Values, $Transaction) {
    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $Sql; $command.CommandTimeout = 180
        $command.Transaction = $Transaction
        foreach ($value in $Values) { $parameter = $command.CreateParameter(); $parameter.Value = $value; [void]$command.Parameters.Add($parameter) }
        [void]$command.ExecuteNonQuery()
    } finally { $command.Dispose() }
}

function WriteSoftwareDatabaseMsi($Provider, $Connection, $Resolved, $Transaction) {
    $package = $Resolved.Package
    $temporaryApplication = $Resolved.Application + '-Updater-' + [guid]::NewGuid().ToString('N')
    $obj = NewSoftwareDatabaseObject $Provider $Connection 'User.zzzSoftware'
    $stream = $null; $file = $null; $hasher = $null
    try {
        foreach ($pair in @(@('Application',$temporaryApplication), @('Version',[string]$package.ProductVersion), @('Ordner','Setup'), @('Dateiname','MSI'), @('Pfad','0'))) {
            SetSoftwareDatabaseProperty $Connection $obj $pair[0] $pair[1]
        }
        $signature = $Connection.GetMtdSignature()
        try {
            # Like the generated nt2-dotnet proxy, request the binary-stream
            # projection, rather than a generic IRISObject with no Write method.
            $signature.SetReturnType($Connection, $Provider.BinaryStreamType)
            $obj.GetProperty('Datei', $signature); $stream = $signature.ReturnValue.Value
        } finally { $signature.Clear() }
        if ($null -eq $stream) { throw 'zzzSoftware.Datei ist kein Binärstream.' }
        [void]$stream.Clear()
        $file = [IO.File]::Open($Resolved.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $hasher = [Security.Cryptography.SHA256]::Create()
        $buffer = New-Object byte[] 30720
        $done = 0L; $clock = [Diagnostics.Stopwatch]::StartNew()
        while (($read = $file.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $stream.Write($buffer, 0, $read)
            [void]$hasher.TransformBlock($buffer, 0, $read, $null, 0); $done += $read
            if ($clock.ElapsedMilliseconds -gt 250) {
                Progress 'zzzSoftware' ($package.ProductName + " · $done / " + $file.Length + ' Bytes') ([int][Math]::Min(99, 100.0 * $done / [Math]::Max(1, $file.Length)))
                $clock.Restart()
            }
        }
        [void]$hasher.TransformFinalBlock([byte[]]@(), 0, 0)
        $actual = [BitConverter]::ToString($hasher.Hash).Replace('-', '')
        if ($actual -ine $Resolved.Sha256 -or $stream.Length -ne $file.Length) { throw 'MSI-Inhalt wurde während des Uploads verändert oder unvollständig übertragen.' }
        if (-not $stream.Save().IsOK) { throw 'MSI-Binärstream konnte nicht gespeichert werden.' }
        InvokeSoftwareObjectMethod $Provider $Connection $obj '%Save' 'tStatus'
        # Replace only after a complete stream has been saved, within the caller's
        # transaction. As in InstallMSI, old service packs belong to the old setup.
        InvokeSoftwareDatabaseSql $Connection "DELETE FROM zzzSoftware WHERE Application=? AND Ordner IN ('Setup','ServicePacks')" @($Resolved.Application) $Transaction
        InvokeSoftwareDatabaseSql $Connection 'UPDATE zzzSoftware SET Application=? WHERE Application=?' @($Resolved.Application, $temporaryApplication) $Transaction
    } finally {
        if ($null -ne $hasher) { $hasher.Dispose() }; if ($null -ne $file) { $file.Dispose() }
        if ($null -ne $stream) { $stream.Close() }; $obj.Close()
    }
}

function TestSoftwareMsiMetadata([string]$Path, $Package) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    try {
        $database = $installer.OpenDatabase($Path, 0)
        foreach ($property in @('ProductName','ProductVersion','ProductCode')) {
            $view = $null; $record = $null
            try {
                $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''' + $property + '''')
                [void]$view.Execute(); $record = $view.Fetch()
                if ($null -eq $record -or [string]$record.StringData(1) -ine [string]$Package.$property) { throw "MSI-$property stimmt nicht mit dem ausgewählten Paket überein." }
            } finally {
                if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            }
        }
    } finally {
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function ResolveSoftwareMsi($Package, [string]$WorkRoot) {
    $applications = @{ ntc='Nov.WaWi'; pos='Nov.NT.POS'; ntv='Nov.Gutscheinverwaltung'; oman='Nov.Oman.SOL.Config' }
    $null = GetMsiUpdateDecision @() $Package # Validate product name/kind/version even when not installed.
    if (-not $applications.ContainsKey([string]$Package.Kind)) { throw 'Unbekanntes MSI-Produkt.' }
    if ([string]$Package.Sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Die Quellprüfsumme des MSI-Pakets fehlt.' }
    $source = [IO.File]::Open([string]$Package.PayloadPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $actual = [BitConverter]::ToString($hash.ComputeHash($source)).Replace('-', '')
        if ($actual -ine $Package.Sha256) { throw 'Die Quellprüfsumme des MSI-Pakets stimmt nicht mehr.' }
        $path = [string]$Package.PayloadPath
        if ([IO.Path]::GetExtension($path) -ieq '.zip') {
            Add-Type -AssemblyName System.IO.Compression
            $source.Position = 0
            $archive = [IO.Compression.ZipArchive]::new($source, [IO.Compression.ZipArchiveMode]::Read, $true)
            try {
                $relative = ([string]$Package.RelativeMsiPath).Replace('/', '\')
                if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Split('\') -contains '..') { throw 'Ungültiger MSI-Pfad im ZIP-Paket.' }
                $entries = @($archive.Entries | Where-Object { $_.FullName.Replace('/', '\') -ieq $relative })
                if ($entries.Count -ne 1 -or [IO.Path]::GetExtension($relative) -ine '.msi') { throw 'Ausgewählte MSI-Datei im ZIP-Paket fehlt oder ist mehrdeutig.' }
                $path = Join-Path $WorkRoot ([guid]::NewGuid().ToString('N') + '.msi')
                $inputStream = $entries[0].Open(); $outputStream = $null
                try { $outputStream = [IO.File]::Create($path); $inputStream.CopyTo($outputStream) }
                finally { if ($null -ne $outputStream) { $outputStream.Dispose() }; $inputStream.Dispose() }
            } finally { $archive.Dispose() }
        } elseif ([IO.Path]::GetExtension($path) -ine '.msi') { throw 'In zzzSoftware dürfen ausschließlich MSI-Dateien hochgeladen werden.' }
        TestSoftwareMsiMetadata $path $Package
        $msiHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        [pscustomobject]@{ Path=$path; Sha256=$msiHash; Package=$Package; Application=$applications[[string]$Package.Kind] }
    } finally { $hash.Dispose(); $source.Dispose() }
}

function PublishSelectedSoftwarePackages([object[]]$Packages) {
    $config = FindSoftwareDatabaseConfiguration
    foreach ($service in $config.Services) { Progress 'zzzSoftware-Konfiguration' ($service.Name + ': ' + $service.ExePath) }
    foreach ($path in $config.CheckedPaths) { Progress 'zzzSoftware-Konfiguration' ('Geprüft: ' + $path) }
    if ($config.Services.Count -eq 0) { throw "Kein Windows-Dienst 'novacom ntcontrol updateservice' oder 'novacom pos updateservice' gefunden. Lokales Update wurde vor der Installation angehalten." }
    if ($config.Paths.Count -eq 0) { throw 'Keine Datenbankkonfiguration aus den Update-Dienstpfaden gefunden: NTC Einstellungen\einstellung-Cache.xml bzw. POS novacom.ini eine Ebene über bin prüfen. Lokales Update wurde vor der Installation angehalten.' }
    foreach ($path in $config.Paths) { Progress 'zzzSoftware-Konfiguration' ('Verwendet: ' + $path) }
    $connections = @(ReadSoftwareConnections $config.Paths)
    if ($connections.Count -eq 0) { throw 'Keine Serververbindungen in den NTC/POS-Konfigurationen gefunden.' }
    if ($Packages.Count -eq 0) { throw 'Keine MSI-Pakete für zzzSoftware ausgewählt.' }
    $provider = InitializeSoftwareDatabaseProvider $config.Directories
    $tempBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'NovaTouchUpdater'))
    $workRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('database-msi-' + [guid]::NewGuid().ToString('N'))))
    [void][IO.Directory]::CreateDirectory($workRoot)
    $published = @{}; $count = 0
    try {
        $resolved = @($Packages | ForEach-Object { ResolveSoftwareMsi $_ $workRoot })
        if (@($resolved.Application | Sort-Object -Unique).Count -ne $resolved.Count) { throw 'Ein MSI-Produkt wurde mehrfach ausgewählt.' }
        foreach ($definition in $connections) {
            $connection = $null
            try {
                Progress 'zzzSoftware' ($definition.Server + ':' + $definition.Port + ' · Namespaces ermitteln') 0
                $connection = OpenSoftwareDatabase $provider $definition $definition.Namespace
                $namespaces = @(GetSoftwareDatabaseNamespaces $provider $connection | Sort-Object -Unique)
                if ($namespaces.Count -eq 0) { throw 'Die Namespace-Liste ist leer.' }
            } catch { throw "Namespaces von $($definition.Server):$($definition.Port) konnten nicht vollständig ermittelt werden. SQL-Berechtigungen und Verbindung prüfen." }
            finally { if ($null -ne $connection) { $connection.Dispose() } }
            $targets = 0
            foreach ($namespace in $namespaces) {
                $key = "$($definition.Server):$($definition.Port)/$namespace"
                if ($published.ContainsKey($key)) { $targets++; continue }
                $connection = $null; $transaction = $null
                try {
                    $connection = OpenSoftwareDatabase $provider $definition $namespace
                    $exists = InvokeSoftwareObjectMethod $provider $connection $null '%ExistsId' 'tBool' @('User.zzzSoftware') '%Dictionary.CompiledClass'
                    if (-not $exists) { Progress 'zzzSoftware' ($key + ' · keine zzzSoftware-Klasse, übersprungen') 0; continue }
                    $transaction = $connection.BeginTransaction()
                    foreach ($msi in $resolved) {
                        Progress 'zzzSoftware' ($key + ' · ' + $msi.Package.ProductName + ' ' + $msi.Package.ProductVersion) 0
                        WriteSoftwareDatabaseMsi $provider $connection $msi $transaction
                    }
                    $transaction.Commit(); $published[$key] = $true; $targets++; $count++
                    Progress 'zzzSoftware' ($key + ' · alle ausgewählten MSIs gespeichert') 100
                } catch {
                    if ($null -ne $transaction) { try { $transaction.Rollback() } catch { } }
                    # Provider exceptions can contain credentials or connection strings.
                    throw "zzzSoftware-Upload nach $key fehlgeschlagen. Keine lokale Installation gestartet. SQL-Berechtigungen, Verbindung und MSI-Dateien prüfen."
                } finally { if ($null -ne $transaction) { $transaction.Dispose() }; if ($null -ne $connection) { $connection.Dispose() } }
            }
            if ($targets -eq 0) { throw "Keine zzzSoftware in den Namespaces von $($definition.Server):$($definition.Port) gefunden." }
        }
        Emit @{ Event='result'; Success=$true; Changed=($count -gt 0); Message=($resolved.Count.ToString() + " MSI-Paket(e) in $count Namespace(s) gespeichert."); Services=@() }
    } finally {
        # Only delete our own, resolved GUID directory, never application settings.
        if ($workRoot.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($workRoot) -match '^database-msi-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Targets come from the same local registry/active-CPF inventory as Database
# Check. Never use an application's SERVER setting as an upload destination.
function PublishLocalIrisSoftwarePackages([object[]]$Packages, [object[]]$Targets) {
    if ($Packages.Count -eq 0) { throw 'Keine MSI-Pakete für zzzSoftware ausgewählt.' }
    if ($Targets.Count -eq 0) { throw 'Keine lokalen IRIS-Instanzen für zzzSoftware übergeben.' }
    $seenInstances = @{}; $seenPorts = @{}
    foreach ($target in $Targets) {
        if (-not $target.Instance -or -not [IO.Path]::IsPathRooted([string]$target.Directory) -or
            [int]$target.Port -lt 1 -or [int]$target.Port -gt 65535 -or -not $target.User -or -not $target.Password) {
            throw 'Lokale IRIS-Konfiguration oder Zugangsdaten fehlen. BETA: Database Check prüfen.'
        }
        if ($seenInstances.ContainsKey([string]$target.Instance) -or $seenPorts.ContainsKey([int]$target.Port)) { throw 'Mehrdeutige lokale IRIS-Instanz oder mehrfach verwendeter SuperServer-Port.' }
        $seenInstances[[string]$target.Instance] = $true; $seenPorts[[int]$target.Port] = $true
    }
    $tempBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'NovaTouchUpdater'))
    $workRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('database-msi-' + [guid]::NewGuid().ToString('N'))))
    [void][IO.Directory]::CreateDirectory($workRoot)
    $eligible = [Collections.Generic.List[object]]::new()
    $count = 0; $skipped = 0
    try {
        # Verify every selected MSI before modifying any namespace. Only the MSI
        # inside an archive is resolved; ZIPs and companion cabinets are excluded.
        $resolved = @($Packages | ForEach-Object { ResolveSoftwareMsi $_ $workRoot })
        if (@($resolved.Application | Sort-Object -Unique).Count -ne $resolved.Count) { throw 'Ein MSI-Produkt wurde mehrfach ausgewählt.' }
        foreach ($target in $Targets) {
            $directories = @(GetSoftwareDatabaseProviderDirectories ([string]$target.Directory))
            $provider = InitializeSoftwareDatabaseProvider $directories
            $definition = [pscustomobject]@{ Server='127.0.0.1'; Port=[int]$target.Port; User=[string]$target.User; Password=[string]$target.Password; Ssl=[bool]$target.Ssl }
            $namespaces = @($target.Namespaces | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -and -not $_.StartsWith('%') } | Sort-Object -Unique)
            foreach ($namespace in $namespaces) {
                $key = [string]$target.Instance + ':' + $target.Port + '/' + $namespace
                $connection = $null
                try {
                    Progress 'zzzSoftware-Vorprüfung' $key 0
                    $connection = OpenSoftwareDatabase $provider $definition $namespace
                    $exists = InvokeSoftwareObjectMethod $provider $connection $null '%ExistsId' 'tBool' @('User.zzzSoftware') '%Dictionary.CompiledClass'
                    if (-not $exists) { Progress 'zzzSoftware' ($key + ' · keine zzzSoftware-Klasse, übersprungen') 0; $skipped++; continue }
                    $eligible.Add([pscustomobject]@{ Key=$key; Provider=$provider; Connection=$connection })
                    $connection = $null # Ownership is transferred to the eligible list.
                } catch { throw "zzzSoftware-Vorprüfung für $key fehlgeschlagen. IRIS-Zugang und Berechtigungen prüfen. Keine lokale Installation gestartet." }
                finally { if ($null -ne $connection) { $connection.Dispose() } }
            }
        }
        if ($eligible.Count -eq 0) { throw 'Keine zzzSoftware in den konfigurierten lokalen IRIS-Namespaces gefunden. Keine lokale Installation gestartet.' }
        foreach ($destination in $eligible) {
            $transaction = $null
            try {
                $transaction = $destination.Connection.BeginTransaction()
                foreach ($msi in $resolved) {
                    Progress 'zzzSoftware' ($destination.Key + ' · ' + $msi.Package.ProductName + ' ' + $msi.Package.ProductVersion) 0
                    WriteSoftwareDatabaseMsi $destination.Provider $destination.Connection $msi $transaction
                }
                $transaction.Commit(); $count++
                Progress 'zzzSoftware' ($destination.Key + ' · alle ausgewählten MSIs gespeichert') 100
            } catch {
                if ($null -ne $transaction) { try { $transaction.Rollback() } catch { } }
                throw "zzzSoftware-Upload nach $($destination.Key) fehlgeschlagen. Der betroffene Namespace wurde zurückgesetzt. Keine lokale Installation gestartet; bereits abgeschlossene Namespaces: $count."
            } finally { if ($null -ne $transaction) { $transaction.Dispose() } }
        }
        Emit @{ Event='result'; Success=$true; Changed=($count -gt 0); Message=($resolved.Count.ToString() + " MSI-Paket(e) in $count lokalen Namespace(s) gespeichert; $skipped ohne zzzSoftware übersprungen."); Services=@() }
    } finally {
        foreach ($destination in $eligible) { $destination.Connection.Dispose() }
        if ($workRoot.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($workRoot) -match '^database-msi-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
