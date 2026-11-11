. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Assert($value,[string]$message) { if(-not $value) { throw $message }; Write-Host "PASS $message" }
foreach($name in @('SoftwareDatabase','RemoteSoftwareDatabase','SoftwareCompilation')) { . ([scriptblock]::Create((Get-Content (Join-Path $TestScriptsRoot ($name+'.ps1')) -Raw -Encoding UTF8))) }
Add-Type -Path (Join-Path $PSScriptRoot '..\Shared\SoftwareCompilationFake.cs')
$fixture=Join-Path $TestArtifactsRoot ('compile-'+[guid]::NewGuid().ToString('N').Substring(0,8)); [void][IO.Directory]::CreateDirectory($fixture)
$bin=Join-Path $fixture 'Control\bin'; [void][IO.Directory]::CreateDirectory($bin)
foreach($name in @('Aprima.Sys.dll','Aprima.SysDB.dll','Nov.SysDB.dll','Nov.NT.Daten.dll','InterSystems.Data.IRISClient.dll','InterSystems.Data.Bindings.dll')) { [IO.File]::WriteAllText((Join-Path $bin $name),'fixture, never executed') }
$script:programs=@([pscustomobject]@{InstallLocation=(Split-Path $bin);DisplayIcon=('"'+(Join-Path $bin 'Nov.WaWi.exe')+'",0')})
function ReadSoftwareCompilationPrograms { $script:programs }
$script:compilationServices=@(); $script:serviceReads=0
function Get-CimInstance {
    [CmdletBinding()]param($ClassName,$Filter)
    Assert ($ClassName -eq 'Win32_Service' -and $Filter -ceq "Name='novacom ntcontrol updateservice'") 'fallback reads only the exact installed NTC update service on the executing host'
    $script:serviceReads++
    $script:compilationServices
}
Assert ((FindSoftwareCompilationDirectory) -eq $bin) 'installed Control is located from registration without Win32_Product or MSI repair'
Assert ($serviceReads -eq 0) 'a valid registry bin path does not require service discovery'
$script:programs=@(); $rejected=$false; try { FindSoftwareCompilationDirectory } catch { $rejected=$_.Exception.Message -match 'novacom ntcontrol updateservice' }
Assert $rejected 'missing installed Control gives a useful error before opening a database'
$serviceBin=Join-Path $fixture 'Control im Dienstpfad\bin'; [void][IO.Directory]::CreateDirectory($serviceBin)
foreach($name in @('Aprima.Sys.dll','Aprima.SysDB.dll','Nov.SysDB.dll','Nov.NT.Daten.dll','InterSystems.Data.IRISClient.dll','InterSystems.Data.Bindings.dll')) { [IO.File]::WriteAllText((Join-Path $serviceBin $name),'service fixture, never executed') }
$script:compilationServices=@([pscustomobject]@{Name='novacom ntcontrol updateservice';PathName=('"'+(Join-Path $serviceBin 'Control.UpdateService.exe')+'" --service --ignored="C:\wrong\bin"');State='Stopped';StartMode='Disabled'})
Assert ((FindSoftwareCompilationDirectory) -eq $serviceBin) 'missing registry paths use the stopped or disabled service EXE directory directly, without appending another bin'
$script:programs=@([pscustomobject]@{InstallLocation='';DisplayIcon=''})
Assert ((FindSoftwareCompilationDirectory) -eq $serviceBin) 'a registered Control without InstallLocation or DisplayIcon is discovered through its installed service'
$script:programs=@([pscustomobject]@{InstallLocation=(Join-Path $fixture 'stale install');DisplayIcon=('"'+(Join-Path $fixture 'installer-cache\icon.exe')+'",0')})
$script:compilationServices[0].PathName=(Join-Path $serviceBin 'Control.UpdateService.exe')+' --service'
Assert ((FindSoftwareCompilationDirectory) -eq $serviceBin) 'stale registry paths and cached icons fall back to an unquoted service path with spaces'
$script:compilationServices[0].PathName='relative\Control.UpdateService.exe --service'
$rejected=$false; try { FindSoftwareCompilationDirectory } catch { $rejected=$_.Exception.Message -match 'nicht absolut' }
Assert $rejected 'relative service paths cannot redirect DLL discovery into the current working directory'
$script:compilationServices=@()
$script:programs=@([pscustomobject]@{InstallLocation=(Split-Path $bin);DisplayIcon=''})
Remove-Item -LiteralPath (Join-Path $bin 'Aprima.SysDB.dll')
$rejected=$false; try { FindSoftwareCompilationDirectory } catch { $rejected=$_.Exception.Message -match 'Aprima.SysDB.dll' }
Assert $rejected 'missing dependency is reported by exact DLL name before database work'
[IO.File]::WriteAllText((Join-Path $bin 'Aprima.SysDB.dll'),'fixture')
Remove-Item -LiteralPath (Join-Path $bin 'Nov.SysDB.dll')
$rejected=$false; try { FindSoftwareCompilationDirectory } catch { $rejected=$_.Exception.Message -match 'Nov.SysDB.dll' }
Assert $rejected 'the additional Novacom system assembly is required before opening any database'
[IO.File]::WriteAllText((Join-Path $bin 'Nov.SysDB.dll'),'fixture')

