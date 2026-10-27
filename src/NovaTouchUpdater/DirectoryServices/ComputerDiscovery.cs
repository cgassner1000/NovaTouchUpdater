using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NovaTouchUpdater;

internal enum ComputerSearchSource { Automatic, Domain, LocalNetwork }
internal sealed record ComputerSearchOptions(ComputerSearchSource Source, string Domain, string SearchBase,
    string Text, bool CustomFilter, string NetworkRange);

internal sealed class ComputerDiscovery
{
    private readonly Func<LdapSearchRequest, CancellationToken, Task<LdapSearchResult>> directorySearch;
    private readonly Func<NetworkSearchRequest, IProgress<string>?, CancellationToken, Task<LdapSearchResult>> networkSearch;
    private readonly Func<CancellationToken, Task<bool>> domainAvailable;

    internal ComputerDiscovery(
        Func<LdapSearchRequest, CancellationToken, Task<LdapSearchResult>>? directorySearch = null,
        Func<NetworkSearchRequest, IProgress<string>?, CancellationToken, Task<LdapSearchResult>>? networkSearch = null,
        Func<CancellationToken, Task<bool>>? domainAvailable = null)
    {
        this.directorySearch = directorySearch ?? LdapComputerSearch.SearchAsync;
        this.networkSearch = networkSearch ?? new NetworkComputerSearch().SearchAsync;
        this.domainAvailable = domainAvailable ?? (ct => Task.Run(WindowsDomain.IsJoined, ct));
    }

    internal async Task<LdapSearchResult> SearchAsync(ComputerSearchOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool automatic = options.Source == ComputerSearchSource.Automatic;
        bool explicitDomain = !string.IsNullOrWhiteSpace(options.Domain) || !string.IsNullOrWhiteSpace(options.SearchBase);
        bool local = options.Source == ComputerSearchSource.LocalNetwork;
        if (!local && automatic && !explicitDomain)
        {
            progress?.Report("Domäne wird ermittelt …");
            local = !await domainAvailable(ct).ConfigureAwait(false);
        }
        if (local) return await SearchNetworkAsync(options, progress, ct).ConfigureAwait(false);

        var request = LdapComputerQuery.Create(options.Domain, options.SearchBase, options.Text, options.CustomFilter);
        progress?.Report("Domäne wird durchsucht …");
        try { return await directorySearch(request, ct).ConfigureAwait(false); }
        catch (LdapDomainUnavailableException) when (automatic && !explicitDomain && !options.CustomFilter)
        {
            return await SearchNetworkAsync(options, progress, ct).ConfigureAwait(false);
        }
    }

    private Task<LdapSearchResult> SearchNetworkAsync(ComputerSearchOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (options.CustomFilter)
            throw new ArgumentException("LDAP-Filter benötigen eine Domäne. Für das lokale Netzwerk bitte „Namensmuster“ verwenden.");
        progress?.Report(options.Source == ComputerSearchSource.Automatic
            ? "Keine Domäne gefunden · lokales Netzwerk wird durchsucht …"
            : "Lokales Netzwerk wird durchsucht …");
        return networkSearch(new(options.Text, options.NetworkRange), progress, ct);
    }
}

internal static class WindowsDomain
{
    internal static bool IsJoined()
    {
        IntPtr name = IntPtr.Zero;
        try
        {
            int result = NetGetJoinInformation(null, out name, out int status);
            if (result != 0) throw new Win32Exception(result, "Die Windows-Domänenzugehörigkeit konnte nicht ermittelt werden.");
            return status == 3; // NetSetupDomainName; workgroup membership is not a domain.
        }
        finally { if (name != IntPtr.Zero) NetApiBufferFree(name); }
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);
    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
