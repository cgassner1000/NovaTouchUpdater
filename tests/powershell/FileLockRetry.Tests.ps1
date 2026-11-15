. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($condition, [string]$message) { if (-not $condition) { throw $message }; Write-Output ('PASS ' + $message) }

# Run the actual copy helpers with real Windows file locks. No services,
# installers, remoting, application config or production files are touched.
$tokens = $null; $parseErrors = $null
$sourceText = Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'UpdateSteps.ps1') -Raw -Encoding UTF8
$ast = [Management.Automation.Language.Parser]::ParseInput($sourceText, [ref]$tokens, [ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) 'UpdateSteps parses in Windows PowerShell'
foreach ($name in @('UpdateProgress','CopyUpdateFile','CopyContents')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public sealed class UpdateFileLockFixture : IDisposable {
    private FileStream stream;
    private Timer timer;
    public UpdateFileLockFixture(string path, int milliseconds) {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (milliseconds >= 0) timer = new Timer(Release, null, milliseconds, Timeout.Infinite);
    }
    private void Release(object state) {
        FileStream held = Interlocked.Exchange(ref stream, null);
        if (held != null) held.Dispose();
    }
    public void Dispose() {
        if (timer != null) timer.Dispose();
        Release(null);
    }
}
'@
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('file-lock-' + [guid]::NewGuid().ToString('N'))))
$fixturePrefix = [IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\'
Assert ($fixture.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) 'File-lock fixtures remain within test artifacts'
[void][IO.Directory]::CreateDirectory($fixture)
function Progress {
    [pscustomobject]@{ Total=2; Completed=1; LastPercent=-1; Timer=[Diagnostics.Stopwatch]::StartNew(); LockWaitMilliseconds=0L }
}
try {
    $source = Join-Path $fixture 'source.dll'
    $target = Join-Path $fixture 'grpc_csharp_ext.x64.dll'
    [IO.File]::WriteAllText($source, 'New synthetic DLL bytes; never executed.')
    [IO.File]::WriteAllText($target, 'Old synthetic bytes')
    $progress = Progress
    $events = @(CopyUpdateFile $source $target 'nt.fiscal' $progress)
    Assert ($events.Count -eq 0 -and $progress.LockWaitMilliseconds -eq 0) 'Ordinary copies add no wait or file-lock progress'

    [IO.File]::WriteAllText($target, 'Old synthetic bytes')
    $held = [UpdateFileLockFixture]::new($target, 650)
    try {
        $progress = Progress
        $events = @(CopyUpdateFile $source $target 'nt.fiscal' $progress)
        Assert ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $target).Hash) 'A real transient sharing violation recovers with the complete original source bytes'
        $waits = @($events | Where-Object Phase -eq 'Warte auf Dateifreigabe')
        Assert ($waits.Count -gt 0 -and $waits[0].Message.Contains('grpc_csharp_ext.x64.dll') -and $waits[0].ProgressPercent -eq 50) 'Lock progress identifies the exact DLL and retains the completed-file percentage'
        Assert ($progress.Completed -eq 1 -and $progress.LockWaitMilliseconds -ge 400 -and $events[-1].Phase -eq 'Update installieren') 'Retries do not count failed copies as completed and report recovery'
    } finally { $held.Dispose() }

    [IO.File]::WriteAllText($target, 'Old synthetic bytes')
    $held = [UpdateFileLockFixture]::new($target, -1)
    try {
        $progress = Progress
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        $failure = $null
        try { CopyUpdateFile $source $target 'nt.fiscal' $progress 400 | Out-Null } catch { $failure = $_ }
        Assert ($null -ne $failure -and $failure.Exception.Message.Contains($target) -and $failure.Exception.Message.Contains('400 ms')) 'A permanent lock reports the file and the exhausted bounded wait budget'
        Assert ($elapsed.ElapsedMilliseconds -ge 350 -and $elapsed.ElapsedMilliseconds -lt 3000 -and [IO.File]::ReadAllText($target) -eq 'Old synthetic bytes') 'A persistent lock preserves the old file and cannot wait indefinitely'
        $progress = Progress
        $progress.LockWaitMilliseconds = 380L
        $elapsed.Restart()
        try { CopyUpdateFile $source $target 'nt.fiscal' $progress 400 | Out-Null } catch { }
        Assert ($elapsed.ElapsedMilliseconds -lt 1000 -and $progress.LockWaitMilliseconds -ge 400) 'Other files share the remaining installation budget instead of receiving a fresh timeout'
    } finally { $held.Dispose() }

    foreach ($nativeCode in @(5,112,3)) {
        $script:copyCalls = 0
        $script:failureCode = $nativeCode
        function Copy-Item {
            [CmdletBinding()] param($LiteralPath, $Destination, [switch]$Force)
            $script:copyCalls++
            throw [IO.IOException]::new('Synthetic permanent copy error', (-2147024896 + $script:failureCode))
        }
        try {
            $progress = Progress
            $failure = $null
            try { CopyUpdateFile $source $target 'nt.fiscal' $progress | Out-Null } catch { $failure = $_ }
            Assert ($null -ne $failure -and $script:copyCalls -eq 1 -and $progress.LockWaitMilliseconds -eq 0) "Native error $nativeCode fails immediately without retry (permissions, disk full or path)"
        } finally { Remove-Item Function:\Copy-Item }
    }

    $script:copyCalls = 0
    function Copy-Item {
        [CmdletBinding()] param($LiteralPath, $Destination, [switch]$Force)
        $script:copyCalls++
        if ($script:copyCalls -eq 1) {
            throw [InvalidOperationException]::new('Wrapped native lock', [IO.IOException]::new('Lock violation', -2147024863))
        }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force -ErrorAction Stop
    }
    try {
        CopyUpdateFile $source $target 'nt.fiscal' (Progress) | Out-Null
        Assert ($script:copyCalls -eq 2) 'Nested native lock violations also retry the affected file only'
    } finally { Remove-Item Function:\Copy-Item }
}
finally {
    Assert ($fixture.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($fixture) -match '^file-lock-[a-f0-9]{32}$') 'Cleanup remains confined to the verified test fixture'
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction Stop
}
