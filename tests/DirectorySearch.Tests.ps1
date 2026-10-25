$ErrorActionPreference = 'Stop'
$workerSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\DirectorySearch.ps1'), [Text.Encoding]::UTF8)
. ([scriptblock]::Create($workerSource)) -LibraryOnly
Add-Type -AssemblyName System.DirectoryServices
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public class LdapFixtureEntry : IDisposable {
    public string Path;
    public bool Disposed, FailDispose, FailReadProperties;
    public string[] RefreshedProperties;
    private Dictionary<string,List<string>> properties = new Dictionary<string,List<string>>();
    public Dictionary<string,List<string>> Properties {
        get { if (FailReadProperties) throw new InvalidOperationException("simulated naming context error"); return properties; }
    }
    public void RefreshCache(string[] names) {
        RefreshedProperties = names;
        if (FailReadProperties) throw new InvalidOperationException("simulated naming context error");
    }
    public void Dispose() { Disposed = true; if (FailDispose) throw new InvalidOperationException("simulated entry cleanup failure"); }
}
public class LdapFixtureResults : List<LdapFixtureEntry>, IDisposable {
    public bool Disposed, FailDispose;
    public void Dispose() { Disposed = true; if (FailDispose) throw new InvalidOperationException("simulated results cleanup failure"); }
}
public class LdapFixtureSearcher : IDisposable {
    public string Filter;
    public object SearchScope;
    public int PageSize, SizeLimit;
    public bool CacheResults, Disposed, Fail, FailCom, FailDispose;
    public TimeSpan ClientTimeout, ServerTimeLimit, ServerPageTimeLimit;
    public object ReferralChasing;
    public List<string> PropertiesToLoad = new List<string>();
    public LdapFixtureResults Results = new LdapFixtureResults();
    public LdapFixtureResults FindAll() {
        if (Fail) throw new InvalidOperationException("simulated LDAP error");
        if (FailCom) throw new System.Runtime.InteropServices.COMException("simulated LDAP bind error", unchecked((int)0x80072020));
        return Results;
    }
    public void Dispose() { Disposed = true; if (FailDispose) throw new InvalidOperationException("simulated searcher cleanup failure"); }
}
'@

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    Write-Host "PASS $message"
}

# A deliberately unknown ADSI provider exercises the real PowerShell
# DirectoryEntry adapter without contacting a domain, DNS server or host.
$offlineEntry = New-DirectorySearchEntry 'NovaTouchOfflineFixture://no-network'
try {
    Assert ($offlineEntry -is [DirectoryServices.DirectoryEntry] -and $null -eq $offlineEntry.psbase.Username) 'real directory entries leave credentials unset to use the current Windows identity'
    $expectedAuthentication = [DirectoryServices.AuthenticationTypes]::Secure -bor [DirectoryServices.AuthenticationTypes]::Signing -bor [DirectoryServices.AuthenticationTypes]::Sealing
    Assert ($offlineEntry.psbase.AuthenticationType -eq $expectedAuthentication) 'current-identity binding retains secure authentication, signing and sealing'
    $script:offlineDisposed = $false
    $offlineEntry.psbase.add_Disposed([EventHandler]{ $script:offlineDisposed = $true })
    $cleanupOutput = @(Close-DirectorySearchResource $offlineEntry)
    Assert ($script:offlineDisposed -and $cleanupOutput.Count -eq 0) 'real ADSI cleanup calls the managed Dispose method without another bind or protocol output'
} finally { $offlineEntry.psbase.Dispose() }

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
    $entry.FailDispose = [bool]$script:fixture.FailEntryDispose
    $entry.FailReadProperties = [bool]$script:fixture.FailRootRead
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
Assert ($fixture.Entries[0].RefreshedProperties.Count -eq 1 -and $fixture.Entries[0].RefreshedProperties[0] -eq 'defaultNamingContext') 'domain discovery binds explicitly so native errors are not swallowed by a property getter'
Assert ($fixture.Searcher.SearchScope -eq [DirectoryServices.SearchScope]::Subtree -and $fixture.Searcher.ReferralChasing -eq [DirectoryServices.ReferralChasingOption]::None) 'search covers the selected subtree without following other-domain referrals'
Assert ($fixture.Searcher.ClientTimeout.TotalSeconds -eq 25 -and $fixture.Searcher.ServerTimeLimit.TotalSeconds -eq 20) 'directory operations have bounded client and server timeouts'
Assert ($fixture.Searcher.Disposed -and $fixture.Searcher.Results.Disposed -and @($fixture.Entries | Where-Object { -not $_.Disposed }).Count -eq 0) 'search results, searcher and all bound directory entries are disposed after success'

