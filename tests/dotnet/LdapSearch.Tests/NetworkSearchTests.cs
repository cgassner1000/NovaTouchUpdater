using System.Buffers.Binary;
using System.Net;
using System.Text;
using NovaTouchUpdater;

internal static class NetworkSearchTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task Rejects<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Console.WriteLine("PASS " + message); return; }
        throw new InvalidOperationException(message);
    }

    internal static async Task Run()
    {
        foreach (string pattern in new[] { "PC*-RST-POS-*", "SERVER-*", "*-KASSE-*", "", "  " })
        {
            string name = pattern switch { "SERVER-*" => "server-42", "*-KASSE-*" => "XY-KASSE-ABC", _ => "pc01-rst-pos-99" };
            Assert(NetworkComputerSearch.NamePattern(pattern).IsMatch(name), "local name patterns preserve wildcard and case-insensitive matching: " + pattern);
        }
        Assert(!NetworkComputerSearch.NamePattern("PC*-RST-POS-*").IsMatch("SERVER-POS-01") &&
            !NetworkComputerSearch.NamePattern("PC?").IsMatch("PC1") && NetworkComputerSearch.NamePattern("PC[1]?").IsMatch("PC[1]?"),
            "local name patterns are anchored and only * acts as a wildcard");
        Assert(IPv4Subnet.Parse("192.0.2.42/24").ToString() == "192.0.2.0/24" &&
            IPv4Subnet.Parse("192.0.2.42/24").AddressCount == 254, "network addresses are normalized with the actual prefix");
        Assert(IPv4Subnet.Parse("192.0.2.0/30").Addresses().Select(ip => ip.ToString()).SequenceEqual(new[] { "192.0.2.1", "192.0.2.2" }) &&
            IPv4Subnet.Parse("192.0.2.0/31").Addresses().Count() == 2 && IPv4Subnet.Parse("192.0.2.42/32").Addresses().Single().ToString() == "192.0.2.42",
            "ordinary subnets omit network/broadcast addresses while /31 and /32 remain usable");
        foreach (string invalid in new[] { "", "192.0.2.1", "192.0.2.1/33", "::1/64", "invalid/24" })
            await Rejects<ArgumentException>(() => Task.FromResult(IPv4Subnet.Parse(invalid)), "invalid IPv4 range is rejected: " + invalid);

        await ScanTests();
        await RoutingTests();
        NetBiosTests();
    }

    private static async Task ScanTests()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        var progress = new Messages();
        var scanner = new NetworkComputerSearch(() => [IPv4Subnet.Parse("192.0.2.0/29"), IPv4Subnet.Parse("192.0.2.0/30")], (ip, ct) =>
        {
            seen.Add(ip.ToString());
            int last = ip.GetAddressBytes()[3];
            string? name = last switch { 1 => "PC01-RST-POS-01", 2 => "pc02-rst-pos-99", 3 => "SERVER-1", 4 => ip.ToString(), _ => null };
            return Task.FromResult(name is null ? null : new LdapComputer(name, "", "", "IP-Adresse: " + ip));
        });
        LdapSearchResult result = await scanner.SearchAsync(new("PC*-RST-POS-*", ""), progress, CancellationToken.None);
        Assert(result.Computers.Length == 2 && result.Computers.All(c => c.Name.Contains("RST", StringComparison.OrdinalIgnoreCase)),
            "local scanning returns only matching named computers");
        Assert(seen.Count == 6 && seen.Distinct().Count() == 6 && !seen.Contains("192.0.2.0") && !seen.Contains("192.0.2.7"),
            "overlapping adapter ranges are scanned once without network/broadcast addresses");
        Assert(progress.Values.Last().Contains("6 / 6") && result.SearchBase.Contains("1 Geräte ohne"),
            "progress reaches the complete range and unresolved names are explicitly reported");
        result = await scanner.SearchAsync(new("", "192.0.2.0/29"), null, CancellationToken.None);
        Assert(result.Computers.Length == 4 && result.Computers.Any(c => c.Host == "192.0.2.4"), "an empty pattern includes all detected devices, including unnamed IP addresses");

        int calls = 0;
        var bounded = new NetworkComputerSearch(() => [], (_, _) => { calls++; return Task.FromResult<LdapComputer?>(null); });
        await Rejects<ArgumentException>(() => bounded.SearchAsync(new("*", "10.0.0.0/8"), null, CancellationToken.None), "large networks require narrowing instead of silent truncation");
        await Rejects<ArgumentException>(() => bounded.SearchAsync(new("*", ""), null, CancellationToken.None), "missing active networks require an explicit range");
        Assert(calls == 0, "invalid or oversized ranges cause no probe requests");

        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        var waiting = new NetworkComputerSearch(() => [IPv4Subnet.Parse("192.0.2.0/24")], async (_, ct) =>
        {
            Interlocked.Increment(ref probes);
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        Task<LdapSearchResult> scan = waiting.SearchAsync(new("*", ""), null, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Rejects<OperationCanceledException>(async () => await scan, "cancelling stops local discovery without returning partial selectable results");
        Assert(probes is > 0 and <= 32, "local network probes have bounded concurrency");
    }

    private static async Task RoutingTests()
    {
        bool joined = false, missing = false, denied = false;
        int ldap = 0, network = 0, detection = 0;
        var discovery = new ComputerDiscovery(
            directorySearch: (_, _) =>
            {
                ldap++;
                if (missing) throw new LdapDomainUnavailableException("No domain found");
                if (denied) throw new UnauthorizedAccessException("LDAP denied");
                return Task.FromResult(new LdapSearchResult(true, [], "DC=example,DC=test", false, ""));
            },
            networkSearch: (request, _, _) =>
            {
                network++;
                Assert(request.Pattern == "PC*-RST-POS-*" && request.Range == "192.0.2.0/24", "automatic fallback retains the entered name pattern and IP range");
                return Task.FromResult(new LdapSearchResult(true, [], "Lokales Netzwerk", false, ""));
            }, domainAvailable: _ => { detection++; return Task.FromResult(joined); });
        var options = new ComputerSearchOptions(ComputerSearchSource.Automatic, "", "", "PC*-RST-POS-*", false, "192.0.2.0/24");
        await discovery.SearchAsync(options, null, CancellationToken.None);
        Assert(ldap == 0 && network == 1 && detection == 1, "without domain membership automatic discovery searches the local network");
        joined = true;
        await discovery.SearchAsync(options, null, CancellationToken.None);
        Assert(ldap == 1 && network == 1, "an existing domain is searched first; zero LDAP results do not trigger a network scan");
        missing = true;
        await discovery.SearchAsync(options, null, CancellationToken.None);
        Assert(ldap == 2 && network == 2, "domain-discovery unavailability triggers automatic network fallback");
        await Rejects<LdapDomainUnavailableException>(() => discovery.SearchAsync(options with { Domain = "dc.example.test" }, null, CancellationToken.None), "explicit domains never silently change to a local scan");
        await Rejects<LdapDomainUnavailableException>(() => discovery.SearchAsync(options with { Source = ComputerSearchSource.Domain }, null, CancellationToken.None), "domain-only mode retains domain-discovery errors");
        missing = false; denied = true;
        await Rejects<UnauthorizedAccessException>(() => discovery.SearchAsync(options, null, CancellationToken.None), "LDAP permission errors are not hidden by automatic network fallback");
        int previousDetection = detection;
        await discovery.SearchAsync(options with { Source = ComputerSearchSource.LocalNetwork }, null, CancellationToken.None);
        Assert(detection == previousDetection && network == 3, "explicit local mode bypasses domain detection and LDAP");
        await Rejects<ArgumentException>(() => discovery.SearchAsync(options with { Source = ComputerSearchSource.LocalNetwork, CustomFilter = true }, null, CancellationToken.None), "LDAP filters cannot silently turn into local name patterns");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Rejects<OperationCanceledException>(() => discovery.SearchAsync(options, null, cancel.Token), "already-cancelled discovery starts no network or directory query");
    }

    private static void NetBiosTests()
    {
        const ushort transaction = 0x1234;
        byte[] query = NetBiosNames.CreateQuery(transaction);
        Assert(query.Length == 50 && BinaryPrimitives.ReadUInt16BigEndian(query) == transaction && query[5] == 1 && query[12] == 32 &&
            query[13] == 'C' && query[14] == 'K' && query[45] == 0 && query[47] == 0x21 && query[49] == 1,
            "NetBIOS discovery sends a read-only wildcard node-status question");
        // A group entry precedes the actual workstation name. Packet includes a normal, uncompressed RR name.
        byte[] reply = new byte[query.Length + 6 + 1 + 18 * 2];
        query.CopyTo(reply, 0);
        reply[2] = 0x84; reply[5] = 0; reply[7] = 1;
        reply[46] = 0; reply[47] = 0x21; reply[48] = 0; reply[49] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(54), 37);
        reply[56] = 2;
        Encoding.ASCII.GetBytes("WORKGROUP".PadRight(15)).CopyTo(reply, 57);
        reply[73] = 0x80;
        Encoding.ASCII.GetBytes("PC01-RST-POS-01".PadRight(15)).CopyTo(reply, 75);
        Assert(NetBiosNames.ReadName(reply, transaction) == "PC01-RST-POS-01", "NetBIOS replies yield the unique computer name, excluding workgroup names");
        Assert(NetBiosNames.ReadName(reply, 1) is null && NetBiosNames.ReadName(reply.AsSpan(0, 60), transaction) is null,
            "foreign transaction IDs and truncated NetBIOS replies are ignored");
        for (int length = 0; length < reply.Length; length++)
            AssertSilent(NetBiosNames.ReadName(reply.AsSpan(0, length), transaction) is null);
        Console.WriteLine("PASS every truncated NetBIOS reply is handled without an exception");
    }

    private static void AssertSilent(bool condition) { if (!condition) throw new InvalidOperationException("Truncated reply unexpectedly accepted"); }
    private sealed class Messages : IProgress<string>
    {
        internal List<string> Values { get; } = [];
        public void Report(string value) { lock (Values) Values.Add(value); }
    }
}
