# No installer, service or zzzSoftware operations. Only explicitly selected code
# exports are loaded through the existing IRIS bindings connection.
function ReadServicePackItems([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    $items = [Collections.Generic.List[string]]::new()
    try {
        if ($reader.MoveToContent() -ne [Xml.XmlNodeType]::Element -or $reader.LocalName -cne 'Export') { throw 'Kein InterSystems-XML-Export.' }
        while ($reader.Read()) {
            if ($reader.NodeType -ne [Xml.XmlNodeType]::Element -or $reader.Depth -ne 1) { continue }
            if ($reader.LocalName -cnotin @('Class','Routine')) { throw 'Servicepacks dürfen nur Klassen und Routinen enthalten.' }
            $name = $reader.GetAttribute('name')
            if (-not $name -or $name -match '[^\p{L}\p{N}%._]') { throw 'Ungültiger Klassen-/Routinenname im Servicepack.' }
            if ($reader.LocalName -ceq 'Class') { if ($name -notmatch '(?i)\.cls$') { $name += '.cls' } }
            elseif ($name -notmatch '(?i)\.(mac|int|inc|bas)$') {
                $type = $reader.GetAttribute('type'); if (-not $type) { $type = 'MAC' }
                if ($type -notin @('MAC','INT','INC','BAS')) { throw 'Unbekannter Routinentyp.' }
                $name += '.' + $type.ToLowerInvariant()
            }
            if ($items -contains $name) { throw 'Objekt mehrfach in derselben XML-Datei.' }
            $items.Add($name)
        }
    } finally { $reader.Dispose() }
    if ($items.Count -eq 0) { throw 'Keine Klassen oder Routinen im Servicepack.' }
    $items.ToArray()
}

function AssertServicePackFiles([object[]]$Files) {
    if ($Files.Count -eq 0) { throw 'Keine XML-Servicepacks ausgewählt.' }
    foreach ($file in $Files) {
        if ([IO.Path]::GetExtension([string]$file.Path) -ine '.xml' -or -not (Test-Path -LiteralPath $file.Path -PathType Leaf)) { throw 'XML-Servicepack fehlt.' }
        if ([string]$file.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ine $file.Sha256) { throw ('Servicepack-Prüfsumme stimmt nicht: ' + $file.Name) }
        $actual = @(ReadServicePackItems $file.Path | Sort-Object -Unique)
        $expected = @($file.Items | Sort-Object -Unique)
        if ($expected.Count -eq 0 -or @(Compare-Object $actual $expected).Count -gt 0) { throw 'Servicepack-Inhalt entspricht nicht der bestätigten Auswahl.' }
    }
}

function LoadServicePackXml($Provider, $Connection, $File) {
    $stream = $null; $reader = $null; $source = $null
    try {
        $source = [IO.File]::Open($File.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($source)).Replace('-','') } finally { $hasher.Dispose() }
        if ($actualHash -ine $File.Sha256) { throw 'Servicepack wurde nach der Vorbereitung verändert.' }
        $source.Position = 0
        $reader = [IO.StreamReader]::new($source, [Text.UTF8Encoding]::new($false,$true), $true)
        $stream = NewSoftwareDatabaseObject $Provider $Connection '%Stream.GlobalCharacter'
        $buffer = New-Object char[] 8193
        while (($read = $reader.Read($buffer,0,8192)) -gt 0) {
            if ([char]::IsHighSurrogate($buffer[$read-1])) { $next=$reader.Read(); if ($next -ge 0) { $buffer[$read]=[char]$next; $read++ } }
            InvokeSoftwareObjectMethod $Provider $Connection $stream 'Write' 'tStatus' @([string]::new($buffer,0,$read))
        }
        InvokeSoftwareObjectMethod $Provider $Connection $stream 'Rewind' 'tStatus'
        $signature = $Connection.GetMtdSignature()
        try {
            $signature.SetReturnType($Connection, [Enum]::Parse($Provider.ClientType,'tStatus'))
            $signature.Add($stream,$Connection,$Provider.ObjectType,$false)
            $signature.Add('/compile=0',$false)
            $objectType = $Provider.ObjectType
            $objectType::RunClassMethodS($Connection,'%SYSTEM.OBJ','LoadStream',$signature)
            AssertSoftwareDatabaseStatus $signature.ReturnValue.Value 'XML-Import'
        } finally { $signature.Clear() }
    } finally {
        if ($null -ne $stream) {
            try { InvokeSoftwareObjectMethod $Provider $Connection $stream 'Clear' 'tStatus' } catch { }
            $stream.Close()
        }
        if ($null -ne $reader) { $reader.Dispose() }
        elseif ($null -ne $source) { $source.Dispose() }
    }
}

function CompileServicePackItems($Provider, $Connection, [string[]]$Items) {
    $batches = [Collections.Generic.List[string]]::new(); $batch = ''
    foreach ($item in @($Items | Sort-Object -Unique)) {
        if ($batch.Length -gt 0 -and $batch.Length + $item.Length + 1 -gt 16000) { $batches.Add($batch); $batch='' }
        $batch += $(if ($batch) { ',' } else { '' }) + $item
    }
    if ($batch) { $batches.Add($batch) }
    foreach ($list in $batches) {
        $signature = $Connection.GetMtdSignature()
        try {
            $signature.SetReturnType($Connection,[Enum]::Parse($Provider.ClientType,'tStatus'))
            $signature.Add($list,$true); $signature.Add('ckbf',$false)
            $objectType = $Provider.ObjectType
            $objectType::RunClassMethodS($Connection,'%SYSTEM.OBJ','CompileList',$signature)
            AssertSoftwareDatabaseStatus $signature.ReturnValue.Value 'Kompilieren (ckbf)'
        } finally { $signature.Clear() }
    }
}

