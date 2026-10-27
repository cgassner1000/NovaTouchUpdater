. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$fixture=[IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('stage-packages-' + [guid]::NewGuid().ToString('N'))))
function FixturePath([string]$path) {
    $full=[IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $full.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected file access: $full" }
    $full
}
[void][IO.Directory]::CreateDirectory((FixturePath (Join-Path $fixture 'source')))
$files=@()
foreach ($name in @('nt.payment_1.0.0.zip','Control.msi','data.cab')) {
    $path=FixturePath (Join-Path $fixture ('source\' + $name))
    [IO.File]::WriteAllText($path, 'Never executed or installed: ' + $name)
    $files += [pscustomobject]@{ LocalPath=$path; RelativePath=$(if ($name -eq 'data.cab') { 'cabinet/data.cab' } else { '' }); Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}

$source=[IO.File]::ReadAllText((Join-Path $TestScriptsRoot 'RemoteWorker.ps1'))
$ast=[Management.Automation.Language.Parser]::ParseInput($source, [ref]$null, [ref]$null)
$copy=$ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'CopyRemoteFileWithProgress' }, $true)
. ([scriptblock]::Create($copy.Extent.Text.Replace('function CopyRemoteFileWithProgress(', 'function CopyLocalProduction(')))
$replacement=@'
function CopyRemoteFileWithProgress($session, [string]$localFile, [string]$remoteFile, [string]$label) {
    $null=FixturePath $localFile; $null=FixturePath $remoteFile
    Assert ([IO.Directory]::Exists([IO.Path]::GetDirectoryName($remoteFile))) 'nested target directories are prepared before copying a companion file'
    if (TestLocalSession $session) { CopyLocalProduction $session $localFile $remoteFile $label }
    else { [IO.File]::Copy($localFile, $remoteFile, $true) }
    $script:copies++
}
'@
$source=$source.Remove($copy.Extent.StartOffset, $copy.Extent.EndOffset - $copy.Extent.StartOffset).Insert($copy.Extent.StartOffset, $replacement)
$source=$source.Replace('$requestLine = [Console]::In.ReadLine()', '$requestLine = $script:requestJson').Replace('exit 0','return').Replace('exit 1','return')
$worker=[scriptblock]::Create($source)
function New-PSSessionOption { param($OpenTimeout, $OperationTimeout) Assert (-not $script:local) 'local staging creates no WinRM options'; @{} }
function New-PSSession {
    param($ComputerName,$Port,$SessionOption,[switch]$UseSSL,$ErrorAction)
    Assert (-not $script:local -and $Port -eq 443 -and $UseSSL) 'remote staging retains the selected HTTPS port'
    $script:sessions++; [pscustomobject]@{ Id=1 }
}
function Invoke-Command { param($Session,$ScriptBlock,[object[]]$ArgumentList,$ErrorAction) Assert (-not $script:local) 'local staging invokes no remote command'; & $ScriptBlock @ArgumentList }
function Remove-PSSession { param($Session,$ErrorAction) Assert (-not $script:local) 'local staging closes no remote session'; $script:closed++ }
function Get-Service { throw 'Package staging must not access services' }
function Stop-Service { throw 'Package staging must not stop services' }
function Start-Service { throw 'Package staging must not start services' }
function Start-Process { throw 'Package staging must not run an installer' }
function Get-ItemProperty { throw 'Package staging must not inspect installed software' }
function Remove-Item { throw 'Package staging must not clean existing files' }

function RunCase([string]$name,[string]$destination,[bool]$local,[bool]$skip,[bool]$force,[int]$expectedCopies,[bool]$success=$true,[object[]]$payload=$files,[string]$action='stage-packages') {
    $script:local=$local; $script:copies=0; $script:sessions=0; $script:closed=0
    $request=@{ Action=$action; Host=$(if ($local) { 'LOCALHOST' } else { 'fixture.invalid' }); LocalExecution=$local;
        Username=''; Password=''; UseSsl=$true; HttpsPort=443; ForceUpdate=$force; SkipZipTransfer=$skip;
        RemoteInstallPath=(FixturePath $destination); TransferFiles=$payload }
    $script:requestJson=$request | ConvertTo-Json -Depth 8 -Compress
    $writer=[IO.StringWriter]::new(); $original=[Console]::Out
    try { [Console]::SetOut($writer); & $worker } finally { [Console]::SetOut($original) }
    $messages=@($writer.ToString() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $result=@($messages | Where-Object Event -eq 'result')[-1]
    Assert ($result.Success -eq $success) ($name + ' returns the expected outcome: ' + ($result | ConvertTo-Json -Compress -Depth 3))
    Assert ($script:copies -eq $expectedCopies) ($name + ' copies only missing or different package files')
    Assert ($script:sessions -eq $(if (-not $local -and $action -eq 'transfer') { 1 } else { 0 }) -and $script:closed -eq $script:sessions) ($name + ' retains local execution and rejects remote bulk staging before connecting')
    if ($success -and -not ($skip -and -not $local)) {
        foreach ($file in $payload) {
            $relative=if ($file.RelativePath) { $file.RelativePath } else { [IO.Path]::GetFileName($file.LocalPath) }
            $target=FixturePath (Join-Path $destination $relative)
            Assert ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $file.Sha256) ($name + ' preserves every verified payload byte')
        }
    }
    Write-Host ('PASS ' + $name)
    $result
}
try {
    $localTarget=Join-Path $fixture 'local-target'
    $remoteTarget=Join-Path $fixture 'remote-target'
    $null=RunCase 'local-missing' $localTarget $true $false $false 3
    [IO.File]::WriteAllText((FixturePath (Join-Path $localTarget 'keep.txt')), 'Keep unrelated files')
    $null=RunCase 'local-current-force' $localTarget $true $false $true 0
    Assert ([IO.File]::ReadAllText((Join-Path $localTarget 'keep.txt')) -eq 'Keep unrelated files') 'existing non-package files are never cleaned'
    $null=RunCase 'reject-remote-bulk-staging' $remoteTarget $false $false $false 0 $false
    Assert (-not [IO.Directory]::Exists($remoteTarget)) 'remote bulk staging creates no folder and copies no files'
    $skippedTarget=Join-Path $fixture 'remote-skipped'
    $result=RunCase 'reject-remote-bulk-skip-force' $skippedTarget $false $true $true 0 $false
    Assert (-not [IO.Directory]::Exists($skippedTarget)) 'remote bulk staging is rejected even with Skip or Force'
    $null=RunCase 'unchanged-explicit-manual-upload' $remoteTarget $false $true $true 3 $true $files 'transfer'
    $null=RunCase 'local-skip-remote-option' (Join-Path $fixture 'local-with-skip') $true $true $true 3
    $bad=@([pscustomobject]@{ LocalPath=$files[0].LocalPath; RelativePath='..\escape.zip'; Sha256=$files[0].Sha256 })
    $result=RunCase 'reject-traversal' (Join-Path $fixture 'reject') $true $false $false 0 $false $bad
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture 'escape.zip'))) 'relative payload paths cannot escape the destination'
    Write-Host 'PASS bulk staging is local-only; explicit manual upload and all installation rules retain their existing behavior'
} finally {
    $checked=[IO.Path]::GetFullPath($fixture)
    if ($checked.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($checked) -match '^stage-packages-[a-f0-9]{32}$') { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $checked -Recurse -Force }
}
