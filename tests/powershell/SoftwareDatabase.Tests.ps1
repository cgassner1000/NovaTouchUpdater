. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
function ExpectFailure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true; Assert (-not $_.Exception.Message.Contains('synthetic-secret')) 'database errors do not expose credentials' }
    Assert $failed $Message
}
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('software-database-' + [guid]::NewGuid().ToString('N'))))
Assert ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'all test writes stay in test artifacts'
[void][IO.Directory]::CreateDirectory($fixture)
$originalTemp = $env:TEMP; $originalTmp = $env:TMP
$env:TEMP = $fixture; $env:TMP = $fixture
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'MsiUpdates.ps1'))))
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'SoftwareDatabase.ps1'))))
Add-Type -Path (Join-Path $PSScriptRoot '..\Shared\SoftwareDatabaseFake.cs') -ReferencedAssemblies System.Data, System.Core
function Progress { param($Phase,$Message,$Percent) $script:progress.Add([string]$Message) }
function Emit($Value) { $script:results.Add($Value) }
function InitializeSoftwareDatabaseProvider([string[]]$Directories, [string[]]$Families = @('IRIS', 'Cache')) {
    $script:lastProviderDirectories = $Directories; $script:lastProviderFamilies = $Families
    [pscustomobject]@{ ConnectionType=[SoftwareDatabaseFixture.Connection]; BuilderType=[SoftwareDatabaseFixture.Builder]; ObjectType=[SoftwareDatabaseFixture.DynamicObject]; ClientType=[SoftwareDatabaseFixture.ClientType]; BinaryStreamType=[SoftwareDatabaseFixture.BinaryStream] }
}
function Get-CimInstance {
    param($ClassName,$Filter,$ErrorAction,$ComputerName)
    if ($ClassName -ne 'Win32_Service' -or $ComputerName) { throw 'Unexpected service query' }
    $script:serviceQueries++
    $script:services
}
function Get-ItemProperty { throw 'Database publication must not depend on uninstall registration' }
function New-PSSession { throw 'Database publication must never use WinRM' }
function Start-Process { throw 'Database publication must never run an installer' }
function Stop-Service { throw 'Database publication must never stop services' }
function Start-Service { throw 'Database publication must never start services' }
$script:results = [Collections.Generic.List[object]]::new()
$script:progress = [Collections.Generic.List[string]]::new()
$script:serviceQueries = 0
try {
    $ntcRoot = Join-Path $fixture 'Control'; $posRoot = Join-Path $fixture 'POS'
    foreach ($root in @($ntcRoot, $posRoot)) { [void][IO.Directory]::CreateDirectory((Join-Path $root 'bin')) }
    [void][IO.Directory]::CreateDirectory((Join-Path $ntcRoot 'Einstellungen'))
    $configPath = Join-Path $ntcRoot 'Einstellungen\einstellung-Cache.xml'
    $xml = '<einstellungen><DB><C1><server>fixture.invalid</server><DatabasePort>1972</DatabasePort><Database>MANDANT1</Database><SQLDSN>Namespace=MANDANT1</SQLDSN><SQLUser>test</SQLUser><SQLPasswort>synthetic-secret</SQLPasswort></C1><C2><Server>fixture.invalid</Server><DatabasePort>1972</DatabasePort><Database>MANDANT2</Database><SQLUser>test</SQLUser><SQLPasswort>synthetic-secret</SQLPasswort></C2></DB></einstellungen>'
    $xml = $xml.Replace('</DB>', '<C3><Server>other-fixture.invalid</Server><Database>MANDANT1</Database><SQLUser>test</SQLUser><SQLPasswort>synthetic-secret</SQLPasswort></C3></DB>')
    [IO.File]::WriteAllText($configPath, $xml)
    [IO.File]::WriteAllText((Join-Path $posRoot 'novacom.ini'), "[NOVACOM]`r`nSYSTEM=CACHE`r`nSERVER=cn_iptcp:fixture.invalid[1972]:MANDANT1`r`nUSER=test`r`nPW=synthetic-secret`r`nSSL=0`r`n")
    $ntcService = [pscustomobject]@{ Name='novacom ntcontrol updateservice'; PathName=('"' + (Join-Path $ntcRoot 'bin\Control.Service.exe') + '" --service') }
    $posService = [pscustomobject]@{ Name='novacom pos updateservice'; PathName=('"' + (Join-Path $posRoot 'bin\POS.Service.exe') + '"') }
    $script:services = @($ntcService,$posService)
    $configuration = FindSoftwareDatabaseConfiguration
    Assert ($configuration.Paths.Count -eq 2) 'service EXEs resolve NTC settings XML and POS novacom.ini'
    $connections = @(ReadSoftwareConnections $configuration.Paths)
    Assert ($connections.Count -eq 3 -and $connections[0].User -eq 'test' -and $connections[0].Password -eq 'synthetic-secret') 'all XML connections are read case-insensitively and duplicate NTC/POS definitions are removed'
    $encrypted = $null; $aes = [Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = [Text.Encoding]::UTF8.GetBytes('ViBaGeneralRijndaelKeyA!B$%OP98#'); $aes.IV = [Text.Encoding]::UTF8.GetBytes('VectViBaRijndael')
        $plain = [Text.Encoding]::UTF8.GetBytes('encrypted-fixture'); $encoder = $aes.CreateEncryptor()
        try { $encrypted = [BitConverter]::ToString($encoder.TransformFinalBlock($plain,0,$plain.Length)).Replace('-','') } finally { $encoder.Dispose() }
    } finally { $aes.Dispose() }
    Assert ((DecodeSoftwareCredential $encrypted) -eq 'encrypted-fixture') 'User2 and Passwort2 use the existing AP.Sys encryption format'
    [IO.File]::WriteAllText($configPath, ('<einstellungen><DB><C1><Server>fixture.invalid</Server><SQLUser2>{0}</SQLUser2><SQLPasswort2>{0}</SQLPasswort2></C1></DB></einstellungen>' -f $encrypted))
    $decoded = @(ReadSoftwareConnections @($configPath))[0]
    Assert ($decoded.User -eq 'encrypted-fixture' -and $decoded.Password -eq 'encrypted-fixture') 'encrypted SQL fields are selected and decoded from the XML'
    [IO.File]::WriteAllText($configPath, '<einstellungen><DB><C1><Server>fixture.invalid</Server><SQLDSN>Namespace=DSN_NAMESPACE;PORT=51773;UID=test;PWD=synthetic-secret</SQLDSN></C1></DB></einstellungen>')
    $dsnConnection = @(ReadSoftwareConnections @($configPath))[0]
    Assert ($dsnConnection.Namespace -eq 'DSN_NAMESPACE' -and $dsnConnection.Port -eq 51773) 'SQLDSN connection definitions supply namespace, port and credentials'
    [IO.File]::WriteAllText($configPath, $xml.Replace('<DatabasePort>1972</DatabasePort>','<DatabasePort>broken</DatabasePort>'))
    ExpectFailure { ReadSoftwareConnections @($configPath) } 'invalid configured ports stop publication'
    [IO.File]::WriteAllText($configPath, '<!DOCTYPE e [<!ENTITY ext SYSTEM "file:///never-read">]><einstellungen><DB>&ext;</DB></einstellungen>')
    ExpectFailure { ReadSoftwareConnections @($configPath) } 'XML external entities are prohibited'
    [IO.File]::WriteAllText($configPath, $xml)

    $index = Get-Content -LiteralPath (Join-Path $TestArtifactsRoot 'local-msi-fixtures.json') -Raw | ConvertFrom-Json
    function Package([string]$Kind, [string]$Path) {
        $installer = New-Object -ComObject WindowsInstaller.Installer; $db = $installer.OpenDatabase($Path, 0)
        $values = @{}
        try {
            foreach ($name in @('ProductName','ProductVersion','ProductCode')) {
                $view = $db.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''' + $name + ''''); [void]$view.Execute(); $record = $view.Fetch()
                $values[$name] = [string]$record.StringData(1)
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
            }
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
        [pscustomobject]@{ Kind=$Kind; ProductName=$values.ProductName; ProductVersion=$values.ProductVersion; ProductCode=$values.ProductCode;
            PayloadPath=$Path; Sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash; RelativeMsiPath='' }
    }
    $ntc = Package ntc ([string]$index.ntc)
    $pos = Package pos ([string]$index.pos)
    $ntv = Package ntv ([string]$index.ntv)
    $oman = Package oman ([string]$index.oman)
    $bundle = Join-Path $fixture 'bundle.zip'
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($bundle, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $pos.PayloadPath, 'POS/setup.msi')
        $entry = $archive.CreateEntry('service.zip'); $writer = [IO.StreamWriter]::new($entry.Open()); try { $writer.Write('never upload') } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    $pos.PayloadPath = $bundle; $pos.RelativeMsiPath = 'POS/setup.msi'; $pos.Sha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
    $selected = @($ntc,$pos,$ntv,$oman)
    $script:services = @($ntcService)
    PublishSelectedSoftwarePackages $selected
    $entries = [SoftwareDatabaseFixture.Database]::Entries
    Assert ($entries.Count -eq 16 -and $script:results.Count -eq 1 -and $script:results[0].Success) 'NTC alone publishes all four chosen MSIs to every application namespace on both servers; POS need not be installed'
    $applicationKinds = @{ 'Nov.WaWi'='ntc'; 'Nov.NT.POS'='pos'; 'Nov.Gutscheinverwaltung'='ntv'; 'Nov.Oman.SOL.Config'='oman' }
    foreach ($server in @('fixture.invalid','other-fixture.invalid')) {
    foreach ($namespace in @('MANDANT1','MANDANT2')) {
        foreach ($application in $applicationKinds.Keys) {
            $entry = @($entries | Where-Object { $_.Target -eq "$server/$namespace" -and $_.Application -eq $application })[0]
            Assert ($entry.Ordner -eq 'Setup' -and $entry.Dateiname -eq 'MSI' -and $entry.Pfad -eq '0' -and $entry.Bytes.Length -gt 0) 'stored metadata matches the existing uploader'
            $hash = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($hash.ComputeHash($entry.Bytes)).Replace('-','') } finally { $hash.Dispose() }
            $expected = [string]$index.($applicationKinds[$application])
            Assert ($actual -eq (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash) 'database stream contains the actual MSI bytes, including MSI selected from a ZIP'
        }
    }
    }
    Assert (@([SoftwareDatabaseFixture.Database]::Events | Where-Object { $_ -like 'commit:*' }).Count -eq 4) 'overlapping connections publish each namespace only once'
    Assert ($script:progress.Exists([Predicate[string]]{ param($line) $line.Contains('%SYS') -and $line.Contains('übersprungen') })) 'namespaces without zzzSoftware are reported and never modified'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'NovaTouchUpdater') -Directory).Count -eq 0) 'temporary MSI extraction is cleaned after publication'
    $script:results.Clear()
    $script:services = @($posService)
    PublishSelectedSoftwarePackages $selected
    Assert ($script:results.Count -eq 1 -and $script:results[0].Success -and [SoftwareDatabaseFixture.Database]::Entries.Count -eq 16) 'POS novacom.ini alone publishes all selected products without NTC or an Einstellungen folder'
    $script:results.Clear()
    $script:services = @($ntcService,$posService)
    PublishSelectedSoftwarePackages $selected
    Assert ([SoftwareDatabaseFixture.Database]::Entries.Count -eq 16) 'repeated uploads replace setups without creating duplicates'
    [SoftwareDatabaseFixture.Database]::FailTarget = 'fixture.invalid/MANDANT1'
    $script:results.Clear()
    ExpectFailure { PublishSelectedSoftwarePackages $selected } 'a provider failure stops publication and is not returned as success'
    Assert ([SoftwareDatabaseFixture.Database]::Entries.Count -eq 16 -and $script:results.Count -eq 0 -and [SoftwareDatabaseFixture.Database]::Events.Contains('rollback:fixture.invalid/MANDANT1')) 'failed namespace replacement rolls back to the previous complete MSI set'
    [SoftwareDatabaseFixture.Database]::FailTarget = ''
    $ntc.Sha256 = '0' * 64
    ExpectFailure { PublishSelectedSoftwarePackages @($ntc,$pos) } 'changed source checksums stop before any namespace is modified'
    $ntc.Sha256 = (Get-FileHash -LiteralPath $ntc.PayloadPath -Algorithm SHA256).Hash
    $ntc.ProductCode = [guid]::NewGuid().ToString('B')
    ExpectFailure { PublishSelectedSoftwarePackages @($ntc) } 'MSI metadata is checked independently of installed local products'
    $ntc = Package ntc ([string]$index.ntc)
    $script:services = @()
    ExpectFailure { PublishSelectedSoftwarePackages @($pos) } 'missing NTC/POS update services prevent local installation'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'NovaTouchUpdater') -Directory).Count -eq 0) 'temporary MSI extraction is also cleaned on errors'

    # CPF targets determine local destinations, independently of NTC/POS or the
    # provider's namespace enumeration. All %-prefixed names are excluded; USER stays.
    [SoftwareDatabaseFixture.Database]::Entries.Clear(); [SoftwareDatabaseFixture.Database]::Events.Clear()
    $targets = @(
        [pscustomobject]@{ Instance='FIRST'; Directory=$fixture; Port=1972; Namespaces=@('%SYS','%OTHER','%custom',' %PADDED','USER','MANDANT1','EMPTY','user'); User='test'; Password='synthetic-secret'; Ssl=$false },
        [pscustomobject]@{ Instance='SECOND'; Directory=$fixture; Port=51774; Namespaces=@('MANDANT2'); User='test'; Password='synthetic-secret'; Ssl=$false }
    )
    $selected = @($ntc,$pos,$ntv,$oman); $script:results.Clear()
    $queriesBefore = $script:serviceQueries
    PublishLocalIrisSoftwarePackages $selected $targets $fixture
    Assert ($script:lastProviderDirectories.Count -eq 1 -and $script:lastProviderDirectories[0] -eq $fixture -and
        $script:lastProviderFamilies.Count -eq 1 -and $script:lastProviderFamilies[0] -eq 'IRIS') 'local publication loads only the supplied bundled IRIS provider directory, independently of installed drivers'
    Assert ([SoftwareDatabaseFixture.Database]::Entries.Count -eq 12 -and $script:results[0].Success -and $script:serviceQueries -eq $queriesBefore) 'local CPF targets upload all selected MSIs into every eligible namespace on both instance ports without NTC/POS queries'
    Assert (-not [SoftwareDatabaseFixture.Database]::Events.Exists([Predicate[string]]{ param($line) $line.Contains('/%') }) -and
        [SoftwareDatabaseFixture.Database]::Events -contains 'commit:127.0.0.1:1972/USER') 'local upload never connects to percent-prefixed namespaces but includes USER, deduplicates names and uses only loopback'
    Assert ($script:progress.Exists([Predicate[string]]{ param($line) $line.Contains('/EMPTY') -and $line.Contains('übersprungen') })) 'a namespace without zzzSoftware is explicitly reported and skipped'
    PublishLocalIrisSoftwarePackages $selected $targets $fixture
    Assert ([SoftwareDatabaseFixture.Database]::Entries.Count -eq 12) 'local repeat replaces existing setups without duplicates'
    [SoftwareDatabaseFixture.Database]::FailTarget = '127.0.0.1:1972/MANDANT1'
    ExpectFailure { PublishLocalIrisSoftwarePackages $selected $targets $fixture } 'local write failure stops before installation and sanitizes provider secrets'
    Assert ([SoftwareDatabaseFixture.Database]::Entries.Count -eq 12 -and [SoftwareDatabaseFixture.Database]::Events.Contains('rollback:127.0.0.1:1972/MANDANT1')) 'local namespace failure preserves the old complete MSI set with transaction rollback'
    [SoftwareDatabaseFixture.Database]::FailTarget = ''
    $ntc.Sha256 = '0' * 64
    $eventsBefore = [SoftwareDatabaseFixture.Database]::Events.Count
    ExpectFailure { PublishLocalIrisSoftwarePackages $selected $targets $fixture } 'local source checksum failure prevents all database connections'
    Assert ([SoftwareDatabaseFixture.Database]::Events.Count -eq $eventsBefore) 'MSI content is verified before any namespace is opened'
    $ntc.Sha256 = (Get-FileHash -LiteralPath $ntc.PayloadPath -Algorithm SHA256).Hash
    ExpectFailure { PublishLocalIrisSoftwarePackages $selected @($targets[0],$targets[0]) $fixture } 'ambiguous duplicate instance ports are rejected'
    ExpectFailure { PublishLocalIrisSoftwarePackages $selected $targets '' } 'missing bundled provider paths are rejected without searching installed drivers'
    [SoftwareDatabaseFixture.Database]::Events.Clear()
    [SoftwareDatabaseFixture.Database]::FailOpenTarget = '127.0.0.1:51774/MANDANT2'
    ExpectFailure { PublishLocalIrisSoftwarePackages $selected $targets $fixture } 'an inaccessible later instance fails preflight without exposing credentials'
    Assert (-not [SoftwareDatabaseFixture.Database]::Events.Exists([Predicate[string]]{ param($line) $line.StartsWith('begin:') }) -and
        [SoftwareDatabaseFixture.Database]::Entries.Count -eq 12) 'all namespace connections are checked before the first transaction or replacement'
    [SoftwareDatabaseFixture.Database]::FailOpenTarget = ''

    # Dispatch also enforces local-only execution before any remoting or service work.
    $source = [IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'RemoteWorker.ps1'))
    $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$null, [ref]$null)
    $inventory = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ReadSoftwareInventory' }, $true)
    $source = $source.Remove($inventory.Extent.StartOffset, $inventory.Extent.EndOffset - $inventory.Extent.StartOffset).Insert($inventory.Extent.StartOffset,
        'function ReadSoftwareInventory { throw "Database publication must not read uninstall inventory" }')
    $source = $source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 0','return').Replace('exit 1','return')
    $worker = [scriptblock]::Create($source); $script:services = @($ntcService)
    foreach ($local in @($true,$false)) {
        $queriesBefore = $script:serviceQueries; $eventsBefore = [SoftwareDatabaseFixture.Database]::Events.Count
        $script:requestJson = @{ Action='publish-msi-packages'; LocalExecution=$local; MsiPackages=@($ntc,$pos); DatabaseTargets=$targets; DatabaseProviderDirectory=$fixture; Host='fixture.invalid' } | ConvertTo-Json -Depth 8 -Compress
        $writer = [IO.StringWriter]::new(); $previous = [Console]::Out
        try { [Console]::SetOut($writer); & $worker } finally { [Console]::SetOut($previous) }
        $events = @($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        $result = @($events | Where-Object Event -eq 'result')
        Assert ($result.Count -eq 1 -and $result[0].Success -eq $local -and $script:serviceQueries -eq $queriesBefore) 'worker dispatch allows local publication and performs no service or uninstall queries'
        if (-not $local) { Assert ($result[0].Error.Contains('ausschließlich lokal') -and [SoftwareDatabaseFixture.Database]::Events.Count -eq $eventsBefore) 'remote publication is rejected before database connections or WinRM' }
    }
    Write-Host 'PASS SoftwareDatabase tests completed without connecting to a database or running an installer'
} finally {
    $env:TEMP = $originalTemp; $env:TMP = $originalTmp
    if ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
