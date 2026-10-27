using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using NovaTouchUpdater;

internal static partial class Program
{
    private static async Task StreamingProtocolTests()
    {
        using var reader = new SearchLines();
        var arrived = new TaskCompletionSource<LdapComputer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LdapSearchResult?> reading = LdapComputerSearch.ReadResultsAsync(reader, new FoundComputer(c => arrived.TrySetResult(c)), CancellationToken.None);
        var computer = new LdapComputer("PC01", "pc01.example.test", "Windows", "Kasse");
        reader.Lines.Writer.TryWrite(JsonSerializer.Serialize(new { Event="computer", Computer=computer }));
        LdapComputer first = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(first.Name == "PC01" && !reading.IsCompleted, "LDAP JSON frames reach the consumer before the final worker result");
        reader.Lines.Writer.TryWrite(JsonSerializer.Serialize(new LdapSearchResult(true, [computer], "DC=example,DC=test", false, "")));
        reader.Lines.Writer.Complete();
        LdapSearchResult? final = await reading;
        Assert(final?.Computers.Single().Host == "pc01.example.test", "streamed LDAP output retains the final result and internal DNS host identity");

        var nextProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var networkHit = new TaskCompletionSource<LdapComputer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new NetworkComputerSearch(() => [IPv4Subnet.Parse("192.0.2.0/30")], async (ip, ct) =>
        {
            if (ip.ToString() == "192.0.2.1") return new("PC01", "pc01.example.test", "", "Network fixture");
            await nextProbe.Task.WaitAsync(ct); return null;
        });
        Task<LdapSearchResult> scan = scanner.SearchAsync(new("PC*", ""), null, CancellationToken.None, new FoundComputer(c => networkHit.TrySetResult(c)));
        first = await networkHit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(first.IpAddress == "192.0.2.1" && !scan.IsCompleted, "network hits include the probed IP immediately while other addresses are still being scanned");
        nextProbe.SetResult(true);
        Assert((await scan).Computers.Single().IpAddress == "192.0.2.1", "the final network result preserves the known IP without a forward DNS lookup");

        var missingDns = new ComputerDiscovery(
            directorySearch: (_, _, found) => { found?.Report(computer); return Task.FromResult(new LdapSearchResult(true, [computer], "fixture", false, "")); },
            resolveAddress: (_, _) => Task.FromException<string>(new SocketException((int)SocketError.HostNotFound)));
        final = await missingDns.SearchAsync(new(ComputerSearchSource.Domain, "", "", "*", false, ""), null, CancellationToken.None);
        Assert(final.Success && final.Computers.Single().IpAddress == "" && final.Computers.Single().Host == computer.Host,
            "missing DNS leaves a found domain computer available for import without turning the search into an error");
    }

    private static void StreamingDialogTests(WindowsFormsSynchronizationContext uiContext)
    {
        var finished = new TaskCompletionSource<LdapSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var address = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<LdapComputer>? found = null;
        var discovery = new ComputerDiscovery(
            directorySearch: (_, ct, sink) => { found=sink; return finished.Task.WaitAsync(ct); },
            resolveAddress: (_, ct) => address.Task.WaitAsync(ct));
        using var dialog = new LdapComputerSearchDialog(discovery);
        _ = dialog.Handle;
        var controls = All(dialog).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
        ((ComboBox)controls["searchSource"]).SelectedIndex = (int)ComputerSearchSource.Domain;
        var grid = (DataGridView)controls["results"];
        SynchronizationContext.SetSynchronizationContext(uiContext);
        Task running = dialog.SearchAsync();
        var computer = new LdapComputer("PC01", "pc01.example.test", "Windows 11", "Kasse");
        found!.Report(computer);
        Application.DoEvents();
        Assert(!running.IsCompleted && grid.Rows.Count == 1 && grid.Enabled && grid.Columns["include"].ReadOnly,
            "found PCs appear in the enabled results table during the running search, with selection locked until completion");
        Assert(grid.Columns["ip"].HeaderText == "IP-Adresse" && !grid.Columns.Contains("dns") &&
            Equals(grid.Rows[0].Cells["os"].Value, "Windows 11") && Equals(grid.Rows[0].Cells["description"].Value, "Kasse"),
            "the IP column replaces DNS name while computer, operating system and description remain visible");
        address.SetResult("192.0.2.12, 192.0.2.13");
        PumpUntil(() => Equals(grid.Rows[0].Cells["ip"].Value, "192.0.2.12, 192.0.2.13"));
        found.Report(computer);
        Application.DoEvents();
        Assert(grid.Rows.Count == 1 && !running.IsCompleted, "DNS address enrichment updates the existing row without waiting for search completion or duplicating it");
        finished.SetResult(new(true, [computer], "fixture", false, ""));
        Complete(running); Application.DoEvents();
        dialog.SetAllSelected(true);
        Assert(grid.Rows.Count == 1 && dialog.GetSelectedHosts().SequenceEqual(new[] { "pc01.example.test" }) && controls["import"].Enabled,
            "final reconciliation preserves IPs and imports the original host identity after explicit selection");
        string summary = controls["searchStatus"].Text;
        found.Report(new("LATE", "late.example.test", "", ""));
        Application.DoEvents();
        Assert(grid.Rows.Count == 1 && controls["searchStatus"].Text == summary, "late callbacks cannot add old hits or replace the completed summary");

        var waitingForAddress = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<LdapComputer>? oldSearch = null;
        using var cancelled = new LdapComputerSearchDialog(new ComputerDiscovery(
            directorySearch: async (_, ct, sink) =>
            {
                oldSearch=sink; sink?.Report(computer);
                await Task.Delay(Timeout.Infinite, ct);
                return new(true, [], "", false, "");
            }, resolveAddress: async (_, ct) => { waitingForAddress.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); return ""; }));
        _ = cancelled.Handle;
        ((ComboBox)All(cancelled).Single(c => c.Name == "searchSource")).SelectedIndex = (int)ComputerSearchSource.Domain;
        running = cancelled.SearchAsync();
        PumpUntil(() => waitingForAddress.Task.IsCompleted);
        cancelled.CancelSearch(); Complete(running);
        oldSearch!.Report(new("OLD", "old.example.test", "", ""));
        Application.DoEvents();
        Assert(!All(cancelled).Single(c => c.Name == "import").Enabled && All(cancelled).Single(c => c.Name == "searchStatus").Text.Contains("abgebrochen"),
            "cancellation stops pending DNS work and leaves partial results unavailable for import");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(5); }
        Assert(condition(), "the live result update reaches the UI");
    }

    private sealed class FoundComputer(Action<LdapComputer> report) : IProgress<LdapComputer>
    {
        public void Report(LdapComputer value) => report(value);
    }

    private sealed class SearchLines : TextReader
    {
        internal Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            try { return await Lines.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }
    }
}
