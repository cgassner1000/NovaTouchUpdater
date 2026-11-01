using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NovaTouchUpdater.Feedback;

namespace NovaTouchUpdater.FeedbackServer;

internal sealed class GitHubFeedbackPublisher(HttpClient http, string token)
{
    internal const string Repository = "cgassner1000/NovaTouchUpdater";

    internal async Task<FeedbackReceipt> PublishAsync(FeedbackSubmission submission)
    {
        submission = submission.Validate();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Der GitHub-Zugang ist serverseitig noch nicht eingerichtet.");
        string kind = submission.Kind == "bug" ? "Fehler" : "Verbesserungsvorschlag";
        string name = submission.Name.Length == 0 ? "Anonym" : submission.Name;
        string body = $"## {kind}\n\n{submission.Description}\n\n---\nGemeldet von: {name}\n\nNovaTouch Updater: {submission.AppVersion}\n\n<!-- NovaTouchFeedback:{submission.SubmissionId:D} -->";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{Repository}/issues")
        {
            Content = JsonContent.Create(new { title = $"[{kind}] {submission.Title}", body })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("NovaTouchUpdater-Feedback/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!json.RootElement.TryGetProperty("number", out var number) || !number.TryGetInt32(out int issue) || issue <= 0) throw new InvalidDataException("GitHub hat keine gültige Issue-Nummer zurückgegeben.");
        return new(issue);
    }
}
