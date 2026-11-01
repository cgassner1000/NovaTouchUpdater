using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaTouchUpdater.Feedback;
using NovaTouchUpdater.FeedbackServer;

internal static class FeedbackServerTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task Main()
    {
        using var handler = new FakeGitHub();
        using var github = new HttpClient(handler);
        var publisher = new GitHubFeedbackPublisher(github, "synthetic-test-token");
        await using var app = NovaTouchUpdater.FeedbackServer.Program.CreateApp([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Feedback:PerIpLimit"] = "20" });
            builder.Services.AddSingleton(publisher);
        });
        await app.StartAsync();
        using var api = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var anonymous = new FeedbackSubmission(Guid.NewGuid(), "bug", "Update bleibt stehen", "Nach dem Auswählen eines Pakets bleibt der Fortschritt stehen.", "", "1.2.3");
        using var first = await api.PostAsJsonAsync("/api/feedback", anonymous);
        var receipt = await first.Content.ReadFromJsonAsync<FeedbackReceipt>();
        Assert(first.IsSuccessStatusCode && receipt?.IssueNumber == 1001 && handler.Bodies.Count == 1, "anonymous form data creates an issue through the server without a user login");
        Assert(handler.Bodies[0].Contains("Gemeldet von: Anonym") && handler.Bodies[0].Contains("NovaTouch Updater: 1.2.3") &&
            !handler.Bodies[0].Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "anonymous issues contain only submitted content and app version, without the operating-system user");
        using var duplicate = await api.PostAsJsonAsync("/api/feedback", anonymous);
        Assert(duplicate.IsSuccessStatusCode && handler.Bodies.Count == 1, "retrying an unchanged submission returns its receipt without creating another GitHub issue");
        using var conflict = await api.PostAsJsonAsync("/api/feedback", anonymous with { Title = "Andere Meldung" });
        Assert(conflict.StatusCode == HttpStatusCode.Conflict && handler.Bodies.Count == 1, "a submission identifier cannot be reused for different content");
        using var named = await api.PostAsJsonAsync("/api/feedback", anonymous with { SubmissionId = Guid.NewGuid(), Kind = "enhancement", Name = "  Test Person  " });
        Assert(named.IsSuccessStatusCode && handler.Bodies[1].Contains("Gemeldet von: Test Person"), "a voluntary name appears in an improvement issue without a GitHub user account");
        using var invalid = await api.PostAsJsonAsync("/api/feedback", anonymous with { SubmissionId = Guid.NewGuid(), Title = "" });
        Assert(invalid.StatusCode == HttpStatusCode.BadRequest && handler.Bodies.Count == 2, "invalid feedback is rejected before calling GitHub");
        handler.Status = HttpStatusCode.Forbidden;
        using var rejected = await api.PostAsJsonAsync("/api/feedback", anonymous with { SubmissionId = Guid.NewGuid() });
        Assert(rejected.StatusCode == HttpStatusCode.BadGateway && !(await rejected.Content.ReadAsStringAsync()).Contains("synthetic-test-token"),
            "GitHub failures return a generic delivery error without exposing provider content or credentials");
        bool limited = false;
        for (int i = 0; i < 21; i++)
        {
            using var response = await api.PostAsJsonAsync("/api/feedback", anonymous with { Title = "" });
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { limited = true; break; }
        }
        Assert(limited && handler.Bodies.Count == 3, "the anonymous endpoint applies a request limit without creating spam issues");
        using var noToken = new HttpClient(handler);
        bool refused = false;
        try { await new GitHubFeedbackPublisher(noToken, "").PublishAsync(anonymous); } catch (InvalidOperationException) { refused = true; }
        Assert(refused && handler.Bodies.Count == 3, "missing server credentials prevent GitHub calls");
        await app.StopAsync();
        Console.WriteLine("PASS feedback server tests use loopback and a fake GitHub handler; no real issue was created");
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal HttpStatusCode Status = HttpStatusCode.Created;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert(request.Method == HttpMethod.Post && request.RequestUri?.ToString() == "https://api.github.com/repos/cgassner1000/NovaTouchUpdater/issues" &&
                request.Headers.Authorization?.Parameter == "synthetic-test-token", "only the server adds credentials and posts to the fixed repository");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Bodies.Add(json.RootElement.GetProperty("body").GetString()!);
            return new HttpResponseMessage(Status) { Content = Status == HttpStatusCode.Created ? JsonContent.Create(new { number = 1000 + Bodies.Count }) : JsonContent.Create(new { message = "synthetic-test-token must remain private" }) };
        }
    }
}
