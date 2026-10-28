using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using NovaTouchUpdater;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

var now = DateTimeOffset.Now.AddMinutes(1);
var progressEvent = JsonSerializer.Deserialize<WorkerEvent>("{\"Event\":\"progress\",\"Phase\":\"Kopieren\",\"Message\":\"12/20 Dateien\",\"ProgressPercent\":60}")!;
var progressHost = new HostState();
progressHost.ApplyProgress(progressEvent);
Assert(progressHost.ProgressPercent == 60 && progressHost.Phase.Contains("12/20 Dateien"), "worker percentages and readable status text reach the host state together");
progressHost.Phase = "Update fehlgeschlagen";
Assert(progressHost.ProgressPercent is null, "a new phase or failure clears stale green progress");
progressHost.SetProgress(120);
Assert(progressHost.ProgressPercent == 100, "progress is constrained to the cell bounds");
progressHost.BeginCommand(false);
Assert(progressHost.ProgressPercent is null, "a new command clears completed progress");
progressHost.ApplyProgress(new WorkerEvent { Phase="Gestoppt", ProgressPercent=50 });
progressHost.CompleteCommand(false, now);
Assert(progressHost.ProgressPercent == 100, "confirmed service restart completes its progress bar");
progressHost.FailCommand(false, "Service failed");
Assert(progressHost.ProgressPercent is null, "a failed service command removes the success bar");
Assert(new WorkerRequest().HttpsPort == 5986 &&
    JsonSerializer.Deserialize<WorkerRequest>("{\"UseSsl\":true,\"HttpsPort\":443}") is { UseSsl: true, HttpsPort: 443 } &&
    JsonSerializer.Serialize(new WorkerRequest { UseSsl=true, HttpsPort=443 }).Contains("\"HttpsPort\":443"),
    "worker protocol defaults to HTTPS 5986 and preserves configured port 443");
Assert(UpdatePackageNames.TryServerSuiteVersion(@"C:\updates\nt.server.suite-2026.4.9792.zip", out string suiteTarget) && suiteTarget == "2026.4.9792",
    "server-suite target version is read from the ZIP filename");
Assert(UpdatePackageNames.TryServerSuiteVersion("NT.SERVER.SUITE-2026.4.9792.1-release.zip", out string suiteRevision) && suiteRevision == "2026.4.9792.1" &&
    !UpdatePackageNames.TryServerSuiteVersion("nt.server.suite-2026.4.zip", out _) && !UpdatePackageNames.TryServerSuiteVersion("nt.server.suite-2026.4.9792.exe", out _) &&
    !UpdatePackageNames.TryServerSuiteVersion("other-2026.4.9792.zip", out _), "server-suite file selection requires the expected prefix, ZIP extension and a complete version");
Assert(DisplayText.ServiceVersions([]) == "Nicht installiert", "a successfully read empty service inventory is shown as not installed");
Assert(DisplayText.ServiceVersions([new ServiceInfo { ProductVersion="1.2.3.4" }]) == "1.2.3.4" &&
    DisplayText.ServiceVersions([new ServiceInfo { FileVersion="2.3.4.5" }]) == "2.3.4.5", "installed services keep product versions and file-version fallback");
Assert(DisplayText.ServiceVersions([new ServiceInfo()]) == "–", "an installed service without readable version metadata is not falsely reported as absent");
Assert(DisplayText.ServiceInstances([], "–") == "–", "unknown suite inventory does not claim an absent installation in its tooltip");
var host = new HostState
{
    Host = "reboot.invalid", Result = "Fehler", Error = "Earlier update failed",
    Phase = "Maximal 3 Versuche", Attempt = 3, UpdateFailed = true, Scanned = true,
    Payment = "1.2.3", PosVersion = "2.5"
};
host.Log.Add("Earlier update failed");
int oldRevision = host.ConnectionState.Revision;
host.BeginCommand(true);
Assert(host.Result == "Läuft" && host.Error == "" && host.Attempt == 0,
    "a new command replaces the stale error and update attempt in the visible result");
host.CompleteCommand(true, now);
Assert(host.Result == "Neustart angefordert" && !host.CommandSucceeded && !host.Scanned,
    "an accepted reboot is pending, not a verified software update or completed reboot");
Assert(host.CanRetryUpdate && host.UpdateFailed && host.Log.Contains("Earlier update failed"),
    "a failed update remains retryable and its history survives a successful reboot request");
Assert(!host.ApplyConnectionCheck(oldRevision, true, "late pre-reboot response", now.AddSeconds(1)) && host.Result == "Neustart angefordert",
    "a pre-reboot connection response cannot complete the reboot");
