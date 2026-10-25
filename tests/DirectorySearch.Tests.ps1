$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\DirectorySearch.ps1') -LibraryOnly
Add-Type -AssemblyName System.DirectoryServices
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public class LdapFixtureEntry : IDisposable {
    public string Path;
    public bool Disposed;
    public Dictionary<string,List<string>> Properties = new Dictionary<string,List<string>>();
    public void Dispose() { Disposed = true; }
}
public class LdapFixtureResults : List<LdapFixtureEntry>, IDisposable {
    public bool Disposed;
    public void Dispose() { Disposed = true; }
}
public class LdapFixtureSearcher : IDisposable {
    public string Filter;
    public object SearchScope;
    public int PageSize, SizeLimit;
    public bool CacheResults, Disposed, Fail;
    public TimeSpan ClientTimeout, ServerTimeLimit, ServerPageTimeLimit;
    public object ReferralChasing;
    public List<string> PropertiesToLoad = new List<string>();
    public LdapFixtureResults Results = new LdapFixtureResults();
    public LdapFixtureResults FindAll() { if (Fail) throw new InvalidOperationException("simulated LDAP error"); return Results; }
    public void Dispose() { Disposed = true; }
}
'@

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    Write-Host "PASS $message"
}
function Set-FixtureProperty($item, [string]$name, [string]$value) {
    $list = [Collections.Generic.List[string]]::new()
    $list.Add($value)
    $item.Properties[$name] = $list
}
function New-Fixture([int]$count) {
    $state = @{ Entries=[Collections.Generic.List[LdapFixtureEntry]]::new(); Searcher=[LdapFixtureSearcher]::new(); Context='DC=example,DC=test' }
    for ($i=1; $i -le $count; $i++) {
        $item = [LdapFixtureEntry]::new()
        Set-FixtureProperty $item 'name' ('CUSTOM-' + $i)
        Set-FixtureProperty $item 'dnshostname' ('custom-' + $i + '.example.test')
        Set-FixtureProperty $item 'operatingsystem' 'Windows test fixture'
        Set-FixtureProperty $item 'description' 'synthetic data only'
        $state.Searcher.Results.Add($item)
    }
    return $state
}
$createEntry = {
    param($path)
    $entry = [LdapFixtureEntry]::new()
    $entry.Path = $path
    Set-FixtureProperty $entry 'defaultNamingContext' $script:fixture.Context
    $script:fixture.Entries.Add($entry)
    return $entry
}
$createSearcher = { param($entry) return $script:fixture.Searcher }
$request = [pscustomobject]@{ Domain=''; SearchBase=''; Filter='(&(objectCategory=computer)(name=CUSTOM-*))' }

$script:fixture = New-Fixture 1201
$result = Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher
Assert ($result.Success -and $result.Computers.Count -eq 1201 -and -not $result.Truncated) 'paged search returns more than the usual 1000 AD entries without silently truncating'
Assert ($fixture.Searcher.Filter -eq $request.Filter -and $fixture.Searcher.PageSize -eq 500 -and $fixture.Searcher.SizeLimit -eq 0) 'the arbitrary LDAP filter and paging configuration reach DirectorySearcher'
Assert ($fixture.Entries[0].Path -eq 'LDAP://RootDSE' -and $fixture.Entries[1].Path -eq 'LDAP://DC=example,DC=test') 'blank domain and base use the current domain naming context'
Assert ($fixture.Searcher.SearchScope -eq [DirectoryServices.SearchScope]::Subtree -and $fixture.Searcher.ReferralChasing -eq [DirectoryServices.ReferralChasingOption]::None) 'search covers the selected subtree without following other-domain referrals'
Assert ($fixture.Searcher.ClientTimeout.TotalSeconds -eq 25 -and $fixture.Searcher.ServerTimeLimit.TotalSeconds -eq 20) 'directory operations have bounded client and server timeouts'
Assert ($fixture.Searcher.Disposed -and $fixture.Searcher.Results.Disposed -and @($fixture.Entries | Where-Object { -not $_.Disposed }).Count -eq 0) 'search results, searcher and all bound directory entries are disposed after success'

$script:fixture = New-Fixture 3
$request.Domain = 'dc.branch.example.test'
$request.SearchBase = 'OU=POS/Terminals,DC=branch,DC=example,DC=test'
$result = Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher -MaximumResults 2
Assert ($result.Computers.Count -eq 2 -and $result.Truncated) 'result cap is explicitly reported instead of hiding missing matches'
Assert ($fixture.Entries[1].Path -eq 'LDAP://dc.branch.example.test/OU=POS\/Terminals,DC=branch,DC=example,DC=test') 'custom domain controller and OU are used and ADSI path slashes are escaped'
Assert ((ConvertTo-DirectorySearchPath 'OU=POS\/Terminals,DC=example,DC=test') -eq 'OU=POS\/Terminals,DC=example,DC=test') 'already escaped DN slash is preserved'

$script:fixture = New-Fixture 2
$duplicate = [LdapFixtureEntry]::new()
Set-FixtureProperty $duplicate 'name' 'DUPLICATE'
Set-FixtureProperty $duplicate 'dnshostname' 'CUSTOM-1.EXAMPLE.TEST'
$fixture.Searcher.Results.Add($duplicate)
$result = Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher -MaximumResults 2
Assert ($result.Computers.Count -eq 2 -and -not $result.Truncated) 'duplicate DNS names do not produce duplicate hosts or a false truncation warning'

$script:fixture = New-Fixture 1
$fixture.Searcher.Fail = $true
$failed = $false
try { Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher | Out-Null }
catch { $failed = $_.Exception.Message -like '*simulated LDAP error*' }
Assert ($failed -and $fixture.Searcher.Disposed -and @($fixture.Entries | Where-Object { -not $_.Disposed }).Count -eq 0) 'LDAP errors remain errors and still dispose bound resources'

$script:fixture = New-Fixture 0
$result = Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher
Assert ($result.Success -and $result.Computers.Count -eq 0 -and -not $result.Truncated) 'empty result is successful and remains a JSON array'
