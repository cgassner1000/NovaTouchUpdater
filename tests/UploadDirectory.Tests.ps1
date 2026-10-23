# Windows PowerShell 5.1: only checked workspace fixtures can be mutated.
# Service/registry lookups are mocked; no hosts or actual services are touched.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert($condition,[string]$message) { if (-not $condition) { throw $message } }
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture=[IO.Path]::GetFullPath((Join-Path $workspace ('obj\ud-' + [guid]::NewGuid().ToString('N').Substring(0,8))))
Assert ($fixture.StartsWith($workspace + '\obj\',[StringComparison]::OrdinalIgnoreCase)) 'Invalid fixture'
[void][IO.Directory]::CreateDirectory($fixture)
. ([scriptblock]::Create((Get-Content -LiteralPath (Join-Path $workspace 'UploadDirectory.ps1') -Raw -Encoding UTF8)))
$script:services=@(); $script:images=@{}; $script:deleted=[Collections.Generic.List[string]]::new(); $script:linked=''; $script:registryFailure=$false
function Get-Service { [CmdletBinding()] param() $script:services }
function Get-ItemProperty {
    [CmdletBinding()] param([string]$LiteralPath,[string]$Name)
    Assert ($LiteralPath.StartsWith('HKLM:\SYSTEM\CurrentControlSet\Services\') -and $Name -eq 'ImagePath') 'Unexpected registry read'
    if ($script:registryFailure) { throw 'Simulated registry failure' }
    $service=$LiteralPath.Substring($LiteralPath.LastIndexOf('\')+1)
    [pscustomobject]@{ ImagePath=$script:images[$service] }
}
function Get-Item {
    [CmdletBinding()] param([string]$LiteralPath,[switch]$Force)
    $item=Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force:$Force -ErrorAction Stop
    if ($LiteralPath -eq $script:linked) { return [pscustomobject]@{ Attributes=($item.Attributes -bor [IO.FileAttributes]::ReparsePoint) } }
    $item
}
function Get-ChildItem {
    [CmdletBinding()] param([string]$LiteralPath,[switch]$Force)
    foreach ($item in Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $LiteralPath -Force:$Force -ErrorAction Stop) {
        if ($item.FullName -eq $script:linked) {
            [pscustomobject]@{ Attributes=($item.Attributes -bor [IO.FileAttributes]::ReparsePoint); FullName=$item.FullName }
        } else { $item }
    }
}
function Remove-Item {
    [CmdletBinding()] param([string]$LiteralPath,[switch]$Recurse,[switch]$Force)
    $full=[IO.Path]::GetFullPath($LiteralPath)
    Assert ($full.StartsWith($fixture + '\',[StringComparison]::OrdinalIgnoreCase)) "Deletion outside fixture: $full"
    $script:deleted.Add($full)
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $full -Recurse:$Recurse -Force:$Force -ErrorAction Stop
}
function WriteFixture([string]$relative,[string]$text='keep') {
    $path=[IO.Path]::GetFullPath((Join-Path $fixture $relative))
    Assert ($path.StartsWith($fixture + '\',[StringComparison]::OrdinalIgnoreCase)) 'Invalid write'
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path,$text)
    $path
}
function Fails([scriptblock]$action,[string]$pattern) {
    $caught=''
    try { & $action | Out-Null } catch { $caught=$_.Exception.Message }
    Assert ($caught -and $caught -match $pattern) "Wrong or missing error: $caught"
}
try {
    $missing=Join-Path $fixture 'missing'
    $info=ManageUploadDirectory $missing $false ''
    Assert ($info.EntryCount -eq 0 -and -not [IO.Directory]::Exists($missing)) 'Checking a missing directory is read-only'
    $upload=Join-Path $fixture 'upload'
    [void][IO.Directory]::CreateDirectory($upload)
    $info=ManageUploadDirectory $upload $false ''
    Assert ($info.EntryCount -eq 0 -and $info.CanClean) 'An empty dedicated upload directory needs no cleanup'

    $old=WriteFixture 'upload\old.zip'
    $hidden=WriteFixture 'upload\hidden.dat'
    [IO.File]::SetAttributes($hidden,[IO.FileAttributes]::Hidden)
    $nested=WriteFixture 'upload\sub\config.txt'
    $outside=WriteFixture 'outside\keep.txt'
    $info=ManageUploadDirectory $upload $false ''
    Assert ($info.EntryCount -eq 3 -and $info.Snapshot -and $script:deleted.Count -eq 0) 'Inspection includes hidden files and folders without removing anything'
    Fails { ManageUploadDirectory $upload $true '' } 'bestätigte|best.tigte'
    Assert ($script:deleted.Count -eq 0) 'Cleanup requires a confirmation snapshot'
    [IO.File]::AppendAllText($nested,'changed while dialog open')
    Fails { ManageUploadDirectory $upload $true $info.Snapshot } 'seit der Abfrage'
    Assert ($script:deleted.Count -eq 0 -and [IO.File]::Exists($old)) 'Nested changes invalidate the confirmed directory snapshot before any deletion'
    $info=ManageUploadDirectory $upload $false ''
    $other=Join-Path $fixture 'other'
    [void][IO.Directory]::CreateDirectory($other)
    [IO.File]::WriteAllText((Join-Path $other 'keep.txt'),'keep')
    Fails { ManageUploadDirectory $other $true $info.Snapshot } 'seit der Abfrage'
    Assert ([IO.File]::Exists((Join-Path $other 'keep.txt'))) 'A confirmation snapshot cannot be reused for another directory'
    $result=ManageUploadDirectory $upload $true $info.Snapshot
    Assert ($result.Cleaned -and $result.EntryCount -eq 0 -and [IO.Directory]::Exists($upload) -and
        [IO.File]::ReadAllText($outside) -eq 'keep') 'Confirmed cleanup removes contents, preserves the upload directory and leaves neighboring files untouched'
    Write-Output 'PASS read-only checks, explicit confirmation, hidden/nested files, stale and path-bound snapshots, exact cleanup scope'

    $script:services=@([pscustomobject]@{Name='nt3_server_suite_HKS'},[pscustomobject]@{Name='nt3_server_suite_OTHER'},[pscustomobject]@{Name='nt.payment'})
    $script:images=@{
        nt3_server_suite_HKS=('"' + (Join-Path $fixture 'instance\portable_python\nt3_server_suite_HKS.exe') + '" --service');
        nt3_server_suite_OTHER=('"' + (Join-Path $fixture 'second\portable_python\nested\service.exe') + '"');
        'nt.payment'=('"' + (Join-Path $fixture 'payment\service.exe') + '"')
    }
    foreach ($relative in @('instance\portable_python','instance\apps\nt3-server-suite','second\portable_python','second\apps\nt3-server-suite','payment')) {
        $path=Join-Path $fixture $relative
        $null=WriteFixture ($relative + '\keep.txt')
        $info=ManageUploadDirectory $path $false ''
        Assert (-not $info.CanClean -and $info.EntryCount -gt 0) 'Every recognized installation target must be protected'
        Fails { ManageUploadDirectory $path $true 'not-an-approval' } 'darf nicht bereinigt'
        Assert ([IO.File]::Exists((Join-Path $path 'keep.txt'))) 'Installed files must survive cleanup requests'
    }
    $info=ManageUploadDirectory (Join-Path $fixture 'instance') $false ''
    Assert (-not $info.CanClean) 'The parent of installation folders must not be cleaned'
    $safe=Join-Path $fixture 'instance\uploads'
    $null=WriteFixture 'instance\uploads\old.zip'
    $info=ManageUploadDirectory $safe $false ''
    Assert ($info.CanClean) 'A dedicated upload folder beside apps and portable_python remains usable'
    $null=ManageUploadDirectory $safe $true $info.Snapshot
    Assert ([IO.File]::Exists((Join-Path $fixture 'instance\portable_python\keep.txt'))) 'Sibling upload cleanup must preserve installed runtime files'
    $script:registryFailure=$true
    $info=ManageUploadDirectory $upload $false ''
    Assert (-not $info.CanClean) 'An unreadable service registration must not allow destructive cleanup'
    $script:registryFailure=$false; $script:services=@()
    $info=ManageUploadDirectory ([IO.Path]::GetPathRoot($fixture)) $false ''
    Assert (-not $info.CanClean) 'Drive roots must never be cleaned'
    Write-Output 'PASS all installation instances, parent directories, safe upload siblings, registry errors and drive-root protection'

    $script:linked=WriteFixture 'links\nested\junction'
    Fails { ManageUploadDirectory (Join-Path $fixture 'links') $false '' } 'Verkn.pfung/Junction'
    $script:linked=Join-Path $fixture 'links'
    Fails { ManageUploadDirectory (Join-Path $fixture 'links\nested') $false '' } 'Verkn.pfung/Junction'
    Write-Output 'PASS links in a directory tree and upload-path ancestors are rejected'
}
finally {
    Assert ($fixture.StartsWith($workspace + '\obj\',[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($fixture) -match '^ud-[a-f0-9]{8}$') 'Invalid final cleanup'
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction Stop
}
