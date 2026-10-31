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

function InitializeSoftwareDatabaseProvider([string[]]$Directories, [string[]]$Families = @('IRIS', 'Cache')) {
    # Use a provider shipped with the installation. Dynamic binding avoids
    # loading AP.Sys and its UI/configuration side effects or distributing its DLLs.
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($family in $Families) {
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
    try {
        $connection.ConnectionString = $builder.get_ConnectionString(); $connection.Open()
        # The bindings otherwise return generic IRISObjects even when the
        # signature explicitly requests IRISBinaryStream. Built-in projections
        # live in this assembly; no generated application DLL is needed.
        $connection.GeneratedAssembly = $Provider.ObjectType.Assembly
        return $connection
    }
    catch {
        $detail = GetSoftwareDatabaseError $_ @($Definition.User, $Definition.Password)
        $connection.Dispose()
        throw "Datenbankverbindung fehlgeschlagen. Server, Port, SQL-Zugang und Berechtigungen prüfen. Ursache: $detail"
    }
}

function GetSoftwareDatabaseError($Failure, [string[]]$Secrets = @()) {
    $exception = $Failure.Exception
    $cause = $exception.GetBaseException()
    $text = $exception.Message
    if ($text -ne $cause.Message) { $text += ' | ' + $cause.GetType().Name + ': ' + $cause.Message }
    else { $text = $cause.GetType().Name + ': ' + $text }
    # Keep actionable provider/status details, while excluding connection credentials.
    $text = [regex]::Replace($text, '(?i)\b(Password|Pwd|Passwort|User\s*ID|UID|User\s*Name|User)\s*=\s*(?:"[^"]*"|''[^'']*''|[^;\r\n]*)', '$1=[geschützt]')
    foreach ($secret in $Secrets) { if (-not [string]::IsNullOrEmpty($secret)) { $text = $text.Replace($secret, '[geschützt]') } }
    return ($text -replace '[\r\n]+', ' ').Trim()
}

function AssertSoftwareDatabaseStatus($Status, [string]$Operation) {
    if ($null -eq $Status) { throw "$Operation lieferte keinen InterSystems-Status." }
    if (-not $Status.IsOK) { $Status.ThrowException(); throw "$Operation lieferte einen Fehlerstatus." }
}

function InvokeSoftwareObjectMethod($Provider, $Connection, $Object, [string]$Method, [string]$ReturnType, [object[]]$Arguments = @(), [string]$Class = '') {
    $signature = $Connection.GetMtdSignature()
    try {
        $signature.SetReturnType($Connection, [Enum]::Parse($Provider.ClientType, $ReturnType))
        foreach ($argument in $Arguments) { $signature.Add([string]$argument, $false) }
        # Match the generated nt2-dotnet proxy. The dynamic APIs query additional
        # class metadata and can fail in MsgGetDynClassDef for existing classes.
        if ($Class) { $type = $Provider.ObjectType; $type::RunClassMethodS($Connection, $Class, $Method, $signature) }
        else { $Object.RunMethodS($Method, $signature) }
        $value = $signature.ReturnValue.Value
        if ($ReturnType -eq 'tStatus') { AssertSoftwareDatabaseStatus $value $Method; return }
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
    try { $signature.Add($Value, $false); $Object.SetPropertyS($Name, $signature) }
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

function GetSoftwareDatabaseUploadDecision($Connection, $Resolved, $Transaction = $null) {
    $wanted = $null
    if (-not [version]::TryParse([string]$Resolved.Package.ProductVersion, [ref]$wanted)) { throw 'Ungültige MSI-Paketversion.' }
    $command = $Connection.CreateCommand(); $reader = $null; $highest = $null
    try {
        $command.CommandText = "SELECT Version FROM zzzSoftware WHERE Application=? AND Ordner='Setup' AND Pfad='0' AND Dateiname='MSI'"
        $command.CommandTimeout = 180
        if ($null -ne $Transaction) { $command.Transaction = $Transaction }
        $parameter = $command.CreateParameter(); $parameter.Value = $Resolved.Application; [void]$command.Parameters.Add($parameter)
        $reader = $command.ExecuteReader()
        while ($reader.Read()) {
            $existing = $null; $text = [string]$reader.GetValue(0)
            if (-not [version]::TryParse($text.Trim(), [ref]$existing)) { throw "Vorhandene zzzSoftware-Version '$text' ist nicht vergleichbar; kein Upload." }
            # Compare missing revision components as zero, e.g. 1.2.3 == 1.2.3.0.
            $existing = [version]::new($existing.Major, $existing.Minor, [Math]::Max(0,$existing.Build), [Math]::Max(0,$existing.Revision))
            if ($null -eq $highest -or $existing -gt $highest) { $highest = $existing }
        }
        $wanted = [version]::new($wanted.Major, $wanted.Minor, [Math]::Max(0,$wanted.Build), [Math]::Max(0,$wanted.Revision))
        return [pscustomobject]@{ Upload=($null -eq $highest -or $highest -lt $wanted); ExistingVersion=[string]$highest }
    } finally { if ($null -ne $reader) { $reader.Dispose() }; $command.Dispose() }
}

function WriteSoftwareDatabaseMsi($Provider, $Connection, $Resolved, $Transaction, [string]$Destination = '') {
    $package = $Resolved.Package
    $progressLabel = $(if ($Destination) { $Destination + ' · ' }) + $package.ProductName + ' ' + $package.ProductVersion
    $temporaryApplication = $Resolved.Application + '-Updater-' + [guid]::NewGuid().ToString('N')
    $obj = $null
    $stream = $null; $file = $null; $hasher = $null
    $operation = 'zzzSoftware-Objekt anlegen'
    try {
        $obj = NewSoftwareDatabaseObject $Provider $Connection 'User.zzzSoftware'
        foreach ($pair in @(@('Application',$temporaryApplication), @('Version',[string]$package.ProductVersion), @('Ordner','Setup'), @('Dateiname','MSI'), @('Pfad','0'))) {
            $operation = 'Eigenschaft ' + $pair[0] + ' setzen'
            SetSoftwareDatabaseProperty $Connection $obj $pair[0] $pair[1]
        }
        $operation = 'MSI-Binärstream öffnen'
        $signature = $Connection.GetMtdSignature()
        try {
            # Like the generated nt2-dotnet proxy, request the binary-stream
            # projection, rather than a generic IRISObject with no Write method.
            $signature.SetReturnType($Connection, $Provider.BinaryStreamType)
            $obj.GetPropertyS('Datei', $signature); $stream = $signature.ReturnValue.Value
        } finally { $signature.Clear() }
        if ($null -eq $stream -or -not $Provider.BinaryStreamType.IsInstanceOfType($stream)) { throw 'zzzSoftware.Datei wurde nicht als Binärstream projiziert.' }
        $operation = 'MSI-Binärstream leeren'
        AssertSoftwareDatabaseStatus ($stream.Clear()) $operation
        $operation = 'MSI-Binärdaten übertragen'
        $file = if ($null -ne $Resolved.StreamSource) { $Resolved.StreamSource.Open($Destination) }
            else { [IO.File]::Open($Resolved.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
        $hasher = [Security.Cryptography.SHA256]::Create()
        $buffer = New-Object byte[] 30720
        $done = 0L; $clock = [Diagnostics.Stopwatch]::StartNew()
        while (($read = $file.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $stream.Write($buffer, 0, $read)
            [void]$hasher.TransformBlock($buffer, 0, $read, $null, 0); $done += $read
            if ($clock.ElapsedMilliseconds -gt 250) {
                Progress 'zzzSoftware' ($progressLabel + " · $done / " + $file.Length + ' Bytes') ([int][Math]::Min(99, 100.0 * $done / [Math]::Max(1, $file.Length)))
                $clock.Restart()
            }
        }
        [void]$hasher.TransformFinalBlock([byte[]]@(), 0, 0)
        $actual = [BitConverter]::ToString($hasher.Hash).Replace('-', '')
        if ($actual -ine $Resolved.Sha256 -or $stream.Length -ne $file.Length) { throw 'MSI-Inhalt wurde während des Uploads verändert oder unvollständig übertragen.' }
        $operation = 'MSI-Binärstream speichern'
        AssertSoftwareDatabaseStatus ($stream.Save()) $operation
        $operation = 'zzzSoftware-Objekt speichern'
        InvokeSoftwareObjectMethod $Provider $Connection $obj '%Save' 'tStatus'
        # Replace only after a complete stream has been saved, within the caller's
        # transaction. As in InstallMSI, old service packs belong to the old setup.
        $operation = 'alte Setups und Servicepacks ersetzen'
        InvokeSoftwareDatabaseSql $Connection "DELETE FROM zzzSoftware WHERE Application=? AND Ordner IN ('Setup','ServicePacks')" @($Resolved.Application) $Transaction
        InvokeSoftwareDatabaseSql $Connection 'UPDATE zzzSoftware SET Application=? WHERE Application=?' @($Resolved.Application, $temporaryApplication) $Transaction
    } catch {
        throw [InvalidOperationException]::new("MSI '$($package.ProductName)': $operation fehlgeschlagen.", $_.Exception)
    } finally {
        if ($null -ne $hasher) { $hasher.Dispose() }; if ($null -ne $file) { $file.Dispose() }
        if ($null -ne $stream) { $stream.Close() }; if ($null -ne $obj) { $obj.Close() }
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
        [pscustomobject]@{ Path=$path; Sha256=$msiHash; Package=$Package; Application=$applications[[string]$Package.Kind]; StreamSource=$null; OnlyDestinations=$null }
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
                        WriteSoftwareDatabaseMsi $provider $connection $msi $transaction $key
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

function InvokeSoftwareDatabaseNamespaceUpload($Provider, $Destination, [object[]]$Resolved) {
    $connection = $null; $transaction = $null; $uploaded = 0; $skipped = 0
    try {
        # Connections belong to this namespace's runspace and are never shared.
        $connection = OpenSoftwareDatabase $Provider $Destination.Definition $Destination.Namespace
        $transaction = $connection.BeginTransaction()
        foreach ($msi in $Resolved) {
            if ($null -ne $msi.OnlyDestinations -and $msi.OnlyDestinations -notcontains $Destination.Key) { continue }
            $decision = GetSoftwareDatabaseUploadDecision $connection $msi $transaction
            if (-not $decision.Upload) {
                if ($null -ne $msi.StreamSource) { $msi.StreamSource.Skip($Destination.Key) }
                Progress 'zzzSoftware' ($Destination.Key + ' · ' + $msi.Package.ProductName + ' · vorhanden: ' + $decision.ExistingVersion + ' · gleich oder neuer, übersprungen') 100
                $skipped++; continue
            }
            Progress 'zzzSoftware' ($Destination.Key + ' · ' + $msi.Package.ProductName + ' ' + $msi.Package.ProductVersion) 0
            WriteSoftwareDatabaseMsi $Provider $connection $msi $transaction $Destination.Key
            $uploaded++
        }
        $transaction.Commit()
        Progress 'zzzSoftware' ($Destination.Key + ' · alle ausgewählten MSIs gespeichert') 100
        return [pscustomobject]@{ Key=$Destination.Key; Success=$true; Changed=($uploaded -gt 0); Uploaded=$uploaded; Skipped=$skipped; Error='' }
    } catch {
        $secrets = @($Destination.Definition.User, $Destination.Definition.Password)
        $detail = GetSoftwareDatabaseError $_ $secrets
        $rollback = 'Keine Namespace-Transaktion gestartet.'
        if ($null -ne $transaction) {
            try { $transaction.Rollback(); $rollback = 'Der betroffene Namespace wurde zurückgesetzt.' }
            catch { $rollback = 'Rollback fehlgeschlagen: ' + (GetSoftwareDatabaseError $_ $secrets) }
        }
        return [pscustomobject]@{ Key=$Destination.Key; Success=$false; Error="zzzSoftware-Upload nach $($Destination.Key) fehlgeschlagen. $rollback Ursache: $detail" }
    } finally {
        foreach ($msi in $Resolved) { if ($null -ne $msi.StreamSource) { $msi.StreamSource.Skip($Destination.Key) } }
        if ($null -ne $transaction) { $transaction.Dispose() }
        if ($null -ne $connection) { $connection.Dispose() }
    }
}

function InvokeSoftwareDatabaseNamespaceUploads($Provider, [object[]]$Destinations, [object[]]$Resolved, [int]$Parallelism) {
    $limit = [Math]::Min($Destinations.Count, [Math]::Max(1, $Parallelism))
    if ($limit -le 1) {
        foreach ($destination in $Destinations) { InvokeSoftwareDatabaseNamespaceUpload $Provider $destination $Resolved }
        return
    }
    $queue = [Collections.Concurrent.ConcurrentQueue[object]]::new()
    $state = [Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    foreach ($name in @('OpenSoftwareDatabase','GetSoftwareDatabaseError','AssertSoftwareDatabaseStatus','InvokeSoftwareObjectMethod',
            'NewSoftwareDatabaseObject','SetSoftwareDatabaseProperty','InvokeSoftwareDatabaseSql','GetSoftwareDatabaseUploadDecision','WriteSoftwareDatabaseMsi','InvokeSoftwareDatabaseNamespaceUpload')) {
        $definition = (Get-Command $name -CommandType Function).Definition
        $state.Commands.Add([Management.Automation.Runspaces.SessionStateFunctionEntry]::new($name, $definition))
    }
    $state.Variables.Add([Management.Automation.Runspaces.SessionStateVariableEntry]::new('databaseUploadProgressQueue', $queue, 'Upload progress relay'))
    $state.Commands.Add([Management.Automation.Runspaces.SessionStateFunctionEntry]::new('Progress', {
        param($Phase, $Message, $Percent = $null)
        $databaseUploadProgressQueue.Enqueue([pscustomobject]@{ Phase=$Phase; Message=$Message; Percent=$Percent })
    }.ToString()))
    $pool = [Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(1, $limit, $state, $Host)
    $jobs = [Collections.Generic.List[object]]::new()
    try {
        $pool.Open()
        foreach ($destination in $Destinations) {
            $pipeline = [Management.Automation.PowerShell]::Create()
            $pipeline.RunspacePool = $pool
            [void]$pipeline.AddScript({
                param($Provider, $Destination, $Resolved)
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                InvokeSoftwareDatabaseNamespaceUpload $Provider $Destination $Resolved
            }.ToString()).AddArgument($Provider).AddArgument($destination).AddArgument($Resolved)
            $job = [pscustomobject]@{ Pipeline=$pipeline; Async=$null; Destination=$destination }
            $jobs.Add($job)
            $job.Async = $pipeline.BeginInvoke()
        }
        do {
            $entry = $null
            while ($queue.TryDequeue([ref]$entry)) { Progress $entry.Phase $entry.Message $entry.Percent }
            foreach ($job in $jobs.ToArray()) {
                if (-not $job.Async.IsCompleted) { continue }
                try {
                    $output = @($job.Pipeline.EndInvoke($job.Async))
                    if ($output.Count -ne 1 -or $null -eq $output[0].PSObject.Properties['Success']) { throw 'Keine gültige Namespace-Rückmeldung erhalten.' }
                    $output[0]
                } catch {
                    $detail = GetSoftwareDatabaseError $_ @($job.Destination.Definition.User, $job.Destination.Definition.Password)
                    [pscustomobject]@{ Key=$job.Destination.Key; Success=$false; Error="zzzSoftware-Upload nach $($job.Destination.Key) fehlgeschlagen. Ursache: $detail" }
                } finally { $job.Pipeline.Dispose(); [void]$jobs.Remove($job) }
            }
            if ($jobs.Count -gt 0) { [Threading.Thread]::Sleep(25) }
        } while ($jobs.Count -gt 0)
        $entry = $null
        while ($queue.TryDequeue([ref]$entry)) { Progress $entry.Phase $entry.Message $entry.Percent }
    } finally {
        foreach ($job in $jobs) {
            try { $job.Pipeline.Stop() } finally { $job.Pipeline.Dispose() }
        }
        $pool.Dispose()
    }
}

# Targets come from the same local registry/active-CPF inventory as Database
# Check. Never use an application's SERVER setting as an upload destination.
function PublishLocalIrisSoftwarePackages([object[]]$Packages, [object[]]$Targets, [string]$ProviderDirectory, [int]$Parallelism = 8) {
    if ($Packages.Count -eq 0) { throw 'Keine MSI-Pakete für zzzSoftware ausgewählt.' }
    if ($Targets.Count -eq 0) { throw 'Keine lokalen IRIS-Instanzen für zzzSoftware übergeben.' }
    if ([string]::IsNullOrWhiteSpace($ProviderDirectory) -or -not [IO.Path]::IsPathRooted($ProviderDirectory)) {
        throw 'Der mitgelieferte IRIS-Provider wurde nicht bereitgestellt. Updater erneut starten.'
    }
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
    $preflightFailures = [Collections.Generic.List[object]]::new()
    $count = 0; $skipped = 0
    try {
        # Verify every selected MSI before modifying any namespace. Only the MSI
        # inside an archive is resolved; ZIPs and companion cabinets are excluded.
        $resolved = @($Packages | ForEach-Object { ResolveSoftwareMsi $_ $workRoot })
        if (@($resolved.Application | Sort-Object -Unique).Count -ne $resolved.Count) { throw 'Ein MSI-Produkt wurde mehrfach ausgewählt.' }
        # Load only the pinned NuGet payload extracted from our EXE. Never search
        # installed IRIS/NTC/POS directories or switch to an installed driver.
        $provider = InitializeSoftwareDatabaseProvider @($ProviderDirectory) @('IRIS')
        foreach ($target in $Targets) {
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
                    $eligible.Add([pscustomobject]@{ Key=$key; Namespace=$namespace; Definition=$definition })
                } catch {
                    $detail = GetSoftwareDatabaseError $_ @($definition.User, $definition.Password)
                    $preflightFailures.Add([pscustomobject]@{ Key=$key; Success=$false; Error="zzzSoftware-Vorprüfung für $key fehlgeschlagen. Ursache: $detail" })
                }
                finally { if ($null -ne $connection) { $connection.Dispose() } }
            }
        }
        if ($eligible.Count -eq 0 -and $preflightFailures.Count -eq 0) { throw 'Keine zzzSoftware in den konfigurierten lokalen IRIS-Namespaces gefunden. Datenbank-Upload nicht durchgeführt.' }
        $outcomes = @($preflightFailures.ToArray())
        if ($eligible.Count -gt 0) { $outcomes += @(InvokeSoftwareDatabaseNamespaceUploads $provider @($eligible) $resolved $Parallelism) }
        $count = @($outcomes | Where-Object Success).Count
        $failed = @($outcomes | Where-Object { -not $_.Success } | Sort-Object Key)
        if ($failed.Count -gt 0) {
            throw ("$count Namespace(s) erfolgreich, $($failed.Count) fehlgeschlagen. Datenbank-Upload unvollständig. " + (($failed | ForEach-Object Error) -join ' || '))
        }
        $changed = @($outcomes | Where-Object Changed).Count -gt 0
        Emit @{ Event='result'; Success=$true; Changed=$changed; Skipped=(-not $changed); Message=("$count Namespace(s) geprüft; " + ($outcomes | Measure-Object Uploaded -Sum).Sum + ' MSI-Upload(s), ' + ($outcomes | Measure-Object Skipped -Sum).Sum + " gleich/neuer; $skipped ohne zzzSoftware übersprungen."); Services=@() }
    } finally {
        if ($workRoot.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($workRoot) -match '^database-msi-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
