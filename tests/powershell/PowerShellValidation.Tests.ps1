. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
$validator = Join-Path $RepositoryRoot 'build\ValidatePowerShell.ps1'
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('powershell-validation-' + [guid]::NewGuid().ToString('N'))))
Assert ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'validator fixtures stay in test artifacts'
[void][IO.Directory]::CreateDirectory($fixture)
function Validate([string]$Directory) {
    $process = New-Object Diagnostics.Process
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $PSHOME 'powershell.exe'
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $validator + '" -ScriptsDirectory "' + $Directory + '"'
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Validator did not finish' }
        [pscustomobject]@{ ExitCode=$process.ExitCode; Text=($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) }
    } finally { $process.Dispose() }
}
try {
    $valid = Join-Path $fixture 'valid scripts'
    [void][IO.Directory]::CreateDirectory((Join-Path $valid 'nested'))
    [IO.File]::WriteAllText((Join-Path $valid 'worker.ps1'), "throw 'Source scripts must never be executed during validation.'")
    [IO.File]::WriteAllText((Join-Path $valid 'nested\helper.ps1'), 'function Helper { param($Value) return $Value }')
    $result = Validate $valid
    Assert ($result.ExitCode -eq 0 -and $result.Text.Contains('2 scripts')) 'valid scripts are parsed recursively without executing their bodies'

    $broken = Join-Path $fixture 'invalid syntax'
    [void][IO.Directory]::CreateDirectory($broken)
    [IO.File]::WriteAllText((Join-Path $broken 'bad-worker.ps1'), 'function Incomplete {')
    $result = Validate $broken
    Assert ($result.ExitCode -ne 0 -and $result.Text.Contains('bad-worker.ps1(1,')) 'PowerShell syntax errors reject a build with source filename and location'

    $conflicted = Join-Path $fixture 'merge conflict'
    [void][IO.Directory]::CreateDirectory($conflicted)
    # This sample is legal PowerShell, but a merge conflict must still block a release.
    $sample = '$text = @"' + "`n" + '<<<<<<< HEAD' + "`nours`n" + '=======' + "`ntheirs`n" + '>>>>>>> branch' + "`n" + '"@' + "`n"
    $tokens = $null; $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput($sample, [ref]$tokens, [ref]$parseErrors)
    Assert (@($parseErrors).Count -eq 0) 'the marker sample also exercises conflicts that a syntax parser alone would accept'
    [IO.File]::WriteAllText((Join-Path $conflicted 'conflicted.ps1'), $sample)
    $result = Validate $conflicted
    Assert ($result.ExitCode -ne 0 -and $result.Text.Contains('conflicted.ps1(2)') -and $result.Text.Contains('Git conflict marker')) 'merge markers block a release even inside a syntactically valid string'

    $result = Validate $TestScriptsRoot
    Assert ($result.ExitCode -eq 0) 'every actual application PowerShell script passes the build validation'
} finally {
    if ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
