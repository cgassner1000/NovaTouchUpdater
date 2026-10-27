. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
# Windows PowerShell 5.1. Real asynchronous progress streams and ZIP extraction,
# but no remoting: the upload pipeline is replaced with a local file writer.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$workspace=$RepositoryRoot
$fixture=[IO.Path]::GetFullPath((Join-Path $workspace ('artifacts\test-results\bp-' + [guid]::NewGuid().ToString('N').Substring(0,8))))
Assert ($fixture.StartsWith($workspace + '\artifacts\test-results\', [StringComparison]::OrdinalIgnoreCase)) 'Invalid fixture root'
[void][IO.Directory]::CreateDirectory($fixture)
function FixturePath([string]$path) {
    $full=[IO.Path]::GetFullPath($path)
    Assert ($full.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) "Path outside fixture: $full"
    $full
}

$workerSource=Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'RemoteWorker.ps1') -Raw -Encoding UTF8
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($workerSource, [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
foreach ($node in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('Emit','Progress','TestLocalSession','NewUploadPipeline','CopyRemoteFileWithProgress') }, $true)) {
    . ([scriptblock]::Create($node.Extent.Text))
}

try {
    $source=FixturePath (Join-Path $fixture 'source [literal].bin')
    $bytes=[byte[]]::new(2MB)
    $random=[Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    [IO.File]::WriteAllBytes($source,$bytes)
    $session=[pscustomobject]@{ Id='mock-only' }
    $destination=FixturePath (Join-Path $fixture 'uploaded [literal].bin')
    $pipeline=NewUploadPipeline $session $source $destination
    try {
        $command=$pipeline.Commands.Commands[1]
        $parameters=@{}
        foreach ($parameter in $command.Parameters) { $parameters[$parameter.Name]=$parameter.Value }
        Assert ($command.CommandText -eq 'Copy-Item' -and $parameters.LiteralPath -eq $source -and
            $parameters.Destination -eq $destination -and [object]::ReferenceEquals($parameters.ToSession,$session) -and
            $parameters.Force -eq $true -and $parameters.ErrorAction -eq 'Stop') 'The production pipeline must retain literal paths, the existing session, Force and terminating errors'
    }
    finally { $pipeline.Dispose() }

    # Only this factory is mocked. The real upload helper polls a real SDK
    # pipeline, consumes ProgressRecords, handles exceptions and disposes it.
    $script:uploadFailure=$false
    function NewUploadPipeline($session, [string]$localFile, [string]$remoteFile) {
        Assert ($session.Id -eq 'mock-only') 'Only the mocked session may be used'
        $localFile=FixturePath $localFile
        $remoteFile=FixturePath $remoteFile
        $pipeline=[Management.Automation.PowerShell]::Create()
        [void]$pipeline.AddScript(@'
param($source,$destination,$fail)
$ErrorActionPreference='Stop'
$ProgressPreference='Continue'
$bytes=[IO.File]::ReadAllBytes($source)
$stream=[IO.File]::Open($destination,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
    $chunk=$bytes.Length/4
    for ($part=0; $part -lt 4; $part++) {
        $stream.Write($bytes,$part*$chunk,$chunk)
        $stream.Flush()
        Write-Progress -Activity 'Local upload simulation' -Status 'Data block written' -PercentComplete (($part+1)*25)
        if ($fail -and $part -eq 0) { throw 'Simulated upload failure' }
        Start-Sleep -Milliseconds 300
    }
} finally { $stream.Dispose() }
'@).AddArgument($localFile).AddArgument($remoteFile).AddArgument($script:uploadFailure)
        $pipeline
    }
    for ($file=1; $file -le 3; $file++) {
        $destination=FixturePath (Join-Path $fixture ("upload-$file.bin"))
        $writer=[IO.StringWriter]::new()
        $original=[Console]::Out
        try { [Console]::SetOut($writer); CopyRemoteFileWithProgress $session $source $destination "Datei $file/3" }
        finally { [Console]::SetOut($original) }
        $events=@($writer.ToString() -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
        Assert ($events[0].ProgressPercent -eq 0 -and $events[-1].ProgressPercent -eq 99) 'Each upload starts at zero and reserves completion for hash verification'
        Assert (@($events | Where-Object { $_.ProgressPercent -gt 0 -and $_.ProgressPercent -lt 99 }).Count -gt 0) 'Upload must report actual intermediate data-block progress within a single file'
        $previous=-1
        foreach ($event in $events) {
            Assert ($event.ProgressPercent -ge $previous -and $event.ProgressPercent -lt 100 -and $event.Message -like "Datei $file/3 : *") 'Upload progress must be monotonic and identify the current file'
            $previous=$event.ProgressPercent
        }
        Assert ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) 'Progress capture must preserve every uploaded byte'
        Write-Output "PASS asynchronous byte progress for file $file/3"
    }
    $script:uploadFailure=$true
    $destination=FixturePath (Join-Path $fixture 'failed-upload.bin')
    $writer=[IO.StringWriter]::new(); $original=[Console]::Out; $caught=''
    try { [Console]::SetOut($writer); CopyRemoteFileWithProgress $session $source $destination 'Failed upload' }
    catch { $caught=$_.Exception.Message }
    finally { [Console]::SetOut($original) }
    Assert ($caught -match 'Simulated upload failure') 'Pipeline errors must retain the actual transfer error'
    $events=@($writer.ToString() -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    Assert (@($events | Where-Object { $_.ProgressPercent -ge 99 }).Count -eq 0) 'Failed uploads must not report completion or start hash verification'
    Write-Output 'PASS upload failure and SDK pipeline cleanup'

    $destination=FixturePath (Join-Path $fixture 'local-copy.bin')
    $localSession=[pscustomobject]@{ NovaTouchLocal=$true }
    $writer=[IO.StringWriter]::new(); $original=[Console]::Out
    try { [Console]::SetOut($writer); CopyRemoteFileWithProgress $localSession $source $destination 'Local package' }
    finally { [Console]::SetOut($original) }
    Assert ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) 'Direct local copying preserves every byte without creating a remote upload pipeline'
    CopyRemoteFileWithProgress $localSession $source $source 'Same local file'
    Assert ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) 'Direct copying never truncates a package when source and target are identical'
    Write-Output 'PASS direct local copy without WinRM or self-overwrite'

    # Replace only the clock in this extraction test so every data block can be
    # observed deterministically, without slow I/O or hundreds of MB of data.
    function NewExtractionTestTimer {
        $timer=[pscustomobject]@{ ElapsedMilliseconds=300 }
        $timer | Add-Member -MemberType ScriptMethod -Name Restart -Value {}
        $timer
    }
    $steps=Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'UpdateSteps.ps1') -Raw -Encoding UTF8
    $stepsAst=[Management.Automation.Language.Parser]::ParseInput($steps, [ref]$tokens, [ref]$parseErrors)
    Assert ($parseErrors.Count -eq 0) ($parseErrors | Out-String)
    $prepare=$stepsAst.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'PrepareUpdatePackages' }, $true)
    . ([scriptblock]::Create($prepare.Extent.Text.Replace('[Diagnostics.Stopwatch]::StartNew()', '(NewExtractionTestTimer)')))
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $firstZip=FixturePath (Join-Path $fixture 'first.zip')
    $secondZip=FixturePath (Join-Path $fixture 'empty.zip')
    $archive=[IO.Compression.ZipFile]::Open($firstZip,[IO.Compression.ZipArchiveMode]::Create)
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$source,'nested/big.bin') } finally { $archive.Dispose() }
    $archive=[IO.Compression.ZipFile]::Open($secondZip,[IO.Compression.ZipArchiveMode]::Create)
    try { [void]$archive.CreateEntry('empty-dir/'); [void]$archive.CreateEntry('empty.txt') } finally { $archive.Dispose() }
    $stageRoot=FixturePath (Join-Path $fixture 'staging')
    $output=@(PrepareUpdatePackages $stageRoot 'payment,fiscal' $firstZip $secondZip '')
    $stage=FixturePath ([string]$output[-1])
    $firstEvents=@($output | Where-Object { $null -ne $_.PSObject.Properties['Event'] -and $_.Message.StartsWith('first.zip') })
    $secondEvents=@($output | Where-Object { $null -ne $_.PSObject.Properties['Event'] -and $_.Message.StartsWith('empty.zip') })
    Assert ($firstEvents[0].ProgressPercent -eq 0 -and $firstEvents[-1].ProgressPercent -eq 100 -and
        @($firstEvents | Where-Object { $_.ProgressPercent -gt 0 -and $_.ProgressPercent -lt 99 }).Count -ge 3) 'A single large ZIP entry must publish multiple byte-based intermediate percentages'
    Assert ($secondEvents[0].ProgressPercent -eq 0 -and $secondEvents[-1].ProgressPercent -eq 100) 'The next ZIP restarts at zero, including zero-byte archives'
    Assert ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath (Join-Path $stage 'payment\nested\big.bin')).Hash) 'Buffered extraction must preserve every byte'
    Assert ([IO.Directory]::Exists((Join-Path $stage 'fiscal\empty-dir')) -and [IO.File]::Exists((Join-Path $stage 'fiscal\empty.txt'))) 'Empty ZIP directories and files must still be extracted'
    Write-Output 'PASS per-ZIP byte progress, large entry, zero-byte entry and empty directory'
}
finally {
    Assert ($fixture.StartsWith($workspace + '\artifacts\test-results\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($fixture) -match '^bp-[a-f0-9]{8}$') 'Invalid fixture cleanup'
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction Stop
}
