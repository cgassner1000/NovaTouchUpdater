# Pure release/version decisions. No GitHub calls or filesystem mutations.
function ReadUpdaterBaseVersion([string]$Path) {
    $text = [regex]::Replace([IO.File]::ReadAllText($Path), '(?m)^\s*#.*$', '').Trim()
    if ($text -notmatch '^\d+\.\d+$') { throw 'version.txt muss genau eine Basisversion im Format Hauptversion.Nebenversion enthalten.' }
    return $text
}

function ConvertToUpdaterReleaseTag([string]$Tag) {
    $version = $null
    if ($Tag -cmatch '^v\d+\.\d+\.\d+$' -and [version]::TryParse($Tag.Substring(1), [ref]$version)) {
        return [pscustomobject]@{ Tag=$Tag; Version=$version; Series=$version.ToString(2) }
    }
}

function GetNextUpdaterBuildNumber([string]$BaseVersion, [string[]]$Tags) {
    $base = [version]$BaseVersion
    $highest = 0
    foreach ($tag in $Tags) {
        $parsed = ConvertToUpdaterReleaseTag $tag
        if ($null -ne $parsed -and $parsed.Version.Major -eq $base.Major -and $parsed.Version.Minor -eq $base.Minor) {
            $highest = [Math]::Max($highest, $parsed.Version.Build)
        }
    }
    if ($highest -ge 65534) { throw 'Die Buildnummer dieser Versionsreihe ist ausgeschöpft. Basisversion in version.txt erhöhen.' }
    return $highest + 1
}

function GetUpdaterReleaseRetentionPlan([object[]]$Releases) {
    $eligible = @{}; $protected = @{}
    foreach ($release in $Releases) {
        foreach ($property in @('tag_name', 'draft', 'prerelease')) {
            if ($null -eq $release -or $null -eq $release.PSObject.Properties[$property]) { throw 'Unvollständige GitHub-Release-Daten; keine Bereinigung möglich.' }
        }
        if ($release.draft -isnot [bool] -or $release.prerelease -isnot [bool]) { throw 'Ungültige GitHub-Release-Daten; keine Bereinigung möglich.' }
        $tag = [string]$release.tag_name
        $parsed = ConvertToUpdaterReleaseTag $tag
        if ($release.draft -or $release.prerelease -or $null -eq $parsed) { $protected[$tag] = $true; continue }
        $eligible[$tag] = $parsed
    }
    # Use the highest published series, even if an older workflow is rerun later.
    $ordered = @($eligible.Values | Sort-Object Version -Descending)
    $current = if ($ordered.Count -gt 0) { $ordered[0].Series } else { '' }
    $keep = [Collections.Generic.List[string]]::new()
    $delete = [Collections.Generic.List[string]]::new()
    foreach ($tag in $protected.Keys) { $keep.Add($tag) }
    foreach ($group in @($ordered | Group-Object Series)) {
        $limit = if ($group.Name -eq $current) { 3 } else { 1 }
        $index = 0
        foreach ($release in @($group.Group | Sort-Object Version -Descending)) {
            if ($index++ -lt $limit) { $keep.Add($release.Tag) } else { $delete.Add($release.Tag) }
        }
    }
    return [pscustomobject]@{ CurrentSeries=$current; LatestTag=$(if ($ordered.Count -gt 0) { $ordered[0].Tag } else { '' }); Keep=@($keep); Delete=@($delete) }
}
