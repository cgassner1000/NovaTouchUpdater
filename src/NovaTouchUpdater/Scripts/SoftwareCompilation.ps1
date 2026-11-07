# The Aprima runtime is x86/.NET Framework. Never load it into the GUI or the
# bundled-provider worker. Every host gets a separate, installed-runtime child.
$script:softwareCompilationMethods=@{}
function ReadSoftwareCompilationPrograms {
    foreach ($view in $(if ([Environment]::Is64BitOperatingSystem) { @('Registry64','Registry32') } else { @('Registry32') })) {
        $registry=$null; $uninstall=$null
        try {
            $registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Enum]::Parse([Microsoft.Win32.RegistryView], $view))
            $uninstall=$registry.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', $false)
            if ($null -eq $uninstall) { continue }
            foreach ($name in $uninstall.GetSubKeyNames()) {
                $key=$uninstall.OpenSubKey($name, $false)
                try {
                    if ($null -eq $key -or (([string]$key.GetValue('DisplayName','')).Trim() -replace '\s+',' ') -ine 'NovaTouch Control') { continue }
                    [pscustomobject]@{ InstallLocation=[string]$key.GetValue('InstallLocation',''); DisplayIcon=[string]$key.GetValue('DisplayIcon','') }
                } finally { if ($null -ne $key) { $key.Dispose() } }
            }
        } finally { if ($null -ne $uninstall) { $uninstall.Dispose() }; if ($null -ne $registry) { $registry.Dispose() } }
    }
}

function FindSoftwareCompilationDirectory {
    $directories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($program in @(ReadSoftwareCompilationPrograms)) {
        $location=[Environment]::ExpandEnvironmentVariables(([string]$program.InstallLocation).Trim().Trim('"')).TrimEnd('\')
        if ($location) {
            [void]$directories.Add([IO.Path]::GetFullPath($(if ([IO.Path]::GetFileName($location) -ieq 'bin') { $location } else { Join-Path $location 'bin' })))
        }
        $icon=[Environment]::ExpandEnvironmentVariables([string]$program.DisplayIcon)
        if ($icon -match '^\s*"([^"\r\n]+\.exe)"' -or $icon -match '^\s*(.+?\.exe)(?:,\s*-?\d+)?\s*$') {
            [void]$directories.Add([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Matches[1])))
        }
    }
    $candidates=@($directories | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Nov.NT.Daten.dll') -PathType Leaf })
    if ($candidates.Count -eq 0) {
        # MSI registration may omit InstallLocation or point DisplayIcon at the
        # installer cache. The installed update service lives in the actual bin.
        $serviceName='novacom ntcontrol updateservice'
        foreach ($service in @(Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $serviceName + "'") -ErrorAction Stop)) {
            if ([string]$service.Name -ine $serviceName) { continue }
            $command=[Environment]::ExpandEnvironmentVariables([string]$service.PathName)
            if ($command -match '^\s*"([^"\r\n]+\.exe)"(?:\s|$)' -or $command -match '^\s*([^"\r\n]+?\.exe)(?:\s|$)') { $exe=$Matches[1] }
            else { throw "EXE-Pfad des Dienstes '$serviceName' ist nicht lesbar." }
            if (-not [IO.Path]::IsPathRooted($exe)) { throw "EXE-Pfad des Dienstes '$serviceName' ist nicht absolut." }
            $serviceBin=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($exe))
            [void]$directories.Add($serviceBin)
            if (Test-Path -LiteralPath (Join-Path $serviceBin 'Nov.NT.Daten.dll') -PathType Leaf) { $candidates+=,$serviceBin }
        }
        $candidates=@($candidates | Sort-Object -Unique)
    }
    if ($candidates.Count -eq 0) {
        $checked=if ($directories.Count -gt 0) { ' Geprüfte Verzeichnisse: ' + ($directories -join ', ') + '.' } else { '' }
        throw ("Der bin-Pfad der NovaTouch Control konnte weder über die Windows-Installation noch über den Dienst 'novacom ntcontrol updateservice' ermittelt werden (Nov.NT.Daten.dll fehlt)." + $checked)
    }
    if ($candidates.Count -ne 1) { throw ('Mehrere NovaTouch-Control-Installationen gefunden; keine eindeutige DLL-Quelle: ' + ($candidates -join ', ')) }
    foreach ($name in @('Aprima.Sys.dll','Aprima.SysDB.dll','Nov.SysDB.dll','Nov.NT.Daten.dll','InterSystems.Data.IRISClient.dll','InterSystems.Data.Bindings.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $candidates[0] $name) -PathType Leaf)) { throw "Benötigte DLL fehlt im NovaTouch-Control-bin: $name ($($candidates[0]))" }
    }
    return [string]$candidates[0]
}