$script:fixture = New-Fixture 3
$request.Domain = 'dc.branch.example.test'
$request.SearchBase = 'OU=POS/Terminals,DC=branch,DC=example,DC=test'
$result = Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher -MaximumResults 2
Assert ($result.Computers.Count -eq 2 -and $result.Truncated) 'result cap is explicitly reported instead of hiding missing matches'
Assert ($fixture.Entries.Count -eq 1 -and $fixture.Entries[0].Path -eq 'LDAP://dc.branch.example.test/OU=POS\/Terminals,DC=branch,DC=example,DC=test') 'custom domain controller and OU bypass unnecessary RootDSE lookup and ADSI path slashes are escaped'
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

$request.SearchBase = ''
$script:fixture = New-Fixture 1
$fixture.FailEntryDispose = $true
$fixture.Searcher.FailDispose = $true
$fixture.Searcher.Results.FailDispose = $true
$result = Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher
Assert ($result.Success -and $result.Computers.Count -eq 1) 'cleanup failures cannot turn a successful search into an error'
Assert ($fixture.Searcher.Results.Disposed -and $fixture.Searcher.Disposed -and $fixture.Entries.Count -eq 2 -and @($fixture.Entries | Where-Object { -not $_.Disposed }).Count -eq 0) 'every resource is cleaned up independently even if all Dispose methods throw'

$script:fixture = New-Fixture 1
$fixture.FailEntryDispose = $true
$fixture.Searcher.FailDispose = $true
$fixture.Searcher.Fail = $true
$message = ''
try { Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher | Out-Null }
catch { $message = $_.Exception.Message }
Assert ($message -like '*simulated LDAP error*' -and $message -notlike '*cleanup*' -and $message.Contains('LDAP://dc.branch.example.test/DC=example,DC=test')) 'search failure keeps the original cause and target even if disposal also fails'
Assert ($fixture.Searcher.Disposed -and @($fixture.Entries | Where-Object { -not $_.Disposed }).Count -eq 0) 'remaining resources are still disposed after a search and cleanup failure'

$script:fixture = New-Fixture 1
$fixture.Searcher.FailCom = $true
$message = ''
try { Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher | Out-Null }
catch { $message = $_.Exception.Message }
Assert ($message -like '*simulated LDAP bind error*' -and $message.Contains('0x80072020') -and $message.Contains('LDAP://dc.branch.example.test/DC=example,DC=test')) 'native LDAP error code and search target survive PowerShell invocation wrappers'

$script:fixture = New-Fixture 1
$fixture.FailRootRead = $true
$fixture.FailEntryDispose = $true
$message = ''
try { Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher | Out-Null }
catch { $message = $_.Exception.Message }
Assert ($message -like '*simulated naming context error*' -and $message.Contains('LDAP://dc.branch.example.test/RootDSE')) 'RootDSE discovery failures retain their original cause and stage'
Assert ($fixture.Entries.Count -eq 1 -and $fixture.Entries[0].Disposed) 'failed domain discovery still disposes its directory entry'

$script:offlineDisposed = $false
$offlineFactory = {
    param($path)
    $item = New-DirectorySearchEntry 'NovaTouchOfflineFixture://no-network'
    $item.psbase.add_Disposed([EventHandler]{ $script:offlineDisposed = $true })
    return $item
}
$message = ''
try {
    Invoke-DirectoryComputerSearch $request -CreateEntry $offlineFactory -CreateSearcher { throw 'search must not start after a failed bind' } | Out-Null
} catch { $message = $_.Exception.Message }
Assert ($message.Contains('LDAP://dc.branch.example.test/RootDSE') -and $message.Contains('0x80005000') -and $message -notlike '*Dispose*') 'real ADSI bind failure keeps its native error and lookup stage without contacting a domain'
Assert ($script:offlineDisposed) 'real directory entry is disposed after a bind failure without a second bind'

$script:fixture = New-Fixture 0
$result = Invoke-DirectoryComputerSearch $request -CreateEntry $createEntry -CreateSearcher $createSearcher
Assert ($result.Success -and $result.Computers.Count -eq 0 -and -not $result.Truncated) 'empty result is successful and remains a JSON array'
