using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NovaTouchUpdater;

internal enum ComputerSearchSource { Automatic, Domain, LocalNetwork }
internal sealed record ComputerSearchOptions(ComputerSearchSource Source, string Domain, string SearchBase,
    string Text, bool CustomFilter, string NetworkRange);

internal sealed class ComputerDiscovery
{
    private readonly Func<LdapSearchRequest, CancellationToken, IProgress<LdapComputer>?, Task<LdapSearchResult>> directorySearch;
    private readonly Func<NetworkSearchRequest, IProgress<string>?, CancellationToken, IProgress<LdapComputer>?, Task<LdapSearchResult>> networkSearch;
    private readonly Func<CancellationToken, Task<bool>> domainAvailable;
    private readonly Func<string, CancellationToken, Task<string>> resolveAddress;

    internal ComputerDiscovery(
        Func<LdapSearchRequest, CancellationToken, IProgress<LdapComputer>?, Task<LdapSearchResult>>? directorySearch = null,
        Func<NetworkSearchRequest, IProgress<string>?, CancellationToken, IProgress<LdapComputer>?, Task<LdapSearchResult>>? networkSearch = null,
        Func<CancellationToken, Task<bool>>? domainAvailable = null,
        Func<string, CancellationToken, Task<string>>? resolveAddress = null)
    {
        this.directorySearch = directorySearch ?? LdapComputerSearch.SearchAsync;
        this.networkSearch = networkSearch ?? new NetworkComputerSearch().SearchAsync;
        this.domainAvailable = domainAvailable ?? (ct => Task.Run(WindowsDomain.IsJoined, ct));
        this.resolveAddress = resolveAddress ?? ResolveAddressAsync;
    }

    internal async Task<LdapSearchResult> SearchAsync(ComputerSearchOptions options, IProgress<string>? progress, CancellationToken ct, IProgress<LdapComputer>? found = null)
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
        if (local) return await SearchNetworkAsync(options, progress, ct, found).ConfigureAwait(false);

        var request = LdapComputerQuery.Create(options.Domain, options.SearchBase, options.Text, options.CustomFilter);
        progress?.Report("Domäne wird durchsucht …");
        try { return await SearchDirectoryAsync(request, progress, ct, found).ConfigureAwait(false); }
        catch (LdapDomainUnavailableException) when (automatic && !explicitDomain && !options.CustomFilter)
        {
            return await SearchNetworkAsync(options, progress, ct, found).ConfigureAwait(false);
        }
    }

    private Task<LdapSearchResult> SearchNetworkAsync(ComputerSearchOptions options, IProgress<string>? progress, CancellationToken ct, IProgress<LdapComputer>? found)
    {
        ct.ThrowIfCancellationRequested();
        if (options.CustomFilter)
            throw new ArgumentException("LDAP-Filter benötigen eine Domäne. Für das lokale Netzwerk bitte „Namensmuster“ verwenden.");
        progress?.Report(options.Source == ComputerSearchSource.Automatic
            ? "Keine Domäne gefunden · lokales Netzwerk wird durchsucht …"
            : "Lokales Netzwerk wird durchsucht …");
        return networkSearch(new(options.Text, options.NetworkRange), progress, ct, found);
    }

    private async Task<LdapSearchResult> SearchDirectoryAsync(LdapSearchRequest request, IProgress<string>? progress, CancellationToken ct, IProgress<LdapComputer>? found)
    {
        using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var concurrency = new SemaphoreSlim(32);
        var pending = new Dictionary<string, Task<LdapComputer>>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        bool accepting = true;
        async Task<LdapComputer> EnrichAsync(LdapComputer computer)
        {
            if (!string.IsNullOrWhiteSpace(computer.IpAddress)) return computer;
            try
            {
                await concurrency.WaitAsync(lookupCancellation.Token).ConfigureAwait(false);
                try
                {
                    using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(lookupCancellation.Token);
                    lookupTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    string address = await resolveAddress(computer.Host, lookupTimeout.Token).WaitAsync(lookupTimeout.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        computer = computer with { IpAddress = address };
                        if (!lookupCancellation.IsCancellationRequested) found?.Report(computer);
                    }
                }
                finally { concurrency.Release(); }
            }
            catch (OperationCanceledException) { /* Keep the computer even when address lookup expires. */ }
            catch (Exception ex) when (ex is SocketException or ArgumentException) { /* Missing DNS is not a failed computer search. */ }
            return computer;
        }
        Task<LdapComputer> Record(LdapComputer computer)
        {
            lock (gate)
            {
                if (!accepting || ct.IsCancellationRequested) return Task.FromResult(computer);
                if (pending.TryGetValue(computer.Host, out var existing)) return existing;
                found?.Report(computer); // Show the name immediately, before any DNS lookup.
                Task<LdapComputer> lookup = EnrichAsync(computer);
                pending.Add(computer.Host, lookup);
                return lookup;
            }
        }
        try
        {
            var result = await directorySearch(request, ct, new ComputerProgress(computer => { _ = Record(computer); })).ConfigureAwait(false);
            Task<LdapComputer>[] final = (result.Computers ?? []).Select(Record).ToArray();
            if (final.Any(task => !task.IsCompleted)) progress?.Report("Computer gefunden · IP-Adressen werden ermittelt …");
            // Large directories and missing DNS must not postpone completion indefinitely.
            lookupCancellation.CancelAfter(TimeSpan.FromSeconds(15));
            LdapComputer[] computers = await Task.WhenAll(final).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result with { Computers = computers };
        }
        finally
        {
            Task<LdapComputer>[] remaining;
            lock (gate) { accepting = false; remaining = pending.Values.ToArray(); }
            lookupCancellation.Cancel();
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveAddressAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var ipv4 = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        return string.Join(", ", (ipv4.Length > 0 ? ipv4 : addresses).Select(address => address.ToString()).Distinct());
    }

    private sealed class ComputerProgress(Action<LdapComputer> report) : IProgress<LdapComputer>
    {
        public void Report(LdapComputer value) => report(value);
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