function NewSoftwareCompilationEmbeddedResource($Assembly, [string]$Name, [string]$Kind='Classes', [bool]$Once=$false) {
    $application=$Assembly.GetName().Name
    $prefix=if ($application.StartsWith('Aprima',[StringComparison]::OrdinalIgnoreCase)) { 'AP' + $application.Substring(6) } else { $application }
    $manifest=$prefix + '.' + $Name
    if ($manifest -cnotin $Assembly.GetManifestResourceNames()) { throw "XML-Ressource fehlt in ${application}.dll: $Name" }
    $stream=$Assembly.GetManifestResourceStream($manifest); $reader=[IO.StreamReader]::new($stream,$true)
    try { $null=$reader.ReadLine(); $version=$reader.ReadLine() } finally { $reader.Dispose() }
    if (-not $version) { throw "XML-Ressource enthält keine Versionszeile: $application / $Name" }
    return [pscustomobject]@{Name=$Name;Application=$application;Table=$Name;Assembly=$Assembly;Manifest=$manifest;Path='';
        Version=$version;Kind=$Kind;Once=$Once;NoDowngrade=$true}
}

function NewSoftwareCompilationFileResource([string]$Directory, [string]$Name, [bool]$Once=$false) {
    # Daten is a sibling of bin, both on the host being updated.
    $dataDirectory=Join-Path ([IO.Directory]::GetParent([IO.Path]::GetFullPath($Directory).TrimEnd('\')).FullName) 'Daten'
    $path=Join-Path $dataDirectory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Benötigte XML-Datei fehlt neben dem NovaTouch-Control-bin: $path" }
    $reader=[IO.StreamReader]::new($path,$true)
    try { $null=$reader.ReadLine(); $version=$reader.ReadLine() } finally { $reader.Dispose() }
    if (-not $version) { throw "XML-Datei enthält keine Versionszeile: $path" }
    return [pscustomobject]@{Name=$Name;Application='Nov.WaWi';Table=('..\Daten\'+$Name);Assembly=$null;Manifest='';Path=$path;
        Version=$version;Kind='Globals';Once=$Once;NoDowngrade=$false}
}

function GetSoftwareCompilationResources($Aprima, $System, $Data, [string]$Directory) {
    # Datentypen is a distinct resource and must have its own version key.
    # Preserve a separate Daten package only when the DLL actually contains it.
    NewSoftwareCompilationEmbeddedResource $Aprima 'Aprima-Datentypen.xml'
    $aprimaPrefix='AP' + $Aprima.GetName().Name.Substring(6)
    if (($aprimaPrefix+'.Aprima-Daten.xml') -cin $Aprima.GetManifestResourceNames()) {
        NewSoftwareCompilationEmbeddedResource $Aprima 'Aprima-Daten.xml'
    }
    NewSoftwareCompilationEmbeddedResource $Aprima 'Aprima-System.xml'
    NewSoftwareCompilationEmbeddedResource $Aprima 'Aprima-System-PLZ-D.xml' 'Globals' $true
    NewSoftwareCompilationFileResource $Directory 'Aprima-Systemparameter.xml'
    NewSoftwareCompilationEmbeddedResource $System 'Novacom-System.xml'
    NewSoftwareCompilationFileResource $Directory 'bls-v301-sp1.xml' $true
    foreach ($name in @('Novacom-NT-Daten.xml','Novacom-NT-BOH.xml','Novacom-NT-POS.xml','Novacom-NT-POS-API.xml','Novacom-NT-QRY.xml')) {
        NewSoftwareCompilationEmbeddedResource $Data $name
    }
}

function InitializeSoftwareCompilationRuntime([string]$Directory) {
    Add-Type -AssemblyName System.Windows.Forms, Microsoft.VisualBasic
    if (-not ('NovaTouch.Compilation.AssemblyResolver' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
namespace NovaTouch.Compilation {
    public sealed class AssemblyResolver : IDisposable {
        readonly string directory;
        public AssemblyResolver(string path) { directory = Path.GetFullPath(path); AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
        Assembly Resolve(object sender, ResolveEventArgs args) {
            string name = new AssemblyName(args.Name).Name;
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return null;
            string path = Path.Combine(directory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        public void Dispose() { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }
    }
}
'@
    }
    $resolver=[NovaTouch.Compilation.AssemblyResolver]::new($Directory)
    try {
        $provider=InitializeSoftwareDatabaseProvider @($Directory) @('IRIS')
        $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $Directory 'Aprima.Sys.dll'))
        $aprima=[Reflection.Assembly]::LoadFrom((Join-Path $Directory 'Aprima.SysDB.dll'))
        $system=[Reflection.Assembly]::LoadFrom((Join-Path $Directory 'Nov.SysDB.dll'))
        $data=[Reflection.Assembly]::LoadFrom((Join-Path $Directory 'Nov.NT.Daten.dll'))
        foreach ($expected in @(@($aprima,'Aprima.SysDB'),@($system,'Nov.SysDB'),@($data,'Nov.NT.Daten'))) {
            if ($expected[0].GetName().Name -cne $expected[1]) { throw "Unerwartete DLL-Identität für $($expected[1]).dll." }
        }
        $resources=@(GetSoftwareCompilationResources $aprima $system $data $Directory)
        $update=$assembly.GetType('AP.Sys.DB.Update', $true)
        $cache=$assembly.GetType('AP.Sys.DB.Cache', $true)
        $proxy=$assembly.GetType('AP.Sys.dbproxy.User.CacheSystem', $true)
        $versionType=$assembly.GetType('AP.Sys.DB.User.zzzUpdate', $true)
        $globalImport=$assembly.GetType('AP.Sys.dbproxy.cmSY.DatenImport', $true)
        if ($null -eq $globalImport.GetMethod('LoadStream')) { throw 'Installierte Aprima-DLL unterstützt den Global-Import nicht.' }
        $versionObject=$versionType.GetField('obj', [Reflection.BindingFlags]'Instance,NonPublic')
        if ($null -eq $versionObject -or $null -eq $cache.GetField('Connection') -or $null -eq $update.GetMethod('UpdateCreateTable')) { throw 'Installierte Aprima-DLL unterstützt die benötigte Update-Schnittstelle nicht.' }
        return [pscustomobject]@{ Resolver=$resolver; Provider=$provider; Data=$data; Update=$update; Cache=$cache; Proxy=$proxy; GlobalImport=$globalImport;
            VersionType=$versionType; VersionObject=$versionObject; Resources=$resources }
    } catch { $resolver.Dispose(); throw }
}

function InvokeSoftwareCompilationMethod([type]$Type, [string]$Name, [object[]]$Arguments=@()) {
    $key=$Type.AssemblyQualifiedName + '|' + $Name + '|' + $Arguments.Count
    $signature=$script:softwareCompilationMethods[$key]
    if ($null -eq $signature) {
        $methods=@($Type.GetMethods([Reflection.BindingFlags]'Public,Static') | Where-Object { $_.Name -ceq $Name -and $_.GetParameters().Count -eq $Arguments.Count })
        if ($methods.Count -ne 1) { throw "Aprima-Schnittstelle nicht eindeutig: $($Type.FullName).$Name" }
        $types=@($methods[0].GetParameters() | ForEach-Object {
            if ($_.ParameterType.IsByRef) { $_.ParameterType.GetElementType() } else { $_.ParameterType }
        })
        $signature=[pscustomobject]@{ Method=$methods[0]; Types=$types }
        $script:softwareCompilationMethods[$key]=$signature
    }
    for ($index=0; $index -lt $Arguments.Count; $index++) {
        $parameterType=$signature.Types[$index]
        if ($null -ne $Arguments[$index] -and -not $parameterType.IsInstanceOfType($Arguments[$index])) {
            $Arguments[$index]=[Management.Automation.LanguagePrimitives]::ConvertTo($Arguments[$index],$parameterType)
        }
    }
    return $signature.Method.Invoke($null, $Arguments)
}

function CompleteSoftwareCompilationStatus($Runtime, $Connection, $Resource, [string]$Item, $Status, [string]$Destination, [bool]$Classes,
    [Collections.Generic.List[object]]$Failures, [string[]]$Secrets) {
    if ([int]$Status.Code -eq 5380) {
        $flags='frvk/display=none/checkuptodate=expandedonly'
        $Status=InvokeSoftwareCompilationMethod $Runtime.Proxy 'OBJCompileList' @($Connection,$Item,$flags,'')
    }
    if (-not $Status.IsOK) {
        $details=GetSoftwareDatabaseError ([pscustomobject]@{Exception=[Exception]::new([string]$Status.Message)}) $Secrets
        $failedItems=@()
        # With flag b, the failing class can be an existing descendant rather
        # than the class passed to CompileList (IRIS #5030 / #5475).
        $pattern='(?i)(?:compiling\s+(?<kind>class|routine)|Kompilieren\s+(?:(?:der|von)\s+)?(?<kind>Klasse|Routine))\s*:?\s*[''"\x2018\x2019]?(?<name>[%\p{L}\p{N}_]+(?:\.[%\p{L}\p{N}_]+)*)'
        foreach ($match in [regex]::Matches([string]$Status.Message,$pattern)) {
            $type=if ($match.Groups['kind'].Value -imatch '^(class|Klasse)$') { 'Klasse' } else { 'Routine' }
            $failedItems+= [pscustomobject]@{Item=$match.Groups['name'].Value;ItemType=$type}
        }
        if ($failedItems.Count -eq 0) { $failedItems=@([pscustomobject]@{Item=$Item;ItemType=$(if($Classes){'Klasse'}else{'Routine'})}) }
        foreach ($failed in $failedItems) {
            $name=$failed.Item -replace '(?i)\.cls$',''
            if (@($Failures | Where-Object { $_.Resource -ceq $Resource.Name -and $_.Item -ieq $name -and $_.ItemType -eq $failed.ItemType }).Count -gt 0) { continue }
            $separator=$Destination.LastIndexOf('/')
            $failure=[pscustomobject]@{Instance=$Destination.Substring(0,$separator);Namespace=$Destination.Substring($separator+1);
                Resource=$Resource.Name;Item=$name;ItemType=$failed.ItemType;Details=$details}
            $Failures.Add($failure)
            Progress 'Softwarepaket-Kompilierungsfehler' ($Destination + ' · ' + $Resource.Name + ' · Fehler beim Kompilieren der ' + $failure.ItemType + ': ' + $name + ' · ' + $details)
        }
    }
}

function CompileSoftwareCompilationItems($Runtime, $Connection, $Resource, [string[]]$Items, [string]$Destination, [bool]$Classes,
    [Collections.Generic.List[object]]$Failures, [string[]]$Secrets) {
    if ($Items.Count -eq 0) { return }
    # The installed Aprima proxy forwards a comma-separated list to CompileList.
    # Bound the UTF-8 size for providers using the legacy short-string protocol.
    $groups=[Collections.Generic.List[object]]::new()
    $group=[Collections.Generic.List[string]]::new(); $bytes=0
    foreach ($item in $Items) {
        $size=[Text.Encoding]::UTF8.GetByteCount($item)+1
        if ($size -gt 24000) { throw 'Klassen-/Routinenname überschreitet die maximale Compilerlistenlänge.' }
        if ($group.Count -gt 0 -and $bytes+$size -gt 24000) { $groups.Add($group.ToArray()); $group.Clear(); $bytes=0 }
        $group.Add($item); $bytes+=$size
    }
    if ($group.Count -gt 0) { $groups.Add($group.ToArray()) }
    # Explicit entries must be rebuilt by this manual action. Only already
    # current dependencies expanded by r/b may be skipped by the compiler.
    $flags='frvkb/display=none/checkuptodate=expandedonly'
    $done=0; $kind=if ($Classes) { 'Klassen' } else { 'Routinen' }
    foreach ($batch in $groups) {
        Progress 'Softwarepaket kompilieren' ("$Destination · $($Resource.Name) · $kind $($done+1)–$($done+$batch.Count)/$($Items.Count) · Compilerflags: $flags")
        $status=InvokeSoftwareCompilationMethod $Runtime.Proxy 'OBJCompileList' @($Connection,([string]::Join(',', [string[]]$batch)),$flags,'')
        if (-not $status.IsOK) {
            if ($batch.Count -eq 1) {
                CompleteSoftwareCompilationStatus $Runtime $Connection $Resource $batch[0] $status $Destination $Classes $Failures $Secrets
            } else {
                # Diagnose individual entries and continue the remainder,
                # retaining the 5380 retry. Do not skip explicit current classes.
                # Retain named compiler errors before checking currency: the
                # parent may already be current although its descendant failed.
                if ([int]$status.Code -ne 5380 -and [string]$status.Message -match '(?i)(compiling\s+(class|routine)|Kompilieren\s+(?:(?:der|von)\s+)?(Klasse|Routine))') {
                    CompleteSoftwareCompilationStatus $Runtime $Connection $Resource $batch[0] $status $Destination $Classes $Failures $Secrets
                }
                Progress 'Softwarepaket-Warnung' ("$Destination · $($Resource.Name) · Compilerliste fehlgeschlagen; betroffene Einträge einzeln prüfen.")
                foreach ($item in $batch) {
                    $individual=InvokeSoftwareCompilationMethod $Runtime.Proxy 'OBJCompileList' @($Connection,$item,$flags,'')
                    CompleteSoftwareCompilationStatus $Runtime $Connection $Resource $item $individual $Destination $Classes $Failures $Secrets
                }
            }
        }
        $done+=$batch.Count
    }
}

function OpenSoftwareCompilationVersion($Runtime, $Resource) {
    $version=[Activator]::CreateInstance($Runtime.VersionType)
    try {
        $application=$Resource.Application.Replace("'","''"); $table=$Resource.Table.Replace("'","''")
        $version.OpenIdBySQL("SELECT ID FROM zzzUpdate WHERE Application='$application' And Tabelle='$table'")
        return $version
    } catch { $version.Close(); throw }
}

function GetSoftwareCompilationSkipReason($Version, $Resource) {
    if ($Resource.Kind -eq 'Globals' -and [string]$Version.Version) {
        if ($Resource.Once) { return 'Globale Stammdaten wurden bereits eingespielt (einmaliger Import wie im NTC).' }
        if ([string]$Version.Version -ceq [string]$Resource.Version) { return 'Globals-Version ist bereits aktuell.' }
    }
    # Class packages are rebuilt explicitly. The timestamp guard follows the
    # original KeinDowngrade setting; file-based globals use the NTC's defaults.
    $fileTimestamp=[regex]::Match([string]$Resource.Version, 'ts="([^"]{19})')
    $dbTimestamp=[regex]::Match([string]$Version.Version, 'ts="([^"]{19})')
    if ($Resource.NoDowngrade -and $fileTimestamp.Success -and $dbTimestamp.Success -and [string]::CompareOrdinal($fileTimestamp.Groups[1].Value, $dbTimestamp.Groups[1].Value) -lt 0) {
        return 'Downgrade-Schutz: Die Datenbank enthält eine neuere XML-Version.'
    }
    return ''
}

function TestSoftwareCompilationNeeded($Version, $Resource) {
    return -not (GetSoftwareCompilationSkipReason $Version $Resource)
}

function OpenSoftwareCompilationSource($Resource) {
    if ($Resource.Path) {
        $stream=[IO.File]::OpenRead($Resource.Path)
        try {
            $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true,1024,$true)
            try { $null=$reader.ReadLine(); $version=$reader.ReadLine() } finally { $reader.Dispose() }
            if ($version -cne $Resource.Version) { throw "XML-Datei wurde seit der Vorbereitung geändert: $($Resource.Path)" }
            $stream.Position=0
            return $stream
        } catch { $stream.Dispose(); throw }
    }
    return $Resource.Assembly.GetManifestResourceStream($Resource.Manifest)
}

function ImportSoftwareCompilationResource($Runtime, $Connection, $Resource, [string]$Destination,
    [Collections.Generic.List[object]]$Failures=[Collections.Generic.List[object]]::new(), [string[]]$Secrets=@()) {
    $version=$null; $locked=$false; $stream=$null; $binary=$null
    $imported=$false; $temporaryXml=$false
    $failuresBefore=$Failures.Count
    $elapsed=[Diagnostics.Stopwatch]::StartNew()
    try {
        $version=OpenSoftwareCompilationVersion $Runtime $Resource
        if (-not (TestSoftwareCompilationNeeded $version $Resource)) {
            Progress 'Softwarepaket übersprungen' ($Destination + ' · ' + $Resource.Name + ' · ' + (GetSoftwareCompilationSkipReason $version $Resource))
            return [pscustomobject]@{ Imported=$false; Skipped=$true }
        }
        if (-not (InvokeSoftwareCompilationMethod $Runtime.Update 'LockExklusiv')) { throw 'Updates werden bereits an einem anderen Arbeitsplatz eingespielt.' }
        $locked=$true
        # Recheck under the original namespace-exclusive update lock.
        $version.Close(); $version=OpenSoftwareCompilationVersion $Runtime $Resource
        if (-not (TestSoftwareCompilationNeeded $version $Resource)) {
            Progress 'Softwarepaket übersprungen' ($Destination + ' · ' + $Resource.Name + ' · ' + (GetSoftwareCompilationSkipReason $version $Resource))
            return [pscustomobject]@{ Imported=$false; Skipped=$true }
        }
        Progress 'Softwarepaket übertragen' ($Destination + ' · ' + $Resource.Name)
        $stream=OpenSoftwareCompilationSource $Resource
        $binary=[Activator]::CreateInstance($Runtime.Provider.BinaryStreamType, [object[]]@($Connection))
        $buffer=New-Object byte[] 30720
        while (($read=$stream.Read($buffer,0,$buffer.Length)) -gt 0) { $binary.Write($buffer,0,$read) }
        # Unlike the VB call site, PowerShell forwards an unused return value to
        # the function's success stream. Rewind returns IRISStatus, not void.
        AssertSoftwareDatabaseStatus ($binary.Rewind()) 'XML-Stream zurückspulen'
        $transferredAt=$elapsed.Elapsed.TotalSeconds
        if ($Resource.Kind -eq 'Globals') {
            # This is the NTC's legacy global reader. Its string result preserves
            # import errors; it has no terminal output and requires no temp XML.
            $imported=$true
            Progress 'Globale Stammdaten importieren' ($Destination + ' · ' + $Resource.Name)
            $loadResult=InvokeSoftwareCompilationMethod $Runtime.GlobalImport 'LoadStream' @($Connection,$binary)
            if ([string]$loadResult -cne '1') { throw ('Globale Stammdaten konnten nicht importiert werden: ' + $loadResult) }
            $loadedAt=$elapsed.Elapsed.TotalSeconds
            $binary.Close(); $binary=$null; $stream.Dispose(); $stream=$null
        } else {
            $temporaryXml=$true
            AssertSoftwareDatabaseStatus (InvokeSoftwareCompilationMethod $Runtime.Proxy 'FileWriteImportXML' @($Connection,$binary)) 'XML-Ressource übertragen'
            $binary.Close(); $binary=$null; $stream.Dispose(); $stream=$null
            $loadArguments=[object[]]@($Connection,'Import.xml','fv/display=none','','')
            # OBJLoad can fail after a partial import.
            $imported=$true
            AssertSoftwareDatabaseStatus (InvokeSoftwareCompilationMethod $Runtime.Proxy 'OBJLoad' $loadArguments) 'XML-Ressource importieren'
            $loadedAt=$elapsed.Elapsed.TotalSeconds
            $items=@(([string]$loadArguments[4]).Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            if ($items.Count -eq 0) { throw 'Der XML-Import hat keine Klassen oder Routinen zurückgemeldet.' }
            # Classes before routines, using bounded compiler lists.
            $classes=@($items | Where-Object { $_ -match '(?i)\.cls$' })
            $routines=@($items | Where-Object { $_ -match '(?i)\.(mac|int|bas)$' })
            $null=CompileSoftwareCompilationItems $Runtime $Connection $Resource $classes $Destination $true $Failures $Secrets
            $null=CompileSoftwareCompilationItems $Runtime $Connection $Resource $routines $Destination $false $Failures $Secrets
            $null=InvokeSoftwareCompilationMethod $Runtime.Proxy 'SQLPurge' @($Connection)
        }
        $compiledAt=$elapsed.Elapsed.TotalSeconds
        if ($Failures.Count -eq $failuresBefore) {
            $version.Application=$Resource.Application; $version.Tabelle=$Resource.Table
            $version.Version=$Resource.Version; $version.dbversion=[string]$Connection.ServerVersion
            # Do not mark a resource with compiler errors as current: a later
            # run must be able to retry it while other resources can finish.
            $persistent=$Runtime.VersionObject.GetValue($version)
            AssertSoftwareDatabaseStatus ($persistent.Save()) 'zzzUpdate-Version speichern'
            if ($Resource.Kind -eq 'Classes') {
                $command=$Connection.CreateCommand()
                try { $command.CommandText="DELETE FROM zzzSoftware WHERE Ordner='ServicePack' AND Dateiname='XML'"; $null=$command.ExecuteNonQuery() }
                finally { $command.Dispose() }
            }
        }
        if ($Resource.Kind -eq 'Globals') {
            Progress 'Softwarepaket-Zeiten' (('{0} · {1} · Bereitstellen {2:N2}s · Import {3:N2}s · Gesamt {4:N2}s · nur eingespielt (ohne Kompilierung)' -f
                $Destination,$Resource.Name,$transferredAt,($loadedAt-$transferredAt),$elapsed.Elapsed.TotalSeconds))
        } else {
            Progress 'Softwarepaket-Zeiten' (('{0} · {1} · Bereitstellen {2:N2}s · Import {3:N2}s · Kompilieren {4:N2}s · Gesamt {5:N2}s' -f
                $Destination,$Resource.Name,$transferredAt,($loadedAt-$transferredAt),($compiledAt-$loadedAt),$elapsed.Elapsed.TotalSeconds))
        }
        return [pscustomobject]@{ Imported=$true; Skipped=$false; CompilationErrors=@($Failures.ToArray() | Select-Object -Skip $failuresBefore) }
    } catch {
        if ($imported) { $_.Exception.Data['SoftwareCompilationChanged']=$true }
        throw
    } finally {
        try {
            if ($null -ne $binary) { $binary.Close() }
            if ($null -ne $stream) { $stream.Dispose() }
            if ($null -ne $version) { $version.Close() }
        } finally {
            if ($locked) {
                try { if ($temporaryXml) { $null=InvokeSoftwareCompilationMethod $Runtime.Proxy 'FileDelete' @($Connection,'Import.xml') } }
                catch { Progress 'Softwarepaket-Warnung' ($Destination + ' · Import.xml konnte nicht entfernt werden.') }
                finally { $null=InvokeSoftwareCompilationMethod $Runtime.Update 'UnLockExklusiv' }
            }
        }
    }
}

function CompileSoftwarePackages([object[]]$Targets) {
    $destinations=@(GetSoftwareDatabaseDestinations $Targets)
    if ($destinations.Count -eq 0) { return @{ Event='result'; Success=$true; Skipped=$true; Changed=$false; Message='Keine IRIS-Namespaces ohne %-Präfix gefunden.'; Error=''; SoftwareCompilationResults=@() } }
    foreach ($target in $Targets) { if ([int]$target.Port -lt 1 -or [int]$target.Port -gt 65535) { throw 'Ungültiger IRIS-Port.' } }
    if (@($Targets.Port | Sort-Object -Unique).Count -ne $Targets.Count -or @($destinations.Key | Sort-Object -Unique).Count -ne $destinations.Count) { throw 'IRIS-Ziele sind nicht eindeutig.' }
    $directory=FindSoftwareCompilationDirectory
    Progress 'Softwarepakete vorbereiten' ('NovaTouch Control: ' + $directory + ' · 32-Bit-Windows-PowerShell')
    $runtime=InitializeSoftwareCompilationRuntime $directory
    $results=[Collections.Generic.List[object]]::new(); $changed=$false
    try {
        foreach ($destination in $destinations) {
            $connection=$null; $errorText=''; $resourceName=''; $imported=0; $skipped=0
            $failures=[Collections.Generic.List[object]]::new()
            try {
                $connection=OpenSoftwareDatabase $runtime.Provider $destination.Definition $destination.Namespace
                $runtime.Cache.GetField('Connection').SetValue($null,$connection)
                $runtime.Update.GetField('TablesCreated').SetValue($null,$false)
                $runtime.Update.GetField('ExitApplication').SetValue($null,$false)
                $null=InvokeSoftwareCompilationMethod $runtime.Update 'UpdateCreateTable'
                foreach ($resource in $runtime.Resources) {
                    $resourceName=$resource.Name
                    $outcome=ImportSoftwareCompilationResource $runtime $connection $resource $destination.Key $failures @($destination.Definition.User,$destination.Definition.Password)
                    if ($outcome.Imported) { $imported++; $changed=$true } else { $skipped++ }
                }
            } catch {
                $failure=$_; $exception=$failure.Exception
                while ($null -ne $exception) { if ($exception.Data.Contains('SoftwareCompilationChanged')) { $changed=$true }; $exception=$exception.InnerException }
                $errorText=GetSoftwareDatabaseError $failure @($destination.Definition.User,$destination.Definition.Password)
                if ($resourceName) { $errorText=$resourceName + ': ' + $errorText }
                Progress 'Softwarepaket-Fehler' ($destination.Key + ' · ' + $errorText)
            } finally {
                $runtime.Cache.GetField('Connection').SetValue($null,$null)
                if ($null -ne $connection) { $connection.Dispose() }
            }
            if ($failures.Count -gt 0) {
                $compilerText=($failures | ForEach-Object { 'Fehler beim Kompilieren der ' + $_.ItemType + ': ' + $_.Item + ' · ' + $_.Details }) -join ' | '
                $errorText=(@($compilerText,$errorText | Where-Object { $_ }) -join ' | ')
            }
            $results.Add([pscustomobject]@{ Instance=($destination.Key.Substring(0,$destination.Key.LastIndexOf('/'))); Namespace=$destination.Namespace;
                Success=($errorText.Length -eq 0); ImportedResources=$imported; SkippedResources=$skipped; Error=$errorText; CompilationErrors=$failures.ToArray() })
            Progress 'Softwarepakete' ($destination.Key + $(if ($errorText) { ' · fehlgeschlagen' } else { ' · abgeschlossen' })) ([int](100*$results.Count/$destinations.Count))
        }
    } finally { $runtime.Resolver.Dispose() }
    $failed=@($results | Where-Object { -not $_.Success })
    return @{ Event='result'; Success=($failed.Count -eq 0); Changed=$changed; SoftwareCompilationResults=@($results.ToArray());
        Message=('{0} Namespace(s) erfolgreich, {1} fehlgeschlagen.' -f ($results.Count-$failed.Count),$failed.Count);
        Error=(($failed | ForEach-Object { $_.Instance + '/' + $_.Namespace + ': ' + $_.Error }) -join ' | ') }
}

function InvokeSoftwareCompilation($Request, $Session, [string]$WorkerPath) {
    $stage=$null
    try {
        $worker=$WorkerPath
        if (-not (TestLocalSession $Session)) {
            $stage=NewRemotePackageStage $Session
            $worker=Join-Path $stage.Path 'Worker.ps1'
            CopySoftwareDatabaseFile $Session $WorkerPath $worker (Get-FileHash -LiteralPath $WorkerPath -Algorithm SHA256).Hash 'Softwarepakete kompilieren'
        }
        $child=@{ Action='compile-software-packages'; LocalExecution=$true; SoftwareCompilationChild=$true; DatabaseTargets=@($Request.DatabaseTargets) }
        $result=InvokeSoftwareDatabaseChildOperation $Session $worker $child
        $message=@{}
        foreach ($property in $result.PSObject.Properties) { $message[$property.Name]=$property.Value }
        return $message
    } finally { if ($null -ne $stage) { RemoveRemotePackageStage $Session $stage } }
}