$script:messages=[Collections.Generic.List[string]]::new()
$script:progressEvents=[Collections.Generic.List[object]]::new()
function Progress {
    param($phase,$message,$percent)
    $script:messages.Add($phase+': '+$message)
    $script:progressEvents.Add([pscustomobject]@{Phase=$phase;Message=$message;Percent=$percent})
}
function Emit { throw 'Compilation must finish without interactive events or confirmation input.' }
$script:compilationResources=$null
function InitializeSoftwareCompilationRuntime {
    param($Directory)
    $data=[CompilationFixture.Data]::new()
    $resources=@(); foreach($name in @('Novacom-NT-Daten.xml','Novacom-NT-BOH.xml','Novacom-NT-POS.xml','Novacom-NT-POS-API.xml','Novacom-NT-QRY.xml')) {
        $resources += NewSoftwareCompilationEmbeddedResource $data $name
    }
    if($null -ne $script:compilationResources) { $resources=@($script:compilationResources) }
    [pscustomobject]@{ Resolver=[CompilationFixture.Resolver]::new(); Provider=[pscustomobject]@{BinaryStreamType=[CompilationFixture.Binary];ObjectType=[CompilationFixture.ServerFile]}; Data=$data;
        Cache=[CompilationFixture.Cache]; Update=[CompilationFixture.Update]; Proxy=[CompilationFixture.Proxy]; GlobalImport=[CompilationFixture.GlobalImport]; VersionType=[CompilationFixture.VersionObject];
        VersionObject=[CompilationFixture.VersionObject].GetField('obj',[Reflection.BindingFlags]'Instance,NonPublic'); Resources=$resources }
}
function OpenSoftwareDatabase { param($Provider,$Definition,$Namespace) [CompilationFixture.Connection]::new($Namespace) }
$targets=@([pscustomobject]@{Instance='IRIS';Port=1972;User='test';Password='synthetic-secret';Ssl=$false;Namespaces=@('%SYS','USER','PROG-DEV')})
[CompilationFixture.State]::Reset(); $result=CompileSoftwarePackages $targets
Assert ($result.Success -and $result.Changed -and $result.SoftwareCompilationResults.Count -eq 2 -and [CompilationFixture.State]::Disposed -eq 2) 'all non-percent namespaces, including PROG-DEV, compile using separate connections'
$expected=@('Novacom-NT-Daten.xml','Novacom-NT-BOH.xml','Novacom-NT-POS.xml','Novacom-NT-POS-API.xml','Novacom-NT-QRY.xml')
foreach($namespace in @('USER','PROG-DEV')) {
    $loads=@([CompilationFixture.State]::Calls | Where-Object { $_ -like ($namespace+':load:*') } | ForEach-Object { $_.Substring(($namespace+':load:').Length) })
    Assert (($loads -join ',') -ceq ($expected -join ',')) ('five resources are imported in the required order in '+$namespace)
    foreach($name in $expected) {
        $updates=@($script:progressEvents | Where-Object { $_.Phase -eq 'Softwarepaket kompilieren' -and $_.Message.StartsWith('IRIS:1972/'+$namespace+' '+[char]0xB7+' ') -and $_.Message.EndsWith(' '+[char]0xB7+' '+$name) })
        Assert ($updates.Count -eq 4 -and ($updates.Percent -join ',') -eq '0,60,60,100' -and $updates[0].Message.Contains('Klasse 1 von 3') -and
            $updates[1].Message.Contains('Klasse 3 von 3') -and $updates[2].Message.Contains('Routine 1 von 2') -and
            $updates[3].Message.Contains('Routine 2 von 2') -and ($updates.Message -join ' ') -notmatch 'Compilerflags|display=|checkuptodate=') ('readable class and routine counts share one XML progress bar: '+$name+' / '+$namespace)
    }
}
$calls=[CompilationFixture.State]::Calls; $compiledItems=[CompilationFixture.State]::CompiledItems
Assert (@($compiledItems | Where-Object { $_ -match ':Test.Current' }).Count -eq 10 -and
    @($compiledItems | Where-Object { $_ -match ':Test.Include' }).Count -eq 0 -and
    $compiledItems.IndexOf('USER:Novacom-NT-Daten.xml:Test.Class.cls') -lt $compiledItems.IndexOf('USER:Novacom-NT-Daten.xml:Test.Routine.mac')) 'explicit current classes are rebuilt, includes are skipped and classes compile before routines'
