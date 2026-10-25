param([switch]$LibraryOnly)

function ConvertTo-DirectorySearchPath([string]$distinguishedName) {
    $value = [Text.StringBuilder]::new()
    $slashes = 0
    foreach ($character in $distinguishedName.ToCharArray()) {
        if ($character -eq '/' -and $slashes % 2 -eq 0) { [void]$value.Append('\') }
        [void]$value.Append($character)
        if ($character -eq '\') { $slashes++ } else { $slashes = 0 }
    }
    $value.ToString()
}

function Invoke-DirectoryComputerSearch {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [scriptblock]$CreateEntry = {
            param($path)
            $authentication = [DirectoryServices.AuthenticationTypes]::Secure -bor [DirectoryServices.AuthenticationTypes]::Signing -bor [DirectoryServices.AuthenticationTypes]::Sealing
            [DirectoryServices.DirectoryEntry]::new($path, $null, $null, $authentication)
        },
        [scriptblock]$CreateSearcher = { param($entry) [DirectoryServices.DirectorySearcher]::new($entry) },
        [ValidateRange(1,10000)][int]$MaximumResults = 10000
    )
    $ErrorActionPreference = 'Stop'
    $domain = ([string]$Request.Domain).Trim()
    $base = ([string]$Request.SearchBase).Trim()
    $filter = ([string]$Request.Filter).Trim()
    if ($domain -and ($domain -match '[/\\:\s]' -or [Uri]::CheckHostName($domain) -eq [UriHostNameType]::Unknown)) { throw 'Ungültiger Domänenname.' }
    if ($base -match '://|[\x00\r\n]') { throw 'Ungültige LDAP-Suchbasis.' }
    if (-not $filter.StartsWith('(&(objectCategory=computer)') -or -not $filter.EndsWith(')')) { throw 'Der LDAP-Filter muss auf Computer beschränkt sein.' }
    $prefix = 'LDAP://'
    if ($domain) { $prefix += $domain + '/' }
    $rootDse = $null; $entry = $null; $searcher = $null; $results = $null
    try {
        $rootDse = & $CreateEntry ($prefix + 'RootDSE')
        if (-not $base) { $base = [string]$rootDse.Properties['defaultNamingContext'][0] }
        if (-not $base) { throw 'Es wurde keine Domäne gefunden. Bitte eine erreichbare Domäne oder einen Domaincontroller eintragen.' }
        $entry = & $CreateEntry ($prefix + (ConvertTo-DirectorySearchPath $base))
        $searcher = & $CreateSearcher $entry
        $searcher.Filter = $filter
        $searcher.SearchScope = [DirectoryServices.SearchScope]::Subtree
        $searcher.PageSize = 500
        $searcher.SizeLimit = 0
        $searcher.CacheResults = $false
        $searcher.ClientTimeout = [TimeSpan]::FromSeconds(25)
        $searcher.ServerTimeLimit = [TimeSpan]::FromSeconds(20)
        $searcher.ServerPageTimeLimit = [TimeSpan]::FromSeconds(10)
        $searcher.ReferralChasing = [DirectoryServices.ReferralChasingOption]::None
        foreach ($property in @('name','dnshostname','operatingsystem','description')) { [void]$searcher.PropertiesToLoad.Add($property) }
        $results = $searcher.FindAll()
        $computers = [Collections.Generic.List[object]]::new()
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $truncated = $false
        foreach ($result in $results) {
            $values = @{}
            foreach ($property in @('name','dnshostname','operatingsystem','description')) {
                $values[$property] = ''
                if ($result.Properties[$property].Count -gt 0) { $values[$property] = ([string]$result.Properties[$property][0]).Trim() }
            }
            $hostName = $values.dnshostname
            if (-not $hostName) { $hostName = $values.name }
            if (-not $hostName -or $hostName -match '\s' -or -not $seen.Add($hostName)) { continue }
            if ($computers.Count -ge $MaximumResults) { $truncated = $true; break }
            $computers.Add([pscustomobject]@{
                Name=$values.name; DnsName=$values.dnshostname; OperatingSystem=$values.operatingsystem; Description=$values.description
            })
        }
        [pscustomobject]@{ Success=$true; Computers=@($computers | Sort-Object Name,DnsName); SearchBase=$base; Truncated=$truncated; Error='' }
    } finally {
        if ($null -ne $results) { $results.Dispose() }
        if ($null -ne $searcher) { $searcher.Dispose() }
        if ($null -ne $entry) { $entry.Dispose() }
        if ($null -ne $rootDse) { $rootDse.Dispose() }
    }
}

if (-not $LibraryOnly) {
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        Add-Type -AssemblyName System.DirectoryServices
        $request = [Console]::In.ReadLine() | ConvertFrom-Json
        $result = Invoke-DirectoryComputerSearch -Request $request
        $result | ConvertTo-Json -Depth 6 -Compress | ForEach-Object { [Console]::Out.WriteLine($_) }
    } catch {
        [pscustomobject]@{ Success=$false; Computers=@(); SearchBase=''; Truncated=$false; Error=$_.Exception.Message } |
            ConvertTo-Json -Depth 3 -Compress | ForEach-Object { [Console]::Out.WriteLine($_) }
    }
}
