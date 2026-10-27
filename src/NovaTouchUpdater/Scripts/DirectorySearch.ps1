param([switch]$LibraryOnly)

function New-DirectorySearchEntry([string]$path) {
    # PowerShell converts $null arguments for string parameters to empty strings.
    # The four-argument constructor would therefore use empty credentials instead
    # of the logged-on Windows identity. Leave credentials unset entirely.
    $entry = [DirectoryServices.DirectoryEntry]::new($path)
    $entry.psbase.AuthenticationType = [DirectoryServices.AuthenticationTypes]::Secure -bor [DirectoryServices.AuthenticationTypes]::Signing -bor [DirectoryServices.AuthenticationTypes]::Sealing
    return $entry
}

function Close-DirectorySearchResource($resource) {
    if ($null -ne $resource) {
        # The ADSI adapter can perform another bind when resolving Dispose, even
        # after a failed search. psbase invokes the managed method directly.
        # Cleanup must neither hide the search error nor invalidate its results.
        try { $resource.psbase.Dispose() } catch { }
    }
}

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
            New-DirectorySearchEntry $path
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
    $step = 'Domäne ermitteln'
    $missingContext = $false
    $path = $prefix + 'RootDSE'
    try {
        if (-not $base) {
            $rootDse = & $CreateEntry $path
            # Bind explicitly: PowerShell can turn an exception from a property
            # getter into $null, losing the real LDAP error during indexing.
            $rootDse.psbase.RefreshCache(@('defaultNamingContext'))
            $base = [string]$rootDse.psbase.Properties['defaultNamingContext'][0]
        }
        if (-not $base) {
            $missingContext = $true
            throw 'Es wurde keine Domäne gefunden. Bitte eine erreichbare Domäne oder einen Domaincontroller eintragen.'
        }
        $step = 'Computer suchen'
        $path = $prefix + (ConvertTo-DirectorySearchPath $base)
        $entry = & $CreateEntry $path
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
        $step = 'Suchergebnisse lesen'
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
    } catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        $detail = $failure.Message.Trim()
        if ($failure -is [DirectoryServices.DirectoryServicesCOMException]) {
            $extended = $failure.ExtendedErrorMessage
            if ($extended -and -not $detail.Contains($extended.Trim())) { $detail += ' ' + $extended.Trim() }
        }
        if ($failure -is [Runtime.InteropServices.COMException]) { $detail += ' (0x{0:X8})' -f $failure.ErrorCode }
        $reported = [InvalidOperationException]::new(($step + ' [' + $path + ']: ' + $detail), $failure)
        # Only failed domain discovery allows the automatic network fallback.
        # Access-denied errors and unsuccessful computer queries stay LDAP errors.
        $reported.Data['DomainUnavailable'] = ($step -eq 'Domäne ermitteln' -and
            ($missingContext -or $failure.HResult -eq -2147023541 -or $failure.HResult -eq -2147016646))
        throw $reported
    } finally {
        Close-DirectorySearchResource $results
        Close-DirectorySearchResource $searcher
        Close-DirectorySearchResource $entry
        Close-DirectorySearchResource $rootDse
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
        $failure = $_.Exception
        $domainUnavailable = $false
        while ($null -ne $failure) {
            if ($failure.Data['DomainUnavailable'] -eq $true) { $domainUnavailable = $true }
            $failure = $failure.InnerException
        }
        [pscustomobject]@{ Success=$false; Computers=@(); SearchBase=''; Truncated=$false; Error=$_.Exception.Message; DomainUnavailable=$domainUnavailable } |
            ConvertTo-Json -Depth 3 -Compress | ForEach-Object { [Console]::Out.WriteLine($_) }
    }
}
