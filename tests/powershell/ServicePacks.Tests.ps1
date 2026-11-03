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
    [pscustomobject]@{ ObjectType=[ServicePackFixture.ProxyObject]; ClientType=[ServicePackFixture.ClientType] }
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
    Assert ($events.Count -eq 3 -and $events[0] -like '*|load|*' -and $events[1] -like '*|load|*' -and $events[2] -eq ($ns+'|compile|ServicePack.mac,User.ServicePack.cls|ckbf')) 'all files load before mixed routine/class compilation with exact ckbf flags'
}
Assert (@([ServicePackFixture.Database]::Events | Where-Object { $_.StartsWith('%') }).Count -eq 0) 'percent namespaces are never connected or imported'
Assert ([ServicePackFixture.Database]::Disposed -eq 3 -and [ServicePackFixture.Database]::Cleared -eq 6 -and [ServicePackFixture.Database]::Closed -eq 6) 'every connection and temporary IRIS stream is cleaned up'
Assert (@([ServicePackFixture.Database]::Events | Where-Object { $_.Contains($unicode) }).Count -eq 3) 'Unicode and surrogate pairs survive character-stream chunk boundaries'

[ServicePackFixture.Database]::Reset(); [ServicePackFixture.Database]::FailCompile='POS'
$result=ImportServicePacks $files $targets 'fake-provider'
Assert (-not $result.Success -and @($result.ServicePackResults | Where-Object Success).Count -eq 2 -and $result.Error -match 'compiler error') 'compiler errors remain per namespace while independent namespaces finish'
[ServicePackFixture.Database]::Reset(); [ServicePackFixture.Database]::FailLoad='POS'
$result=ImportServicePacks $files $targets 'fake-provider'
Assert (-not $result.Success -and @($result.ServicePackResults | Where-Object Success).Count -eq 2 -and $result.Error -notmatch 'synthetic-secret' -and
    @([ServicePackFixture.Database]::Events | Where-Object { $_ -like 'POS|compile|*' }).Count -eq 0) 'a failed XML load prevents that namespace compilation and redacts credentials'

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
$script:fatalChild=$false
$remote=Join-Path $root 'remote'; [void][IO.Directory]::CreateDirectory($remote)
function NewRemotePackageStage { param($Session) [pscustomobject]@{ Path=$remote; Base=$root } }
function RemoveRemotePackageStage { param($Session,$Stage) $script:removed=$true }
function CopySoftwareDatabaseFile { param($Session,$Source,$Target,$Hash)
    if (-not ([IO.Path]::GetFullPath($Target).StartsWith($remote+'\',[StringComparison]::OrdinalIgnoreCase))) { throw 'Test copy escaped the fixture' }
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $Hash) { throw 'Transfer hash mismatch' }
    [IO.File]::Copy($Source,$Target,$true); $script:copied.Add($Target)
}
function InvokeSoftwareDatabaseChildOperation { param($Session,$Worker,$Request)
    Assert ($Request.Action -eq 'import-servicepacks' -and $Request.LocalExecution -and $Request.ServicePackFiles.Count -eq 2 -and
        @($Request.ServicePackFiles | Where-Object { -not $_.Path.StartsWith($remote+'\') }).Count -eq 0 -and $Request.DatabaseTargets[0].Ssl) 'remote child uses staged XMLs and preserves confirmed TLS/IRIS targets' | Out-Host
    if ($script:fatalChild) { return [pscustomobject]@{ Success=$false; Message='Fehler'; Error='provider unavailable' } }
    [pscustomobject]@{ Success=$false; Changed=$true; Message='partial'; Error='compile error'; ServicePackResults=@([pscustomobject]@{ Namespace='POS'; Success=$false }) }
}
$provider=Join-Path $root 'provider'; [void][IO.Directory]::CreateDirectory($provider)
foreach ($name in @('InterSystems.Data.IRISClient.dll','InterSystems.Data.Bindings.dll','IRISClient.LICENSE.txt','Bindings.LICENSE.txt')) { [IO.File]::WriteAllText((Join-Path $provider $name),'not executable') }
$worker=Join-Path $root 'worker.ps1'; [IO.File]::WriteAllText($worker,'# synthetic, never run')
$remoteResult=ImportRemoteServicePacks ([pscustomobject]@{ ServicePackFiles=$files; DatabaseTargets=$targets; DatabaseProviderDirectory=$provider }) 'simulated-session' $worker
Assert (-not $remoteResult.Success -and $remoteResult.ServicePackResults.Count -eq 1 -and $script:copied.Count -eq 7 -and $script:removed) 'remote staging retains partial outcomes and always removes only its own temporary stage'
$script:fatalChild=$true
$remoteResult=ImportRemoteServicePacks ([pscustomobject]@{ ServicePackFiles=$files; DatabaseTargets=$targets; DatabaseProviderDirectory=$provider }) 'simulated-session' $worker
Assert (-not $remoteResult.Success -and $remoteResult.Error -eq 'provider unavailable' -and $remoteResult.ServicePackResults.Count -eq 0) 'remote provider failures retain their actual error even without namespace outcomes'
Write-Output 'Servicepack tests passed. No real IRIS connections, remoting or imports were executed.'
