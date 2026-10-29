. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepositoryRoot 'build\ReleasePolicy.ps1')
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
function Release([string]$Tag, [bool]$Draft=$false, [bool]$Prerelease=$false) { [pscustomobject]@{ tag_name=$Tag; draft=$Draft; prerelease=$Prerelease } }
function ExpectFailure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed $Message
}

$tags = @('v1.0.500','v1.1.8','v1.1.9','v1.1.10','v1.1.250-beta','notes','v1.1.11.1')
Assert ((GetNextUpdaterBuildNumber '1.1' $tags) -eq 11) 'the next build follows the largest numeric tag in the same series, not a tag count or global run number'
foreach ($series in @('1.2','1.3','1.4','2.1')) {
    Assert ((GetNextUpdaterBuildNumber $series $tags) -eq 1) "new series $series begins at build 1"
}
Assert ((GetNextUpdaterBuildNumber '1.2' @('v1.2.1','v1.2.3','v1.1.999')) -eq 4) 'gaps or deleted old tags never reuse an existing build number'
ExpectFailure { GetNextUpdaterBuildNumber '1.2' @('v1.2.65534') } 'exhausted assembly build numbers require a new version series'

$releases = @('v1.0.7','v1.0.8','v1.1.8','v1.1.9','v1.1.10','v1.2.1','v1.2.2','v1.2.3','v1.2.4') | ForEach-Object { Release $_ }
$releases += @( (Release 'v5.0.1' $true), (Release 'v6.0.1' $false $true), (Release 'manual-backup') )
$plan = GetUpdaterReleaseRetentionPlan $releases
Assert ($plan.CurrentSeries -eq '1.2' -and $plan.Keep -contains 'v1.2.4' -and $plan.Keep -contains 'v1.2.3' -and $plan.Keep -contains 'v1.2.2' -and $plan.Delete -contains 'v1.2.1') 'the current series retains its latest three numeric releases'
Assert ($plan.Keep -contains 'v1.0.8' -and $plan.Keep -contains 'v1.1.10' -and $plan.Delete -contains 'v1.1.9' -and $plan.Delete -contains 'v1.0.7') 'each older series retains its last release permanently'
Assert ($plan.Keep -contains 'v5.0.1' -and $plan.Keep -contains 'v6.0.1' -and $plan.Keep -contains 'manual-backup') 'drafts, prereleases and unrelated tags are protected'
$releases += Release 'v1.3.1'
$plan = GetUpdaterReleaseRetentionPlan $releases
Assert ($plan.CurrentSeries -eq '1.3' -and $plan.Keep -contains 'v1.3.1' -and $plan.Keep -contains 'v1.2.4' -and $plan.Delete -contains 'v1.2.3') 'advancing to 1.3 freezes the final 1.2 release while keeping all earlier final releases'
$releases += Release 'v1.1.11'
$plan = GetUpdaterReleaseRetentionPlan $releases
Assert ($plan.CurrentSeries -eq '1.3' -and $plan.Keep -contains 'v1.2.4' -and $plan.Keep -contains 'v1.1.11') 'an older-series rerun cannot turn the newer published series into an archive'
Assert ($plan.LatestTag -eq 'v1.3.1') 'Latest remains the highest stable version when an older series is released again'
Assert ((GetUpdaterReleaseRetentionPlan @()).Delete.Count -eq 0) 'an empty repository has nothing to prune'
ExpectFailure { GetUpdaterReleaseRetentionPlan @([pscustomobject]@{ tag_name='v1.2.1' }) } 'incomplete release data cannot authorize deletion'