int revision = host.ConnectionState.Revision;
host.ApplyConnectionCheck(revision, true, "countdown", now.AddSeconds(10));
Assert(host.Result == "Neustart angefordert" && host.Connection == "Neustart angefordert",
    "an online terminal during the countdown is not reported as back online");
host.ApplyConnectionCheck(revision, false, "rebooting", now.AddSeconds(35));
Assert(host.Result == "Neustart angefordert" && host.Connection == "Nicht erreichbar" && host.Error == "",
    "expected reboot downtime does not become a command error");
host.ApplyConnectionCheck(revision, null, "monitor unavailable", now.AddSeconds(40));
Assert(host.Result == "Neustart angefordert" && !host.CommandSucceeded,
    "an inconclusive connection check does not complete the reboot");
host.ApplyConnectionCheck(revision, true, "authenticated", now.AddSeconds(50));
Assert(host.Result == "Wieder online" && host.CommandSucceeded && host.Error == "" && host.Phase.Contains("WinRM"),
    "authenticated reachability after the countdown completes the pending command result");
Assert(host.CanRetryUpdate && host.Payment == "1.2.3" && host.PosVersion == "2.5",
    "reconnection neither certifies software versions nor repairs the earlier failed update");
host.ApplyConnectionCheck(revision, false, "later outage", now.AddSeconds(60));
Assert(host.Result == "Wieder online" && host.Connection == "Nicht erreichbar",
    "a later outage changes connectivity without rewriting the last completed action");

var fast = new HostState { Host = "fast.invalid" };
fast.BeginCommand(true);
fast.CompleteCommand(true, now);
fast.ApplyConnectionCheck(fast.ConnectionState.Revision, true, "online", now.AddSeconds(31));
Assert(fast.Result == "Wieder online", "a fast reboot missed between polls does not stay pending forever");

var failed = new HostState { Host = "failed.invalid", Result = "Erfolgreich" };
failed.BeginCommand(true);
failed.FailCommand(true, "Access denied <remote>");
failed.ApplyConnectionCheck(failed.ConnectionState.Revision, true, "online", now);
Assert(failed.Result == "Fehler" && failed.Error == "Access denied <remote>" && !failed.CommandSucceeded,
    "a failed reboot remains an error even if the host is online");
Assert(!failed.CanRetryUpdate, "a failed reboot alone is not offered as a software update retry");

var service = new HostState { Host = "services.invalid", Error = "Old error" };
service.BeginCommand(false);
service.CompleteCommand(false, now);
Assert(service.Result == "Dienste OK" && service.CommandSucceeded && service.Error == "" && !service.CanRetryUpdate,
    "successful service commands replace stale errors without claiming a software update");
service.BeginCommand(false);
service.FailCommand(false, "Service failed");
Assert(service.Result == "Fehler" && !service.CommandSucceeded && !service.CanRetryUpdate,
    "service command failures are visible but are not automatically retried as updates");
service.BeginCommand(false);
service.CancelCommand();
Assert(service.Result == "Abgebrochen" && !service.CommandSucceeded,
    "cancellation does not leave the command permanently running");

var earlierCheck = new HostState { Result = "Fehler", Error = "Earlier check or unhandled update failure" };
earlierCheck.BeginCommand(false);
earlierCheck.CompleteCommand(false, now);
Assert(earlierCheck.CanRetryUpdate && earlierCheck.CommandSucceeded,
    "a command also preserves retry eligibility for scan failures and unexpected update exceptions");
earlierCheck.Result = "Erfolgreich";
Assert(!earlierCheck.CanRetryUpdate && !earlierCheck.CommandSucceeded,
    "a later successful update clears old retry eligibility and the command result marker");

var scan = new HostState { Host = "scan.invalid" };
scan.BeginCommand(true);
scan.CompleteCommand(true, now);
scan.Connection = "Prüfung läuft";
scan.Result = "Fehler";
scan.Error = "New scan failed";
scan.Phase = "Scan fehlgeschlagen";
scan.ApplyConnectionCheck(scan.ConnectionState.Revision, true, "online", now.AddSeconds(50));
Assert(scan.Result == "Fehler" && scan.Error == "New scan failed" && scan.Phase == "Scan fehlgeschlagen" && scan.CanRetryUpdate,
    "a new scan error supersedes the reboot and is never cleared just by connectivity");
var update = new HostState { Host = "update.invalid", Result = "Fehler", Error = "Update failed", UpdateFailed = true };
update.ApplyConnectionCheck(0, true, "online", now);
Assert(update.Result == "Fehler" && update.Error == "Update failed" && update.CanRetryUpdate,
    "background monitoring alone never masks a real update failure");

