$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\SelfUpdate.ps1')
function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    Write-Host "PASS $message"
}
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path $workspace ('obj\self-update-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
function New-Fixture([string]$name) {
    $app = Join-Path $root $name
    $stage = Join-Path $app ('.NovaTouchUpdater-update-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $target = Join-Path $app "NovaTouchUpdater-v1.0.22.exe"
    $new = Join-Path $stage 'new.exe'
    [IO.File]::WriteAllText($target, 'old test executable - never executed')
    [IO.File]::WriteAllText($new, 'new test executable - never executed')
    [IO.File]::WriteAllText((Join-Path $app 'config.json'), '{"Hosts":["TEST-ONLY"],"EncryptedPassword":"fixture"}')
    $manifest = Join-Path $stage 'update.json'
    @{ ExecutablePath=$target; StagedPath=$new; Size=(Get-Item -LiteralPath $new).Length; Sha256=(Get-FileHash -LiteralPath $new).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding UTF8
    @{ App=$app; Stage=$stage; Target=$target; New=$new; Manifest=$manifest }
}
try {
    $fixture = New-Fixture 'successful exchange with spaces and ü'
    $config = [IO.File]::ReadAllText((Join-Path $fixture.App 'config.json'))
    Invoke-AppSelfUpdate -ManifestPath $fixture.Manifest -WaitForApplication {
        param($settings)
        Assert ([IO.File]::Exists((Join-Path $fixture.Stage 'ready'))) 'helper signals readiness before the original app exits'
        Assert ([IO.File]::ReadAllText($settings.ExecutablePath).StartsWith('old')) 'old EXE stays untouched while the app is running'
    } -StartApplication {
        param($path)
        Assert ($path -eq $fixture.Target -and [IO.File]::ReadAllText($path).StartsWith('new')) 'new EXE starts at the original path after replacement'
        Assert ([IO.File]::ReadAllText((Join-Path $fixture.Stage 'previous.exe')).StartsWith('old')) 'backup remains available until starting succeeds'
    }
    Assert (-not [IO.Directory]::Exists($fixture.Stage)) 'successful exchange deletes the old EXE and temporary helper files'
    Assert ([IO.File]::ReadAllText((Join-Path $fixture.App 'config.json')) -eq $config) 'saved config remains byte-for-byte unchanged'

    $fixture = New-Fixture 'failed start'
    $script:starts = 0
    $failed = $false
    try {
        Invoke-AppSelfUpdate -ManifestPath $fixture.Manifest -WaitForApplication {} -StartApplication {
            param($path)
            $script:starts++
            if ($script:starts -eq 1) { throw 'simulated start failure' }
            Assert ([IO.File]::ReadAllText($path).StartsWith('old')) 'old EXE is restored before restarting after failure'
        }
    } catch { $failed = $true; Write-Host "Expected failure: $($_.Exception.Message)" }
    Assert ($failed -and $script:starts -eq 2 -and [IO.File]::ReadAllText($fixture.Target).StartsWith('old')) 'start failure reports an error and restores the original executable'

    $fixture = New-Fixture 'damaged stage'
    [IO.File]::AppendAllText($fixture.New, 'modified')
    $failed = $false
    try { Invoke-AppSelfUpdate -ManifestPath $fixture.Manifest -WaitForApplication { throw 'must not wait' } -StartApplication { throw 'must not start' } }
    catch { $failed = $_.Exception.Message -like '*Download*' }
    Assert ($failed -and [IO.File]::ReadAllText($fixture.Target).StartsWith('old') -and -not [IO.File]::Exists((Join-Path $fixture.Stage 'ready'))) 'tampering is rejected before readiness and before replacing the EXE'

    $fixture = New-Fixture 'still running'
    $failed = $false
    try { Invoke-AppSelfUpdate -ManifestPath $fixture.Manifest -WaitForApplication { throw 'simulated exit timeout' } -StartApplication { throw 'must not start' } }
    catch { $failed = $_.Exception.Message -like '*timeout*' }
    Assert ($failed -and [IO.File]::ReadAllText($fixture.Target).StartsWith('old')) 'application exit timeout leaves the original executable intact'

    $fixture = New-Fixture 'invalid paths'
    $manifest = Get-Content -LiteralPath $fixture.Manifest -Raw | ConvertFrom-Json
    $manifest.StagedPath = $fixture.Target
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $fixture.Manifest -Encoding UTF8
    $failed = $false
    try { Invoke-AppSelfUpdate -ManifestPath $fixture.Manifest -WaitForApplication { throw 'must not wait' } -StartApplication { throw 'must not start' } }
    catch { $failed = $_.Exception.Message -like '*Pfade*' }
    Assert ($failed -and [IO.File]::ReadAllText($fixture.Target).StartsWith('old')) 'invalid exchange paths cannot replace or delete neighbouring files'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith((Join-Path $workspace 'obj\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
