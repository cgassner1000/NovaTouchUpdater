. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert($condition,[string]$message) { if (-not $condition) { throw $message }; Write-Output ('PASS '+$message) }
foreach ($module in @('SoftwareDatabase','RemoteSoftwareDatabase','ServicePacks')) {
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot ($module+'.ps1')))))
}
Add-Type -Path (Join-Path $PSScriptRoot '..\Shared\ServicePackDatabaseFake.cs')
function Progress { param($Phase,$Message,$Percent) }
function InitializeSoftwareDatabaseProvider { param($Directories,$Families)
    [pscustomobject]@{ Fixture=$true }
}
function OpenSoftwareDatabase { param($Provider,$Definition,$Namespace) [ServicePackFixture.Connection]::new($Namespace) }
$root=Join-Path $TestArtifactsRoot ('servicepacks-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$files=@()
$unicode=[char]::ConvertFromUtf32(0x1f389)+' Gr'+[char]0x00fc+[char]0x00df+'e'
$prefix='<Export><Class name="User.ServicePack"><Description>'
$classXml=$prefix+('A'*(8191-$prefix.Length))+$unicode+'</Description></Class></Export>'
foreach ($entry in @(@('classes.xml',$classXml,'User.ServicePack.cls'),
    @('routines.xml','<Export><Routine name="ServicePack.mac" type="MAC"><![CDATA[ServicePack ; test]]></Routine></Export>','ServicePack.mac'))) {
    $path=Join-Path $root $entry[0]; [IO.File]::WriteAllText($path,$entry[1],[Text.UTF8Encoding]::new($false))
    $files += [pscustomobject]@{ Path=$path; Name=$entry[0]; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; Items=@($entry[2]) }
}
$targets=@([pscustomobject]@{ Instance='IRIS'; Port=1972; Namespaces=@('USER','POS','%SYS','%SYSTEM','USER'); User='_SYSTEM'; Password='synthetic-secret'; Ssl=$true },
    [pscustomobject]@{ Instance='SECOND'; Port=1973; Namespaces=@('APP'); User='_SYSTEM'; Password='synthetic-secret'; Ssl=$false })
[ServicePackFixture.Database]::Reset()
$result=ImportServicePacks $files $targets 'fake-provider'
Assert ($result.Success -and $result.ServicePackResults.Count -eq 3 -and @($result.ServicePackResults | Where-Object { -not $_.Compiled }).Count -eq 0) 'all application namespaces on every instance receive all XMLs and compile successfully'
foreach ($ns in @('USER','POS','APP')) {
    $events=@([ServicePackFixture.Database]::Events | Where-Object { $_.StartsWith($ns+'|') })
    Assert ($events.Count -eq 4 -and $events[0] -like '*|load|*' -and $events[1] -eq ($ns+'|compile|classes.xml|fck') -and
        $events[2] -like '*|load|*' -and $events[3] -eq ($ns+'|compile|routines.xml|fck')) 'each XML loads and compiles with fck/display=none, without a separate CompileList call'
}
Assert (@([ServicePackFixture.Database]::Events | Where-Object { $_.StartsWith('%') }).Count -eq 0) 'percent namespaces are never connected or imported'
Assert ([ServicePackFixture.Database]::Disposed -eq 3 -and [ServicePackFixture.Database]::LoadPaths.Count -eq 6 -and
    @([ServicePackFixture.Database]::LoadPaths | Where-Object { $_ -notin $files.Path }).Count -eq 0) 'local Load receives the existing XML paths directly, without IRIS file or stream operations, and disposes every connection'
Assert (@([ServicePackFixture.Database]::Events | Where-Object { $_.Contains($unicode) }).Count -eq 3) 'direct XML loading preserves Unicode content'
Assert (@($files | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256 }).Count -eq 0) 'selected XML files remain unchanged on disk'

[ServicePackFixture.Database]::Reset(); [ServicePackFixture.Database]::FailCompile='POS'
$result=ImportServicePacks $files $targets 'fake-provider'
Assert (-not $result.Success -and @($result.ServicePackResults | Where-Object Success).Count -eq 2 -and $result.Error -match 'compiler error') 'compiler errors remain per namespace while independent namespaces finish'
[ServicePackFixture.Database]::Reset(); [ServicePackFixture.Database]::FailLoad='POS'
$result=ImportServicePacks $files $targets 'fake-provider'
Assert (-not $result.Success -and @($result.ServicePackResults | Where-Object Success).Count -eq 2 -and $result.Error -notmatch 'synthetic-secret' -and
    @([ServicePackFixture.Database]::Events | Where-Object { $_ -like 'POS|compile|*' }).Count -eq 0) 'a failed XML load stops that namespace and redacts credentials'