Assert ([CompilationFixture.State]::CurrencyCalls -eq 0 -and [CompilationFixture.State]::CompileCalls -eq 20 -and
    @($calls | Where-Object { $_ -like '*:compile:*' -and $_ -notlike '*/checkuptodate=expandedonly' }).Count -eq 0) 'manual compilation rebuilds all explicit classes and routines in bounded lists, regardless of current namespace defaults'
Assert (@($script:softwareCompilationMethods.Keys | Where-Object { $_ -like '*|OBJCompileList|4' }).Count -eq 1) 'reflection metadata is resolved once and reused across resources and namespaces'
Assert (@($calls | Where-Object { $_ -like '*:save:*' }).Count -eq 10 -and @($calls | Where-Object { $_ -like '*:servicepack-reset' }).Count -eq 10 -and -not [CompilationFixture.State]::Locked) 'successful resources save zzzUpdate, reset ServicePack XML metadata and release exclusive locks'
[CompilationFixture.State]::Reset()
$runtime=InitializeSoftwareCompilationRuntime $bin; $connection=[CompilationFixture.Connection]::new('RESULT')
[CompilationFixture.Cache]::Connection=$connection
try {
    $outcomes=@(ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/RESULT')
    Assert ($outcomes.Count -eq 1 -and $outcomes[0].Imported -and -not $outcomes[0].Skipped) 'a successful Rewind status is consumed and the import returns exactly one structured outcome'
    $outcomes=@(ImportSoftwareCompilationResource $runtime $connection $runtime.Resources[0] 'IRIS:1972/RESULT')
    Assert ($outcomes.Count -eq 1 -and $outcomes[0].Imported -and -not $outcomes[0].Skipped -and [CompilationFixture.State]::CompileCalls -eq 4) 'an already applied resource is imported and recompiled again on an explicit run, returning one structured outcome'
} finally { [CompilationFixture.Cache]::Connection=$null; $connection.Dispose(); $runtime.Resolver.Dispose() }
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailRewind=$true; $result=CompileSoftwarePackages $targets
Assert (-not $result.Success -and -not $result.Changed -and [CompilationFixture.State]::Versions.Count -eq 0 -and
    @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:transfer:*' -or $_ -like '*:load:*' }).Count -eq 0 -and
    -not [CompilationFixture.State]::Locked -and [CompilationFixture.State]::Disposed -eq 2) 'a failed Rewind status prevents upload and import, reports an error and releases every connection and lock'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::AlreadyCurrent=$true; $result=CompileSoftwarePackages $targets
Assert ($result.Success -and $result.Changed -and @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:load:*' }).Count -eq 10 -and
    [CompilationFixture.State]::CompileCalls -eq 20) 'current XML and server versions no longer suppress a requested compilation'
Assert (@([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Test.Current.cls' }).Count -eq 0 -and
    @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Test.Class.cls' }).Count -eq 10 -and
    @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Test.Routine.mac' }).Count -eq 10 -and
    [CompilationFixture.State]::CurrencyCalls -eq 0 -and
    @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:compile:*.cls:*/checkuptodate=all' }).Count -eq 10) 'same-engine runs skip current classes inside the compiler while stale classes and all routines still compile without individual currency queries'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::AlreadyCurrent=$true; [CompilationFixture.State]::Fallback=$true
$result=CompileSoftwarePackages $targets
Assert ($result.Success -and @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:frvk/display=none/checkuptodate=all' }).Count -eq 10 -and
    @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Test.Current.cls' }).Count -eq 0) 'deployed-list retry preserves the server-side current-class check'
[CompilationFixture.State]::Reset()
foreach ($namespace in @('USER','PROG-DEV')) {
    foreach ($name in $expected) {
        $previousEngine=[CompilationFixture.Record]::new(); $previousEngine.Version='<Export ts="2026-11-05 10:00:00">'; $previousEngine.dbversion='2021.1'
        [CompilationFixture.State]::Versions[$namespace+'/'+$name]=$previousEngine
    }
}
$result=CompileSoftwarePackages $targets
Assert ($result.Success -and @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Test.Current.cls' }).Count -eq 10) 'an engine-version change forces explicit classes to rebuild even if their ordinary currency check would pass'
Assert (-not (TestSoftwareCompilationNeeded ([pscustomobject]@{Version='<Export ts="2027-01-01 00:00:00">';dbversion='2022.1'}) ([pscustomobject]@{Version='<Export ts="2026-11-05 10:00:00">';Kind='Classes';NoDowngrade=$true}))) 'newer database timestamps prevent a downgrade'
Assert (TestSoftwareCompilationNeeded ([pscustomobject]@{Version='<Export ts="2026-11-05 10:00:00">';dbversion='2021.1'}) ([pscustomobject]@{Version='<Export ts="2026-11-05 10:00:00">';Kind='Classes';NoDowngrade=$true})) 'an updated IRIS server version also recompiles'
[CompilationFixture.State]::Reset()
foreach($namespace in @('USER','PROG-DEV')) {
    foreach($name in $expected) {
        $newer=[CompilationFixture.Record]::new(); $newer.Version='<Export ts="2027-01-01 00:00:00">'; $newer.dbversion='2022.1'
        [CompilationFixture.State]::Versions[$namespace+'/'+$name]=$newer
    }
}
$result=CompileSoftwarePackages $targets
Assert ($result.Success -and -not $result.Changed -and [CompilationFixture.State]::CompileCalls -eq 0 -and
    @($result.SoftwareCompilationResults | Where-Object { $_.SkippedResources -ne 5 }).Count -eq 0) 'an explicit rebuild still protects newer database resources from older XML imports'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::Fallback=$true; $result=CompileSoftwarePackages $targets
Assert ($result.Success -and @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:frvk/display=none*' }).Count -eq 20 -and
    [CompilationFixture.State]::CompileCalls -eq 40 -and [CompilationFixture.State]::CompiledItems.Count -eq 50) 'deployed status 5380 retries whole lists with frvk while preserving every explicit class and routine'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailBatches=$true; $result=CompileSoftwarePackages $targets
Assert ($result.Success -and [CompilationFixture.State]::Versions.Count -eq 10 -and [CompilationFixture.State]::CurrencyCalls -eq 0) 'a failed list is divided until every explicit item can compile without skipping current classes'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailItem='Test.Class.cls'; $result=CompileSoftwarePackages $targets
Assert (-not $result.Success -and $result.Changed -and [CompilationFixture.State]::Versions.Count -eq 0 -and -not [CompilationFixture.State]::Locked -and
    $result.SoftwareCompilationResults[0].CompilationErrors.Count -eq 5 -and $result.SoftwareCompilationResults[0].ImportedResources -eq 5 -and
    @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Test.Routine.mac' }).Count -eq 10 -and
    @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Test.After.cls' }).Count -eq 10) 'compiler errors are collected without confirmation, retain details, continue every resource, class and routine, avoid success stamps and release the lock'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailItem='Test.Class.cls'; [CompilationFixture.State]::FailCompileResource='Novacom-NT-POS.xml'; $result=CompileSoftwarePackages $targets
Assert (-not $result.Success -and [CompilationFixture.State]::Versions.Count -eq 8 -and $result.SoftwareCompilationResults[0].CompilationErrors.Count -eq 1 -and
    [CompilationFixture.State]::Versions.ContainsKey('USER/Novacom-NT-QRY.xml')) 'POS compiler errors are also shown in the overview while later resources finish and successful versions are saved'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailItem='Test.Class.cls'; [CompilationFixture.State]::FailCompileResource='Novacom-NT-BOH.xml'
[CompilationFixture.State]::CompileErrorMessage="ERROR #5272: Cannot change final 'Method': 'GetScheduledRunTime' > ERROR #5030: An error occurred while compiling class 'cmNT.IFC.Common.TaskDefinition' synthetic-secret"
$result=CompileSoftwarePackages $targets
$compilerFailures=@($result.SoftwareCompilationResults | ForEach-Object { $_.CompilationErrors })
Assert ($compilerFailures.Count -eq 2 -and $compilerFailures[0].Item -ceq 'cmNT.IFC.Common.TaskDefinition' -and
    $compilerFailures[0].Namespace -ceq 'PROG-DEV' -and $compilerFailures[0].Details -match '#5272' -and
    $compilerFailures[0].Details -notmatch 'synthetic-secret' -and [CompilationFixture.State]::Versions.Count -eq 8 -and
    [CompilationFixture.State]::Versions.ContainsKey('PROG-DEV/Novacom-NT-QRY.xml')) 'final-method errors identify the actual descendant class, redact credentials and continue through QRY in all namespaces'
[CompilationFixture.State]::AllClassesCurrent=$true; [CompilationFixture.State]::Versions.Clear()
$result=CompileSoftwarePackages $targets
$compilerFailures=@($result.SoftwareCompilationResults | ForEach-Object { $_.CompilationErrors })
Assert ($compilerFailures.Count -eq 2 -and [CompilationFixture.State]::Versions.Count -eq 8 -and [CompilationFixture.State]::CurrencyCalls -eq 0) 'a failed descendant remains in the overview and current parents are not skipped during the individual retry'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailItem='Test.Routine.mac'; [CompilationFixture.State]::FailCompileResource='Novacom-NT-BOH.xml'
$result=CompileSoftwarePackages $targets
$compilerFailures=@($result.SoftwareCompilationResults | ForEach-Object { $_.CompilationErrors })
Assert ($compilerFailures.Count -eq 2 -and $compilerFailures[0].ItemType -ceq 'Routine' -and
    $compilerFailures[0].Item -ceq 'Test.Routine.mac' -and [CompilationFixture.State]::Versions.Count -eq 8 -and
    @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Novacom-NT-BOH.xml:Test.AfterRoutine.mac' }).Count -eq 2) 'routine errors are distinguished from class errors and do not prevent subsequent routines and packages'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailNamespace='PROG-DEV'; $result=CompileSoftwarePackages $targets
Assert (-not $result.Success -and @($result.SoftwareCompilationResults | Where-Object Success).Count -eq 1 -and $result.Error -notmatch 'synthetic-secret' -and -not [CompilationFixture.State]::Locked) 'one namespace failure does not block independent namespaces and credentials are redacted'
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::DenyLock=$true; $result=CompileSoftwarePackages $targets
Assert (-not $result.Success -and -not $result.Changed -and @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:delete:*' }).Count -eq 0) "a denied lock never deletes another importer's Import.xml"
[CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailSave=$true; $result=CompileSoftwarePackages $targets
Assert (-not $result.Success -and @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:servicepack-reset' }).Count -eq 0) 'failed version saving is reported without a dialog or ServicePack metadata reset'

# The complete startup set: three assemblies and data files beside bin.
$dataDirectory=Join-Path (Split-Path $bin) 'Daten'; [void][IO.Directory]::CreateDirectory($dataDirectory)
function WriteCompilationGlobalFixture([string]$Name,[string]$Timestamp='2026-11-05 10:00:00') {
    $unicodeText=([string][char]0xC4)+'Test'
    $xml="<?xml version=`"1.0`" encoding=`"UTF-8`"?>`n<Export ts=`"$Timestamp`">`n<!-- Resource:$Name -->`n<Global><Node><Sub>^Fixture</Sub><Data><![CDATA[$unicodeText"+('x'*70000)+"]]></Data></Node></Global></Export>"
    [IO.File]::WriteAllText((Join-Path $dataDirectory $Name),$xml,[Text.UTF8Encoding]::new($false))
}
WriteCompilationGlobalFixture 'Aprima-Systemparameter.xml'
WriteCompilationGlobalFixture 'bls-v301-sp1.xml'
$aprima=[CompilationFixture.Data]::new('Aprima.SysDB'); $systemData=[CompilationFixture.Data]::new('Nov.SysDB'); $ntData=[CompilationFixture.Data]::new()
$bundle=@(GetSoftwareCompilationResources $aprima $systemData $ntData $bin)
$allExpected=@('Aprima-Datentypen.xml','Aprima-System.xml','Aprima-System-PLZ-D.xml','Aprima-Systemparameter.xml','Novacom-System.xml','bls-v301-sp1.xml')+$expected
Assert ($bundle.Count -eq 11 -and ($bundle.Name -join ',') -ceq ($allExpected -join ',') -and
    $bundle[0].Manifest -ceq 'AP.SysDB.Aprima-Datentypen.xml' -and $bundle[0].Table -ceq 'Aprima-Datentypen.xml') 'the Datentypen resource is prepared first with its own name and version key'
Assert ($bundle[3].Path -ceq (Join-Path $dataDirectory 'Aprima-Systemparameter.xml') -and
    $bundle[3].Table -ceq '..\Daten\Aprima-Systemparameter.xml' -and
    $bundle[5].Path -ceq (Join-Path $dataDirectory 'bls-v301-sp1.xml') -and $bundle[5].Table -ceq '..\Daten\bls-v301-sp1.xml' -and $bundle[5].Once) 'external globals keep their original paths and version keys, with BLS once-only after Novacom-System and before NT-Daten'
Remove-Item -LiteralPath $bundle[5].Path
[CompilationFixture.State]::Reset(); $rejected=$false
try { $null=@(GetSoftwareCompilationResources $aprima $systemData $ntData $bin) } catch { $rejected=$_.Exception.Message -match 'bls-v301-sp1.xml' }
Assert ($rejected -and [CompilationFixture.State]::Calls.Count -eq 0) 'missing BLS is rejected before any database changes'
WriteCompilationGlobalFixture 'bls-v301-sp1.xml'
Remove-Item -LiteralPath $bundle[3].Path
$wrongDataDirectory=Join-Path $bin 'Daten'; [void][IO.Directory]::CreateDirectory($wrongDataDirectory)
[IO.File]::WriteAllText((Join-Path $wrongDataDirectory 'Aprima-Systemparameter.xml'),'wrong directory, must not be read')
[CompilationFixture.State]::Reset(); $rejected=$false
try { $null=@(GetSoftwareCompilationResources $aprima $systemData $ntData $bin) } catch { $rejected=$_.Exception.Message -match 'Aprima-Systemparameter.xml' }
Assert ($rejected -and [CompilationFixture.State]::Calls.Count -eq 0 -and [CompilationFixture.State]::Disposed -eq 0) 'a missing required Daten file is rejected before DB access even if a misleading bin/Daten file exists'
WriteCompilationGlobalFixture 'Aprima-Systemparameter.xml'
try {
    $script:compilationResources=@(GetSoftwareCompilationResources $aprima $systemData $ntData $bin)
    [CompilationFixture.State]::Reset()
    foreach($namespace in @('USER','PROG-DEV')) {
        $legacy=[CompilationFixture.Record]::new(); $legacy.Application='Aprima.SysDB'; $legacy.Tabelle='Aprima-Daten.xml'; $legacy.Version='legacy data version'
        [CompilationFixture.State]::Versions[$namespace+'/Aprima-Daten.xml']=$legacy
    }
    $script:progressEvents.Clear()
    $result=CompileSoftwarePackages $targets
    Assert ($result.Success -and $result.SoftwareCompilationResults[0].ImportedResources -eq 11 -and [CompilationFixture.State]::Versions.Count -eq 24 -and
        [CompilationFixture.State]::Versions['USER/Aprima-Daten.xml'].Version -ceq 'legacy data version' -and
        [CompilationFixture.State]::Versions['USER/Aprima-Datentypen.xml'].Version -match '2026-11-05') 'the datatype package gets its own version row even when a legacy Aprima-Daten row already exists, without overwriting that legacy row'
    foreach($namespace in @('USER','PROG-DEV')) {
        $steps=@([CompilationFixture.State]::Calls | Where-Object { $_ -match ('^'+[regex]::Escape($namespace)+':(load|globals):') } | ForEach-Object { $_ -replace ('^'+[regex]::Escape($namespace)+':(load|globals):'),'' })
        Assert (($steps -join ',') -ceq ($allExpected -join ',')) ('six startup packages including BLS precede all five NT packages in their original order in '+$namespace)
        foreach($resource in $bundle) {
            $updates=@($script:progressEvents | Where-Object { $null -ne $_.Percent -and $_.Message.StartsWith('IRIS:1972/'+$namespace+' '+[char]0xB7+' ') -and $_.Message.EndsWith(' '+[char]0xB7+' '+$resource.Name) })
            Assert ($updates.Count -ge 2 -and $updates[0].Percent -eq 0 -and $updates[-1].Percent -eq 100) ('every XML has its own progress from start to finish: '+$resource.Name+' / '+$namespace)
        }
    }
    Assert ([CompilationFixture.State]::Versions['USER/Aprima-Datentypen.xml'].Application -ceq 'Aprima.SysDB' -and
        [CompilationFixture.State]::Versions['USER/Aprima-System.xml'].Application -ceq 'Aprima.SysDB' -and
        [CompilationFixture.State]::Versions['USER/Aprima-System-PLZ-D.xml'].Application -ceq 'Aprima.SysDB' -and
        [CompilationFixture.State]::Versions['USER/Novacom-System.xml'].Application -ceq 'Nov.SysDB' -and
        [CompilationFixture.State]::Versions['USER/..\Daten\Aprima-Systemparameter.xml'].Application -ceq 'Nov.WaWi' -and
        [CompilationFixture.State]::Versions['USER/..\Daten\bls-v301-sp1.xml'].Application -ceq 'Nov.WaWi') 'every startup package stores the exact Application and Tabelle association requested for zzzUpdate'
    Assert (@([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:globals:*' }).Count -eq 6 -and
        [CompilationFixture.State]::CompileCalls -eq 32 -and [CompilationFixture.State]::FileCopies -eq 4 -and [CompilationFixture.State]::LegacyWrites -eq 0 -and
        @($script:messages | Where-Object { $_ -like '*bls-v301-sp1.xml*Datei auf Zielserver*0 Schreibaufrufe' }).Count -eq 2) 'BLS and system parameters copy on the server without TCP upload, while only the eight class packages reach the compiler'
    $result=CompileSoftwarePackages $targets
    Assert ($result.Success -and $result.SoftwareCompilationResults[0].ImportedResources -eq 8 -and $result.SoftwareCompilationResults[0].SkippedResources -eq 3 -and
        @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:globals:*' }).Count -eq 6 -and [CompilationFixture.State]::FileCopies -eq 4) 'repeat runs skip once-only PLZ/BLS and unchanged parameters before copying or importing their data again'
    WriteCompilationGlobalFixture 'Aprima-Systemparameter.xml' '2027-01-01 00:00:00'
    $script:compilationResources=@(GetSoftwareCompilationResources $aprima $systemData $ntData $bin)
    $result=CompileSoftwarePackages $targets
    Assert ($result.Success -and $result.SoftwareCompilationResults[0].ImportedResources -eq 9 -and
        [CompilationFixture.State]::Versions['USER/..\Daten\Aprima-Systemparameter.xml'].Version -match '2027-01-01') 'changed external system parameters are reimported and receive the version header from the actual file'
    $withSeparateData=@(GetSoftwareCompilationResources ([CompilationFixture.Data]::new('Aprima.SysDB',$true)) $systemData $ntData $bin)
    Assert ($withSeparateData.Count -eq 12 -and $withSeparateData[0].Name -ceq 'Aprima-Datentypen.xml' -and $withSeparateData[1].Name -ceq 'Aprima-Daten.xml' -and
        $withSeparateData[0].Manifest -cne $withSeparateData[1].Manifest) 'a DLL containing both real resources keeps Daten and Datentypen as independent packages'
    [CompilationFixture.State]::Reset(); $script:compilationResources=@($withSeparateData[0],$withSeparateData[1]); $result=CompileSoftwarePackages $targets
    Assert ($result.Success -and [CompilationFixture.State]::Versions.Count -eq 4 -and
        @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Aprima-Datentypen.xml:*' }).Count -gt 0 -and
        @([CompilationFixture.State]::CompiledItems | Where-Object { $_ -like '*:Aprima-Daten.xml:*' }).Count -gt 0) 'when both resources exist each is imported and compiled under its own version key'
    [CompilationFixture.State]::Reset(); $script:messages.Clear(); $script:compilationResources=@((NewSoftwareCompilationFileResource $bin 'Aprima-Systemparameter.xml')); $result=CompileSoftwarePackages $targets
    Assert ($result.Success -and [CompilationFixture.State]::Versions.Count -eq 2 -and [CompilationFixture.State]::CompileCalls -eq 0 -and
        @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:globals:Aprima-Systemparameter.xml' }).Count -eq 2 -and
        @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:load:*' -or $_ -like '*:compile:*' -or $_ -like '*:purge' }).Count -eq 0 -and
        @($script:messages | Where-Object { $_ -like '*Aprima-Systemparameter.xml*ohne Kompilierung*' }).Count -eq 2) 'system parameters are imported and versioned in every selected namespace with no compiler call; the log explicitly identifies import-only processing'
    [CompilationFixture.State]::Reset(); $script:messages.Clear(); $script:compilationResources=@($bundle[5]); $result=CompileSoftwarePackages $targets
    Assert ($result.Success -and [CompilationFixture.State]::Versions.Count -eq 2 -and [CompilationFixture.State]::CompileCalls -eq 0 -and
        @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:globals:bls-v301-sp1.xml' }).Count -eq 2 -and
        @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:load:*' -or $_ -like '*:compile:*' -or $_ -like '*:purge' }).Count -eq 0 -and
        @($script:messages | Where-Object { $_ -like '*bls-v301-sp1.xml*ohne Kompilierung*' }).Count -eq 2) 'BLS is imported and versioned in every selected namespace without compilation or SQL purge'
    [CompilationFixture.State]::Reset(); [CompilationFixture.State]::FailGlobals=$true
    $script:compilationResources=@($bundle[2]); $result=CompileSoftwarePackages $targets
    Assert (-not $result.Success -and $result.Changed -and [CompilationFixture.State]::Versions.Count -eq 0 -and
        $result.Error -notmatch 'synthetic-secret' -and -not [CompilationFixture.State]::Locked -and
        @([CompilationFixture.State]::Calls | Where-Object { $_ -like '*:delete:*' -or $_ -like '*:load:*' -or $_ -like '*:compile:*' }).Count -eq 0) 'a global import error is reported without a success stamp or compiler call and never deletes a temp XML it did not create'
    [CompilationFixture.State]::Reset(); $script:compilationResources=@($bundle[3]); $result=CompileSoftwarePackages $targets
    Assert (-not $result.Success -and -not $result.Changed -and [CompilationFixture.State]::Versions.Count -eq 0 -and -not [CompilationFixture.State]::Locked) 'external XML header changes after preflight are rejected before any partial global import'
} finally { $script:compilationResources=$null }

[CompilationFixture.State]::Reset(); $targets[0].Namespaces=@('%SYS','%OTHER'); $result=CompileSoftwarePackages $targets
Assert ($result.Success -and $result.Skipped -and [CompilationFixture.State]::Calls.Count -eq 0) 'system namespaces cannot reach the Aprima runtime'
$script:copies=0; $script:removed=0; $script:childFails=$false; $script:childRequests=@()
$workerFile=Join-Path $fixture 'worker.ps1'; [IO.File]::WriteAllText($workerFile,'fixture worker, never executed')
function TestLocalSession($Session) { $Session -eq 'local' }
function NewRemotePackageStage($Session) {
    $path=Join-Path $fixture ('remote-'+[guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($path)
    [pscustomobject]@{Path=$path}
}
function RemoveRemotePackageStage($Session,$Stage) {
    $full=[IO.Path]::GetFullPath([string]$Stage.Path)
    if ([IO.Path]::GetDirectoryName($full) -ne $fixture -or [IO.Path]::GetFileName($full) -notmatch '^remote-[a-f0-9]{32}$') { throw 'Unexpected test cleanup target' }
    Remove-Item -LiteralPath $full -Recurse -Force; $script:removed++
}
function CopySoftwareDatabaseFile($Session,$Source,$Target,$Hash,$Label) {
    Assert ($Source -eq $workerFile -and [IO.Path]::GetFileName($Target) -eq 'Worker.ps1') 'remote compilation transfers only its worker; installed DLLs stay on the target'
    $script:copies++; Copy-Item -LiteralPath $Source -Destination $Target
}
function InvokeSoftwareDatabaseChildOperation($Session,$Worker,$Request) {
    Assert (Test-Path -LiteralPath $Worker -PathType Leaf) 'the compilation worker exists before the child process is requested'
    $script:childRequests+=,$Request
    if ($script:childFails) { throw 'simulated worker start failure' }
    [pscustomobject]@{Event='result';Success=$false;Changed=$true;Message='partial failure';Error='compiler error';SoftwareCompilationResults=@([pscustomobject]@{Instance='IRIS';Namespace='USER';Success=$false;ImportedResources=2;SkippedResources=0;Error='compiler error'})}
}
$request=[pscustomobject]@{DatabaseTargets=$targets}
$result=InvokeSoftwareCompilation $request 'local' $workerFile
Assert ($result -is [hashtable] -and -not $result.Success -and $result.Changed -and $result.SoftwareCompilationResults.Count -eq 1 -and $copies -eq 0 -and $removed -eq 0) 'local compilation retains partial namespace outcomes and uses the original worker without copying'
$result=InvokeSoftwareCompilation $request 'remote' $workerFile
Assert ($copies -eq 1 -and $removed -eq 1 -and $childRequests[-1].LocalExecution -and $childRequests[-1].SoftwareCompilationChild -and
    $childRequests[-1].Action -eq 'compile-software-packages' -and $childRequests[-1].DatabaseTargets.Count -eq $targets.Count -and $result.Changed) 'remote compilation stages one worker, forwards confirmed IRIS targets to its child and removes the stage after partial failure'
$script:childFails=$true; $rejected=$false
try { InvokeSoftwareCompilation $request 'remote' $workerFile | Out-Null } catch { $rejected=$_.Exception.Message -match 'simulated worker' }
Assert ($rejected -and $removed -eq 2) 'remote compilation also removes its private stage if child startup fails'
Write-Host 'Software compilation tests passed. No real databases or installed DLLs were executed.'