# Run the real orchestration with a mock CLI. No GitHub commands leave this test.
$fixture = [IO.Path]::GetFullPath((Join-Path $TestArtifactsRoot ('release-policy-' + [guid]::NewGuid().ToString('N'))))
Assert ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'release test files stay under test artifacts'
[void][IO.Directory]::CreateDirectory($fixture)
$state = [pscustomobject]@{ Calls=[Collections.Generic.List[object]]::new(); Json=''; ApiExit=0; DeleteExit=0; EditExit=0 }
$global:releasePolicyCliState = $state
function gh {
    $global:releasePolicyCliState.Calls.Add(@($args))
    if ($args[0] -eq 'api') { $global:LASTEXITCODE = $global:releasePolicyCliState.ApiExit; $global:releasePolicyCliState.Json; return }
    if ($args[0] -eq 'release' -and $args[1] -eq 'delete') { $global:LASTEXITCODE = $global:releasePolicyCliState.DeleteExit; return }
    if ($args[0] -eq 'release' -and $args[1] -eq 'edit') { $global:LASTEXITCODE = $global:releasePolicyCliState.EditExit; return }
    throw 'Unexpected GitHub operation in mock CLI'
}
try {
    $versionPath = Join-Path $fixture 'version.txt'; $environmentPath = Join-Path $fixture 'github-env.txt'
    [IO.File]::WriteAllText($versionPath, "# Fixture version`n1.2`n")
    $state.Json = '[[{"name":"v1.1.999"}],[{"name":"v1.2.1"},{"name":"v1.2.9"}]]'
    & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Prepare -Repository 'fixture/repo' -VersionFile $versionPath -EnvironmentFile $environmentPath
    $environmentText = [IO.File]::ReadAllText($environmentPath)
    Assert ($environmentText.Contains('UPDATER_BUILD_NUMBER=10') -and $environmentText.Contains('UPDATER_RELEASE_VERSION=1.2.10')) 'preparation reads all tag pages and exports the version used by MSBuild and release naming'
    Assert ($state.Calls[0] -contains '--paginate' -and $state.Calls[0] -contains '--slurp') 'tag and release discovery use complete paginated GitHub results'
    $state.Json = '[[]]'; [IO.File]::WriteAllText($versionPath, "1.3`n"); [IO.File]::WriteAllText($environmentPath, '')
    & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Prepare -Repository 'fixture/repo' -VersionFile $versionPath -EnvironmentFile $environmentPath
    Assert ([IO.File]::ReadAllText($environmentPath).Contains('UPDATER_RELEASE_VERSION=1.3.1')) 'the real preparation starts a previously unseen series at 1'
    $state.ApiExit = 1; [IO.File]::WriteAllText($environmentPath, '')
    ExpectFailure { & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Prepare -Repository 'fixture/repo' -VersionFile $versionPath -EnvironmentFile $environmentPath } 'API failure cannot reset a series counter to 1'
    Assert ([IO.File]::ReadAllText($environmentPath).Length -eq 0) 'failed preparation exports no partial build number'
    $state.ApiExit = 0; $state.Json = 'invalid-json'
    ExpectFailure { & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Prepare -Repository 'fixture/repo' -VersionFile $versionPath -EnvironmentFile $environmentPath } 'malformed API JSON stops preparation'
    foreach ($invalidResponse in @('null', '{}', '[null]', '[{}]', '[[null]]', '[[{"name":null}]]')) {
        $state.Json = $invalidResponse
        ExpectFailure { & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Prepare -Repository 'fixture/repo' -VersionFile $versionPath -EnvironmentFile $environmentPath } "invalid API response $invalidResponse cannot reset a series counter"
    }
    Assert ([IO.File]::ReadAllText($environmentPath).Length -eq 0) 'invalid response shapes export no partial build number'

    $state.Json = '[[{"tag_name":"v1.1.9","draft":false,"prerelease":false},{"tag_name":"v1.1.10","draft":false,"prerelease":false}],[{"tag_name":"v1.2.1","draft":false,"prerelease":false},{"tag_name":"v1.2.2","draft":false,"prerelease":false},{"tag_name":"v1.2.3","draft":false,"prerelease":false},{"tag_name":"v1.2.4","draft":false,"prerelease":false}]]'
    $state.Calls.Clear()
    & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Cleanup -Repository 'fixture/repo' -ExpectedVersion '1.2.4'
    $deletions = @($state.Calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'delete' })
    Assert ($deletions.Count -eq 2 -and @($deletions | ForEach-Object { $_[2] }) -contains 'v1.1.9' -and @($deletions | ForEach-Object { $_[2] }) -contains 'v1.2.1') 'real cleanup deletes only obsolete current builds and older-series predecessors'
    Assert (@($deletions | Where-Object { $_ -notcontains '--cleanup-tag' -or $_ -notcontains '--repo' -or $_ -notcontains 'fixture/repo' }).Count -eq 0) 'cleanup targets the explicit repository and removes only the corresponding obsolete tags'
    Assert (@($state.Calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'edit' -and $_[2] -eq 'v1.2.4' -and $_ -contains '--latest' }).Count -eq 1) 'the newest stable release is marked Latest before any obsolete release is deleted'
    $state.Calls.Clear()
    & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Cleanup -Repository 'fixture/repo' -ExpectedVersion '1.1.10'
    Assert (@($state.Calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'edit' -and $_[2] -eq 'v1.2.4' }).Count -eq 1) 'cleanup after an older-series rerun keeps Latest on the newer series'
    $state.Calls.Clear()
    ExpectFailure { & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Cleanup -Repository 'fixture/repo' -ExpectedVersion '1.3.1' } 'a missing newly published release stops cleanup'
    Assert (@($state.Calls | Where-Object { $_[0] -eq 'release' }).Count -eq 0) 'stale release lists cause no deletions'
    $state.ApiExit = 1
    ExpectFailure { & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Cleanup -Repository 'fixture/repo' -ExpectedVersion '1.2.4' } 'API failure stops cleanup before mutation'
    $state.ApiExit = 0; $state.DeleteExit = 1
    ExpectFailure { & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Cleanup -Repository 'fixture/repo' -ExpectedVersion '1.2.4' } 'a deletion failure is reported instead of silent success'
    $state.DeleteExit = 0; $state.EditExit = 1; $state.Calls.Clear()
    ExpectFailure { & (Join-Path $RepositoryRoot 'build\Release.ps1') -Mode Cleanup -Repository 'fixture/repo' -ExpectedVersion '1.2.4' } 'a failed Latest update stops cleanup'
    Assert (@($state.Calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'delete' }).Count -eq 0) 'failed Latest promotion causes no deletions'
} finally {
    Remove-Variable -Scope Global -Name releasePolicyCliState -ErrorAction SilentlyContinue
    if ($fixture.StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
Write-Host 'PASS release policy tests completed with no real GitHub mutations'