var pos = new HostState { Host = "pos.invalid", Result = "Fehler", Error = "Earlier check failed", Connection = "Verbunden", Scanned = true };
int posRevision = pos.ConnectionState.Revision;
pos.BeginCommand(false, true);
Assert(pos.Phase.Contains("KassaPreh") && pos.Result == "Läuft" && pos.Error == "", "POS command replaces stale error with its own running phase");
pos.CompleteCommand(false, now, true);
Assert(pos.Result == "POS OK" && pos.CommandSucceeded && pos.Scanned && pos.ConnectionState.Revision == posRevision && pos.CanRetryUpdate,
    "POS success preserves Windows connectivity, inventory eligibility and earlier update retry eligibility");
pos.ApplyConnectionCheck(posRevision, true, "online", now.AddSeconds(10));
Assert(pos.Result == "POS OK", "connection monitoring cannot turn a POS command into a Windows reboot result");
pos.FailCommand(false, "Interactive start failed", true);
pos.ApplyConnectionCheck(posRevision, true, "online", now.AddSeconds(20));
Assert(pos.Result == "Fehler" && pos.Phase == "POS-Neustart fehlgeschlagen" && !pos.CommandSucceeded, "POS failures remain failures even when Windows is online");
pos.CompleteCommand(false, now, true);
var absentPos = new HostState { Host = "absent-pos.invalid", Error = "Stale error" };
absentPos.BeginCommand(false, true);
absentPos.SkipCommand("KassaPreh.exe nicht gefunden - übersprungen");
Assert(absentPos.Result == "Übersprungen" && absentPos.Error == "" && !absentPos.CommandSucceeded && !absentPos.CanRetryUpdate,
    "absent POS is explicitly skipped without an error or a new software-update retry");
var skipDto = JsonSerializer.Deserialize<WorkerResult>("{\"Success\":true,\"Skipped\":true,\"Changed\":false}")!;
Assert(skipDto.Success && skipDto.Skipped && !skipDto.Changed, "worker protocol preserves an explicit skipped command outcome");

// Local state only: no worker, form, network call or actual reboot runs.
service.BeginCommand(false);
service.CompleteCommand(false, now);
var pending = new HostState { Host = "pending.invalid" };
pending.BeginCommand(true);
pending.CompleteCommand(true, now);
string report = WebUtility.HtmlDecode(HtmlReport.Create([host, service, failed, pending, update, pos, absentPos], "1.2", "2.3", now.LocalDateTime, false));
Assert(report.Contains("Befehle erfolgreich</div><div class=\"num\">3</div>") &&
    report.Contains("Aktualisiert</div><div class=\"num\">0</div>") &&
    report.Contains("Fehler</div><div class=\"num\">2</div>"),
    "HTML counts commands separately from updates and genuine errors");
Assert(Regex.IsMatch(report, "data-kind=\"command\"[^>]*>\\s*<td class=\"host\">reboot.invalid</td>[\\s\\S]*?<span class=\"status command\">Wieder online</span>"),
    "HTML exports a completed reboot with its explicit success state");
Assert(Regex.IsMatch(report, "<td class=\"host\">pending.invalid</td>[\\s\\S]*?<span class=\"status other\">Neustart angefordert</span>"),
    "HTML distinguishes pending reboot requests from completed commands");
Assert(Regex.IsMatch(report, "<td class=\"host\">pos.invalid</td>[\\s\\S]*?<span class=\"status command\">POS OK</span>") &&
    Regex.IsMatch(report, "<td class=\"host\">absent-pos.invalid</td>[\\s\\S]*?<span class=\"status other\">Übersprungen</span>"),
    "HTML distinguishes a verified POS restart from an absent, skipped POS");
Assert(report.Contains("Earlier update failed") && report.Contains("Softwareversionen werden dabei nicht erneut geprüft"),
    "HTML retains earlier error history and explains the limit of the online confirmation");
var optionalServices = new HostState { Host="pos-only.invalid", Result="Bereit", Scanned=true,
    Payment=DisplayText.ServiceVersions([]), Fiscal=DisplayText.ServiceVersions([]), PosVersion="2.5.1", DotNet48Installed=true };
string optionalReport = WebUtility.HtmlDecode(HtmlReport.Create([optionalServices], "", "", now.LocalDateTime, false));
Assert(optionalReport.Contains("Nicht installiert") && optionalReport.Contains("2.5.1") &&
    optionalReport.Contains("Fehler</div><div class=\"num\">0</div>") && optionalReport.Contains(">Bereit</span>"),
    "HTML shows absent optional services alongside POS inventory without counting a scan error");