function ImportServicePacks([object[]]$Files, [object[]]$Targets, [string]$ProviderDirectory) {
    AssertServicePackFiles $Files
    $destinations = @(GetSoftwareDatabaseDestinations $Targets)
    if ($destinations.Count -eq 0) { return @{ Event='result'; Success=$true; Skipped=$true; Changed=$false; Error=''; Message='Keine IRIS-Namespaces ohne %-Präfix gefunden.'; ServicePackResults=@() } }
    foreach ($target in $Targets) { if ([int]$target.Port -lt 1 -or [int]$target.Port -gt 65535) { throw 'Ungültiger IRIS-Port.' } }
    if (@($Targets.Port | Sort-Object -Unique).Count -ne $Targets.Count) { throw 'Mehrdeutige IRIS-Instanzen mit demselben SuperServer-Port.' }
    if (@($destinations.Key | Sort-Object -Unique).Count -ne $destinations.Count) { throw 'IRIS-Ziele mehrfach vorhanden.' }
    $provider = InitializeSoftwareDatabaseProvider @($ProviderDirectory) @('IRIS')
    $results = [Collections.Generic.List[object]]::new(); $completed=0
    $items = @($Files | ForEach-Object Items | Sort-Object -Unique)
    foreach ($destination in $destinations) {
        $connection=$null; $loaded=0; $fileName=''; $compiled=$false; $errorText=''
        try {
            Progress 'Servicepacks verbinden' $destination.Key ([int](100*$completed/$destinations.Count))
            $connection = OpenSoftwareDatabase $provider $destination.Definition $destination.Namespace
            foreach ($file in $Files) {
                $fileName = [string]$file.Name
                Progress 'Servicepack importieren' ($destination.Key + ' · ' + $fileName)
                LoadServicePackXml $provider $connection $file
                $loaded++
            }
            $fileName=''
            Progress 'Servicepacks kompilieren' ($destination.Key + ' · ckbf')
            CompileServicePackItems $provider $connection $items
            $compiled=$true
        } catch {
            $errorText = GetSoftwareDatabaseError $_ @($destination.Definition.User,$destination.Definition.Password)
            if ($fileName) { $errorText = $fileName + ': ' + $errorText }
            Progress 'Servicepack-Fehler' ($destination.Key + ' · ' + $errorText)
        } finally { if ($null -ne $connection) { $connection.Dispose() } }
        $results.Add([pscustomobject]@{ Instance=($destination.Key.Substring(0,$destination.Key.LastIndexOf('/'))); Namespace=$destination.Namespace;
            Success=$compiled; Compiled=$compiled; ImportedFiles=$loaded; Error=$errorText })
        $completed++
        Progress 'Servicepacks' ($destination.Key + $(if ($compiled) { ' · importiert und kompiliert' } else { ' · fehlgeschlagen' })) ([int](100*$completed/$destinations.Count))
    }
    $failed = @($results | Where-Object { -not $_.Success })
    return @{ Event='result'; Success=($failed.Count -eq 0); Changed=(@($results | Where-Object { $_.ImportedFiles -gt 0 }).Count -gt 0);
        Message=('{0} Namespace(s) erfolgreich, {1} fehlgeschlagen; Compilerflags ckbf.' -f ($results.Count-$failed.Count),$failed.Count);
        Error=(($failed | ForEach-Object { $_.Instance + '/' + $_.Namespace + ': ' + $_.Error }) -join ' | '); ServicePackResults=@($results.ToArray()) }
}

function ImportRemoteServicePacks($Request, $Session, [string]$WorkerPath) {
    AssertServicePackFiles @($Request.ServicePackFiles)
    $stage = NewRemotePackageStage $Session
    try {
        foreach ($name in @('InterSystems.Data.IRISClient.dll','InterSystems.Data.Bindings.dll','IRISClient.LICENSE.txt','Bindings.LICENSE.txt')) {
            $source=Join-Path $Request.DatabaseProviderDirectory $name
            CopySoftwareDatabaseFile $Session $source (Join-Path $stage.Path $name) (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        }
        $remoteWorker=Join-Path $stage.Path 'Worker.ps1'
        CopySoftwareDatabaseFile $Session $WorkerPath $remoteWorker (Get-FileHash -LiteralPath $WorkerPath -Algorithm SHA256).Hash
        $files=@(); $index=0
        foreach ($source in $Request.ServicePackFiles) {
            $path=Join-Path $stage.Path ($index.ToString()+'.xml'); $index++
            CopySoftwareDatabaseFile $Session $source.Path $path $source.Sha256
            $files += [pscustomobject]@{ Path=$path; Name=$source.Name; Sha256=$source.Sha256; Items=@($source.Items) }
        }
        $childRequest = @{ Action='import-servicepacks'; LocalExecution=$true; ServicePackFiles=$files; DatabaseTargets=@($Request.DatabaseTargets); DatabaseProviderDirectory=$stage.Path }
        $result = InvokeSoftwareDatabaseChildOperation $Session $remoteWorker $childRequest
        $changed = $null -ne $result.PSObject.Properties['Changed'] -and [bool]$result.Changed
        $outcomes = @()
        if ($null -ne $result.PSObject.Properties['ServicePackResults']) { $outcomes = @($result.ServicePackResults | Where-Object { $null -ne $_ }) }
        return @{ Event='result'; Success=$result.Success; Changed=$changed; Message=$result.Message; Error=$result.Error; ServicePackResults=@($outcomes) }
    } finally { RemoveRemotePackageStage $Session $stage }
}