Assert ([ServicePackFixture.Database]::Disposed -eq 3) 'failed XML loads also release all database connections'

[ServicePackFixture.Database]::Reset(); [ServicePackFixture.Database]::ThrowLoad='POS'
$result=ImportServicePacks $files $targets 'fake-provider'
Assert (-not $result.Success -and @($result.ServicePackResults | Where-Object Success).Count -eq 2 -and
    $result.Error -match 'XML-Datei in IRIS laden' -and $result.Error -match '1217' -and
    [ServicePackFixture.Database]::Disposed -eq 3) 'server exceptions identify the failed load step, release resources and allow other namespaces to finish'

[ServicePackFixture.Database]::Reset(); [ServicePackFixture.Database]::DisconnectLoad='POS'
$result=ImportServicePacks $files $targets 'fake-provider'
Assert (-not $result.Success -and $result.Error -match 'Server closed communication device' -and
    @($result.ServicePackResults | Where-Object Success).Count -eq 2 -and [ServicePackFixture.Database]::Disposed -eq 3 -and
    @([ServicePackFixture.Database]::Events | Where-Object { $_ -like 'POS|load|*' }).Count -eq 1 -and
    @([ServicePackFixture.Database]::Events | Where-Object { $_ -like 'POS|compile|*' }).Count -eq 0) 'connection loss is preserved without retries or further files in that namespace, while independent namespaces finish'

[ServicePackFixture.Database]::Reset()
$bad=Join-Path $root 'bad.xml'; [IO.File]::WriteAllText($bad,'<Export><Global name="not-code"/></Export>')
$badFile=[pscustomobject]@{ Path=$bad; Name='bad.xml'; Sha256=(Get-FileHash $bad -Algorithm SHA256).Hash; Items=@('not-code') }
$rejected=$false; try { $null=ImportServicePacks @($badFile) $targets 'fake-provider' } catch { $rejected=$true }
Assert ($rejected -and [ServicePackFixture.Database]::Events.Count -eq 0) 'unsupported XML contents fail before any namespace is touched'
$badFile=$files[0] | Select-Object *; $badFile.Sha256=('0'*64)
$rejected=$false; try { $null=ImportServicePacks @($badFile) $targets 'fake-provider' } catch { $rejected=$true }
Assert ($rejected -and [ServicePackFixture.Database]::Events.Count -eq 0) 'changed snapshots fail before database access'
$skipped=ImportServicePacks $files @() 'fake-provider'
Assert ($skipped.Success -and $skipped.Skipped) 'hosts without eligible namespaces are explicitly skipped'

