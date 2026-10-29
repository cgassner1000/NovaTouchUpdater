param(
    [Parameter(Mandatory=$true)][ValidateSet('Prepare','Cleanup')][string]$Mode,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$VersionFile = (Join-Path $PSScriptRoot '..\version.txt'),
    [string]$EnvironmentFile = $env:GITHUB_ENV,
    [string]$ExpectedVersion = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ReleasePolicy.ps1')
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Kein gültiges GitHub-Repository angegeben.' }

function ReadGithubReleasePages([string]$Endpoint) {
    $json = gh api --paginate $Endpoint --slurp
    if ($LASTEXITCODE -ne 0) { throw "GitHub-Abfrage fehlgeschlagen: $Endpoint" }
    $jsonText = $json -join "`n"
    if ([string]::IsNullOrWhiteSpace($jsonText)) { throw 'GitHub hat keine JSON-Antwort geliefert.' }
    # A wrapper preserves empty and single-item arrays in both PowerShell 5 and 7.
    $response = ('{"pages":' + $jsonText + '}') | ConvertFrom-Json
    if ($response.pages -isnot [Array]) { throw 'GitHub hat keine Liste paginierter Ergebnisse geliefert.' }
    foreach ($page in $response.pages) {
        if ($page -isnot [Array]) { throw 'GitHub hat eine ungültige Ergebnisseite geliefert.' }
        foreach ($item in $page) {
            if ($null -eq $item) { throw 'GitHub hat einen leeren Datensatz geliefert.' }
            $item
        }
    }
}

if ($Mode -eq 'Prepare') {
    if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) { throw 'GITHUB_ENV fehlt; Release-Version kann nicht an den Build übergeben werden.' }
    $baseVersion = ReadUpdaterBaseVersion $VersionFile
    $tags = @(ReadGithubReleasePages "repos/$Repository/tags?per_page=100")
    foreach ($tag in $tags) {
        if ($null -eq $tag.PSObject.Properties['name'] -or $tag.name -isnot [string] -or [string]::IsNullOrWhiteSpace($tag.name)) {
            throw 'Unvollständige GitHub-Tag-Daten; Buildnummer kann nicht bestimmt werden.'
        }
    }
    $number = GetNextUpdaterBuildNumber $baseVersion @($tags | ForEach-Object { [string]$_.name })
    [IO.File]::AppendAllLines($EnvironmentFile, [string[]]@(
        "UPDATER_BUILD_NUMBER=$number", "UPDATER_RELEASE_VERSION=$baseVersion.$number"
    ), [Text.UTF8Encoding]::new($false))
    Write-Host "Release-Version: $baseVersion.$number"
} else {
    $expected = ConvertToUpdaterReleaseTag "v$ExpectedVersion"
    if ($null -eq $expected) { throw 'Die erwartete veröffentlichte Release-Version fehlt.' }
    $releases = @(ReadGithubReleasePages "repos/$Repository/releases?per_page=100")
    $plan = GetUpdaterReleaseRetentionPlan $releases
    if (-not @($releases | Where-Object { $_.tag_name -ceq $expected.Tag -and -not $_.draft -and -not $_.prerelease }).Count) {
        throw 'Der gerade veröffentlichte Release ist nicht in der GitHub-Antwort enthalten; keine Bereinigung.'
    }
    Write-Host ("Behalten: " + ($plan.Keep -join ', '))
    # An older-series rerun must not replace the newest stable update offered
    # by the application's /releases/latest endpoint.
    gh release edit $plan.LatestTag --repo $Repository --latest
    if ($LASTEXITCODE -ne 0) { throw 'Der neueste stabile Release konnte nicht als Latest markiert werden; keine Bereinigung.' }
    foreach ($tag in $plan.Delete) {
        gh release delete $tag --repo $Repository --yes --cleanup-tag
        if ($LASTEXITCODE -ne 0) { throw "Löschen fehlgeschlagen: $tag" }
    }
}
