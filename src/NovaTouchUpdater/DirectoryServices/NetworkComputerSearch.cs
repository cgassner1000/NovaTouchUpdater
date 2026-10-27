using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NovaTouchUpdater;

internal sealed record NetworkSearchRequest(string Pattern, string Range);

internal sealed record IPv4Subnet(uint Network, int Prefix)
{
    internal ulong AddressCount => Prefix < 31 ? (1UL << (32 - Prefix)) - 2 : 1UL << (32 - Prefix);
    internal static IPv4Subnet Parse(string text)
    {
        string[] parts = text.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out int prefix) || prefix is < 0 or > 32)
            throw new ArgumentException("Bitte einen IPv4-Bereich mit Präfix eingeben, z. B. 192.168.1.0/24. Mehrere Bereiche mit Komma trennen.");
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return new(BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()) & mask, prefix);
    }

    internal IEnumerable<IPAddress> Addresses()
    {
        ulong first = Network + (Prefix < 31 ? 1UL : 0UL);
        for (ulong i = 0; i < AddressCount; i++)
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(first + i));
            yield return new IPAddress(bytes);
        }
    }

    public override string ToString()
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, Network);
        return $"{new IPAddress(bytes)}/{Prefix}";
    }
}

internal sealed class NetworkComputerSearch
{
    internal const int MaximumAddresses = 4096;
    private readonly Func<IReadOnlyList<IPv4Subnet>> localSubnets;
    private readonly Func<IPAddress, CancellationToken, Task<LdapComputer?>> probe;

    internal NetworkComputerSearch(Func<IReadOnlyList<IPv4Subnet>>? localSubnets = null,
        Func<IPAddress, CancellationToken, Task<LdapComputer?>>? probe = null)
    {
        this.localSubnets = localSubnets ?? GetLocalSubnets;
        this.probe = probe ?? ProbeAsync;
    }

    internal static Regex NamePattern(string pattern)
    {
        pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        // Like the LDAP name-pattern mode, only * is special; ? and brackets stay literal.
        return new("\\A" + Regex.Escape(pattern).Replace("\\*", ".*") + "\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    }

    internal async Task<LdapSearchResult> SearchAsync(NetworkSearchRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Regex pattern = NamePattern(request.Pattern);
        IReadOnlyList<IPv4Subnet> subnets = string.IsNullOrWhiteSpace(request.Range) ? localSubnets()
            : request.Range.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IPv4Subnet.Parse).Distinct().ToArray();
        if (subnets.Count == 0) throw new ArgumentException("Kein aktives IPv4-Netzwerk gefunden. Bitte einen IP-Bereich eintragen.");
        var addresses = new HashSet<IPAddress>();
        foreach (IPv4Subnet subnet in subnets)
        {
            if (subnet.AddressCount > MaximumAddresses)
                throw new ArgumentException($"Der Netzwerkbereich {subnet} ist zu groß. Bitte die Suche auf höchstens {MaximumAddresses} Adressen eingrenzen, z. B. mit /24.");
            foreach (IPAddress address in subnet.Addresses())
                if (addresses.Add(address) && addresses.Count > MaximumAddresses)
                    throw new ArgumentException($"Die Netzwerkbereiche enthalten mehr als {MaximumAddresses} Adressen. Bitte den IP-Bereich eingrenzen.");
        }

        var computers = new List<LdapComputer>();
        var gate = new object();
        int completed = 0, unnamed = 0;
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = ct }, async (address, token) =>
        {
            LdapComputer? computer = await probe(address, token).ConfigureAwait(false);
            lock (gate)
            {
                if (computer is not null)
                {
                    if (IPAddress.TryParse(computer.Name, out _)) unnamed++;
                    if (pattern.IsMatch(computer.Name)) computers.Add(computer);
                }
                progress?.Report($"Lokales Netzwerk: {++completed} / {addresses.Count} Adressen geprüft · {computers.Count} Treffer");
            }
        }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        string scope = "Lokales Netzwerk: " + string.Join(", ", subnets.Distinct());
        if (unnamed > 0) scope += $" · {unnamed} Geräte ohne ermittelbaren Namen (bei * als IP sichtbar)";
        return new(true, computers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Description).ToArray(), scope, false, "");
    }

    private static IReadOnlyList<IPv4Subnet> GetLocalSubnets() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
        .Select(address => IPv4Subnet.Parse($"{address.Address}/{address.PrefixLength}"))
        .Distinct().ToArray();

    private static async Task<LdapComputer?> ProbeAsync(IPAddress address, CancellationToken ct)
    {
        Task<bool> ping = PingAsync(address, ct);
        Task<string?> netbios = NetBiosNames.ResolveAsync(address, ct);
        Task<string?> dns = ReverseDnsAsync(address, ct);
        await Task.WhenAll(ping, netbios, dns).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!ping.Result && netbios.Result is null) return null;
        string? dnsName = dns.Result;
        string name = netbios.Result ?? dnsName?.Split('.')[0] ?? address.ToString();
        return new(name, dnsName ?? "", "", $"IP-Adresse: {address}" + (name == address.ToString() ? " · Name nicht ermittelbar" : ""));
    }

    private static async Task<bool> PingAsync(IPAddress address, CancellationToken ct)
    {
        using var ping = new Ping();
        try { return (await ping.SendPingAsync(address, 700).WaitAsync(ct).ConfigureAwait(false)).Status == IPStatus.Success; }
        catch (Exception ex) when (ex is PingException or SocketException) { return false; }
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            string name = entry.HostName.TrimEnd('.');
            return name.Length > 0 && !name.Any(char.IsWhiteSpace) && !IPAddress.TryParse(name, out _) ? name : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }
}