$script:copied=[Collections.Generic.List[string]]::new(); $script:removed=$false
$script:fatalChild=$false; $script:failXmlUpload=$false; $script:childCalls=0
$remote=Join-Path $root 'upload destination'; $workerStage=Join-Path $root 'worker-stage'
[void][IO.Directory]::CreateDirectory($remote)
$keep=Join-Path $remote 'keep.txt'; [IO.File]::WriteAllText($keep,'unrelated target file')
function InvokeWorkerCommand {
    [CmdletBinding()] param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@())
    & $ScriptBlock @ArgumentList
}
function NewRemotePackageStage { param($Session)
    [void][IO.Directory]::CreateDirectory($workerStage)
    [pscustomobject]@{ Path=$workerStage; Base=$root }
}
function RemoveRemotePackageStage { param($Session,$Stage)
    $full=[IO.Path]::GetFullPath($Stage.Path)
    if ($full -ne $workerStage -or [IO.Path]::GetDirectoryName($full) -ne $root) { throw 'Test stage cleanup escaped the fixture' }
    [IO.Directory]::Delete($full,$true); $script:removed=$true
}
function CopySoftwareDatabaseFile { param($Session,$Source,$Target,$Hash,$Label)
    $directory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Target))
    if ($directory -notin @($remote,$workerStage) -or $Label -ne 'Servicepacks') { throw 'Test copy escaped the fixture or uses the wrong progress label' }
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $Hash) { throw 'Transfer hash mismatch' }
    [IO.File]::Copy($Source,$Target,$true); $script:copied.Add($Target)
    if ($script:failXmlUpload -and [IO.Path]::GetExtension($Target) -eq '.xml') { [IO.File]::WriteAllText($Target,'corrupted upload') }
    if ((Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash -ne $Hash) { throw 'Transfer hash mismatch' }
}
function InvokeSoftwareDatabaseChildOperation { param($Session,$Worker,$Request)
    $script:childCalls++
    Assert ($Request.Action -eq 'import-servicepacks' -and $Request.LocalExecution -and $Request.ServicePackFiles.Count -eq 2 -and
        @($Request.ServicePackFiles | Where-Object { [IO.Path]::GetDirectoryName($_.Path) -ne $remote }).Count -eq 0 -and
        $Request.DatabaseTargets[0].Ssl -and [IO.Path]::GetDirectoryName($Worker) -eq $workerStage) 'remote child loads XMLs from the configured destination and keeps worker files separate, preserving TLS/IRIS targets' | Out-Host
    if ($script:fatalChild) { return [pscustomobject]@{ Success=$false; Message='Fehler'; Error='provider unavailable' } }
    [pscustomobject](ImportServicePacks @($Request.ServicePackFiles) @($Request.DatabaseTargets) $Request.DatabaseProviderDirectory)
}
$provider=Join-Path $root 'provider'; [void][IO.Directory]::CreateDirectory($provider)
foreach ($name in @('InterSystems.Data.IRISClient.dll','InterSystems.Data.Bindings.dll','IRISClient.LICENSE.txt','Bindings.LICENSE.txt')) { [IO.File]::WriteAllText((Join-Path $provider $name),'not executable') }
$worker=Join-Path $root 'worker.ps1'; [IO.File]::WriteAllText($worker,'# synthetic, never run')
[ServicePackFixture.Database]::Reset(); [ServicePackFixture.Database]::FailCompile='POS'
$request=[pscustomobject]@{ ServicePackFiles=$files; DatabaseTargets=$targets; DatabaseProviderDirectory=$provider; RemoteInstallPath=$remote }
$remoteResult=ImportRemoteServicePacks $request 'simulated-session' $worker
Assert (-not $remoteResult.Success -and $remoteResult.ServicePackResults.Count -eq 3 -and $script:copied.Count -eq 7 -and
    $script:removed -and -not [IO.Directory]::Exists($workerStage) -and [ServicePackFixture.Database]::LoadPaths.Count -eq 5 -and
    @([ServicePackFixture.Database]::LoadPaths | Where-Object { [IO.Path]::GetDirectoryName($_) -ne $remote }).Count -eq 0) 'remote XMLs are uploaded to and loaded directly from the selected destination; partial outcomes survive worker cleanup'
Assert ([IO.File]::Exists($keep) -and @($files | Where-Object {
    (Get-FileHash -LiteralPath (Join-Path $remote $_.Name) -Algorithm SHA256).Hash -ne $_.Sha256
}).Count -eq 0) 'uploaded XMLs remain in the destination and unrelated files remain untouched'
$script:fatalChild=$true
$remoteResult=ImportRemoteServicePacks $request 'simulated-session' $worker
Assert (-not $remoteResult.Success -and $remoteResult.Error -eq 'provider unavailable' -and $remoteResult.ServicePackResults.Count -eq 0) 'remote provider failures retain their actual error even without namespace outcomes'
$script:fatalChild=$false; $script:failXmlUpload=$true; $script:removed=$false; $before=$script:childCalls
$rejected=$false; try { $null=ImportRemoteServicePacks $request 'simulated-session' $worker } catch { $rejected=$_.Exception.Message -match 'Transfer hash mismatch' }
Assert ($rejected -and $script:childCalls -eq $before -and $script:removed) 'failed XML upload verification prevents all imports and removes temporary worker files'
$script:failXmlUpload=$false
$sameName=$files[1] | Select-Object *; $sameName.Name=$files[0].Name
$request.ServicePackFiles=@($files[0],$sameName)
[ServicePackFixture.Database]::Reset()
$remoteResult=ImportRemoteServicePacks $request 'simulated-session' $worker
Assert ($remoteResult.Success -and @([ServicePackFixture.Database]::LoadPaths | Sort-Object -Unique).Count -eq 2 -and
    [IO.File]::ReadAllText((Join-Path $remote 'classes.xml')) -eq $classXml -and
    [IO.File]::ReadAllText((Join-Path $remote 'classes-2.xml')) -match '<Routine ') 'same-named XML selections use distinct destination files without overwriting each other'
Write-Output 'Servicepack tests passed. No real IRIS connections, remoting or imports were executed.'