var suiteServices = new[] {
    new ServiceInfo { Name="nt3_server_suite_A", Kind="serversuite", ProductVersion="2026.4.9792", State="Running", InstallDir=@"C:\SuiteA" },
    new ServiceInfo { Name="nt3_server_suite_B", Kind="serversuite", ProductVersion="2026.3.9700", State="Running", InstallDir=@"D:\SuiteB" }
};
Assert(DisplayText.ServiceVersions(suiteServices) == "2026.4.9792, 2026.3.9700" && DisplayText.ServiceInstances(suiteServices).Contains("nt3_server_suite_A") &&
    DisplayText.ServiceInstances(suiteServices).Contains(@"D:\SuiteB"), "multiple suite instances retain their distinct versions and paths");
var suiteHost = new HostState { Host="suite.invalid", Services=suiteServices.ToList(), ServerSuiteBefore="2026.3.9700", ServerSuite=DisplayText.ServiceVersions(suiteServices) };
string suiteReport = WebUtility.HtmlDecode(HtmlReport.Create([suiteHost], "", "", now.LocalDateTime, false, suiteTarget));
var suiteHeaders = Regex.Matches(suiteReport, "<th(?: [^>]*)?>(.*?)</th>").Select(m => m.Groups[1].Value).ToArray();
Assert(Array.IndexOf(suiteHeaders, "Server Suite (vorher → nachher)") == Array.IndexOf(suiteHeaders, "Fiscal (vorher → nachher)") + 1 &&
    suiteReport.Contains("Server Suite Target: <strong>2026.4.9792</strong>") && suiteReport.Contains("nt3_server_suite_A") && suiteReport.Contains("nt3_server_suite_B"),
    "HTML places server suite after fiscal and includes target version plus every installed instance");

var retryHosts = Enumerable.Range(1, 3).Select(attempt => new HostState
{
    Host = $"retry-{attempt}.invalid", Attempt = attempt, Phase = "Kopieren <Paket>", Error = "Fehler & Details"
}).ToArray();
var localMsi = new HostState { Host="LOCALHOST", Connection="Lokal", NtcVersion="2.0.0", RebootRequired=true,
    InstalledPrograms=[new() { Kind="ntv", DisplayName="NovaTouch Voucher", Version="3.0.0" }] };
string localReport = HtmlReport.Create([localMsi], "", "", now.LocalDateTime, true, "",
    new Dictionary<string, string> { ["NovaTouch Control"]="2.0.0", ["<Paket>"]="1.0" }, "Lokal & Remote");
Assert(localMsi.DisplayName == "LOKAL" && localReport.Contains("<td class=\"host\">LOKAL</td>"),
    "the local computer is named LOKAL in the display and HTML report");
Assert(localReport.Contains("NovaTouch NTC</th>") && localReport.Contains("class=\"ntc-ver\">2.0.0</td>") &&
    localReport.Contains("NovaTouch Control Target: <strong>2.0.0</strong>") && localReport.Contains("&lt;Paket&gt;") &&
    localReport.Contains("Lokal &amp; Remote") && localReport.Contains("Windows-Neustart erforderlich") &&
    localReport.Contains("novatouch voucher 3.0.0"), "HTML includes local execution, NTC, encoded MSI targets, searchable additional programs and reboot requirements");
string retryReport = HtmlReport.Create(retryHosts, "", "", now.LocalDateTime, false);
var retryHeaders = Regex.Matches(retryReport, "<th(?: [^>]*)?>(.*?)</th>").Select(match => match.Groups[1].Value).ToArray();
Assert(retryHeaders.Length == 11 && !retryHeaders.Contains("Versuch") && retryHeaders.TakeLast(3).SequenceEqual(new[] { "Information", "Details", "Status" }) &&
    !retryReport.Contains("class=\"attempt\""), "HTML removes the separate attempt column while retaining information, protocol and status");
foreach (int attempt in new[] { 1, 2, 3 })
{
    string row = Regex.Match(retryReport, $"<td class=\"host\">retry-{attempt}.invalid</td>[\\s\\S]*?</tr>").Value;
    string expectedPhase = (attempt == 1 ? "" : $"Versuch {attempt}/3: ") + "Kopieren &lt;Paket&gt;";
    Assert(row.Contains($"<div class=\"info-phase\">{expectedPhase}</div>") && row.Contains("Fehler &amp; Details"),
        $"HTML attempt {attempt} keeps the correct prefix and escapes phase and error text");
}
