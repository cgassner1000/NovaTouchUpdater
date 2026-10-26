using System.Collections.Concurrent;
using NovaTouchUpdater;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

var now = DateTimeOffset.Now.AddMinutes(1);
var state = new ConnectionState();
Assert(state.Status == "Nicht geprüft" && state.LastCheckedAt is null, "initial state has no fabricated connection check");
Assert(state.Apply(0, true, "authenticated", now) && state.Status == "Verbunden", "online probe is shown as connected");
Assert(state.LastCheckedAt == now && state.ToolTip.Contains("authenticated"), "last check time and details are retained");
Assert(state.Apply(0, false, "offline", now.AddSeconds(10)) && state.Status == "Nicht erreichbar", "connection loss is reflected");
Assert(!state.Apply(0, true, "late", now) && state.Status == "Nicht erreichbar", "out-of-order results cannot overwrite newer checks");

int oldRevision = state.Revision;
state.SetForegroundStatus("Prüfung läuft");
Assert(!state.Apply(oldRevision, false, "stale", now.AddSeconds(20)) && state.Status == "Prüfung läuft", "manual check invalidates older background results");
Assert(state.Apply(state.Revision, true, "fresh", now.AddSeconds(21)) && state.Status == "Verbunden", "a fresh monitoring round can resume");

oldRevision = state.Revision;
state.ExpectRestart(now.AddSeconds(30));
int restartRevision = state.Revision;
Assert(!state.Apply(oldRevision, true, "before restart", now.AddSeconds(31)), "restart request invalidates an in-flight online result");
Assert(state.Apply(restartRevision, true, "still shutting down", now.AddSeconds(40)) && state.Status == "Neustart angefordert", "reachable host during countdown is not shown as already restarted");
Assert(state.Apply(restartRevision, false, "rebooting", now.AddSeconds(65)) && state.Status == "Nicht erreichbar", "reboot downtime is shown as offline");
Assert(state.Apply(restartRevision, true, "ready", now.AddSeconds(75)) && state.Status == "Verbunden", "host coming back is shown as connected");

state.ExpectRestart(now.AddSeconds(100));
Assert(state.Apply(state.Revision, true, "ready", now.AddSeconds(131)) && state.Status == "Verbunden", "fast reboot missed between polls does not leave a permanent pending status");
Assert(state.Apply(state.Revision, null, "worker unavailable", now.AddSeconds(140)) && state.Status == "Unbekannt", "monitor failure remains distinct from a proven unreachable host");
Assert(state.Apply(state.Revision, false, "authentication rejected", now.AddSeconds(150)) && state.Status == "Nicht erreichbar", "failed authentication does not count as a usable connection");

if (OperatingSystem.IsWindows())
{
    using var runner = new WorkerRunner("ConnectionState.Tests.MockWorker.ps1");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var events = new ConcurrentQueue<WorkerEvent>();
    var result = await runner.RunAsync(new WorkerRequest
    {
        Action = "monitor-connections", Hosts = ["online.invalid", "offline.invalid", "unknown.invalid"], Parallelism = 2
    }, events.Enqueue, timeout.Token);
    Assert(result.Success && events.Count == 3, "worker protocol streams host results before the round finishes");
    Assert(events.Any(e => e.Host == "online.invalid" && e.Connected == true) &&
        events.Any(e => e.Host == "offline.invalid" && e.Connected == false) &&
        events.Any(e => e.Host == "unknown.invalid" && e.Connected is null), "worker JSON preserves host identity and true/false/unknown connectivity");
}
