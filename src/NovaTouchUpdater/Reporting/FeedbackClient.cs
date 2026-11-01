using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NovaTouchUpdater.Feedback;

namespace NovaTouchUpdater;

internal interface IFeedbackClient
{
    bool IsConfigured { get; }
    Task<FeedbackReceipt> SendAsync(FeedbackSubmission submission, CancellationToken ct);
}

internal sealed class FeedbackClient(HttpClient http, IFeedbackCredentials credentials) : IFeedbackClient
{
    private sealed class Attempt
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal bool Submitted;
        internal FeedbackReceipt? Receipt;
    }
    private readonly ConcurrentDictionary<Guid, Attempt> attempts = new();
    internal static readonly Uri IssuesUrl = new($"https://api.github.com/repos/{AppReleaseClient.Repository}/issues");
    public bool IsConfigured => EmbeddedFeedbackCredentials.HasTokenFormat(credentials.Read());

    public async Task<FeedbackReceipt> SendAsync(FeedbackSubmission submission, CancellationToken ct)
    {
        submission = submission.Validate();
        string token = credentials.Read() ?? "";
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Der Meldungsversand ist in dieser Programmversion noch nicht eingerichtet.");
        if (!EmbeddedFeedbackCredentials.HasTokenFormat(token)) throw new InvalidOperationException("Der Meldungsversand ist ungültig konfiguriert. Bitte eine aktuelle Programmversion verwenden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        Attempt attempt = attempts.GetOrAdd(submission.SubmissionId, _ => new());
        await attempt.Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (attempt.Receipt is not null) return attempt.Receipt;
            if (attempt.Submitted && await FindPreviousAsync(submission, token, timeout.Token).ConfigureAwait(false) is { } previous)
                return attempt.Receipt = previous;
            string kind = submission.Kind == "bug" ? "Fehler" : "Verbesserungsvorschlag";
            string name = submission.Name.Length == 0 ? "Anonym" : submission.Name;
            string body = $"## {kind}\n\n{submission.Description}\n\n---\nGemeldet von: {name}\n\nNovaTouch Updater: {submission.AppVersion}\n\n{Marker(submission)}";
            using var request = Request(HttpMethod.Post, IssuesUrl, token, submission.AppVersion);
            request.Content = JsonContent.Create(new { title = $"[{kind}] {submission.Title}", body });
            attempt.Submitted = true;
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 400 and < 500) attempt.Submitted = false;
            CheckResponse(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("number", out var number) || !number.TryGetInt32(out int issue) || issue <= 0)
                throw new InvalidDataException("GitHub hat die Meldung nicht mit einer Issue-Nummer bestätigt.");
            return attempt.Receipt = new(issue);
        }
        finally { attempt.Gate.Release(); }
    }

    private async Task<FeedbackReceipt?> FindPreviousAsync(FeedbackSubmission submission, string token, CancellationToken ct)
    {
        // Query the current issue list on retry, without waiting for search indexing.
        using var request = Request(HttpMethod.Get, new Uri(IssuesUrl + "?state=all&sort=created&direction=desc&per_page=100"), token, submission.AppVersion);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        CheckResponse(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (JsonElement issue in json.RootElement.EnumerateArray())
            if (!issue.TryGetProperty("pull_request", out _) && issue.TryGetProperty("body", out var body) &&
                (body.GetString() ?? "").Contains(Marker(submission), StringComparison.Ordinal) &&
                issue.TryGetProperty("number", out var number) && number.TryGetInt32(out int value) && value > 0) return new(value);
        return null;
    }

    private static string Marker(FeedbackSubmission submission) => $"<!-- NovaTouchFeedback:{submission.SubmissionId:D} -->";
    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string token, string version)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("NovaTouchUpdater/" + version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Der Meldungsversand ist derzeit nicht verfügbar (Zugriff ungültig oder abgelaufen). Bitte eine aktuelle Programmversion verwenden.");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("Der Meldungsversand ist derzeit nicht verfügbar (Zugriff verweigert oder Anfragelimit erreicht). Bitte später erneut versuchen.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("Zu viele Meldungen. Bitte später erneut versuchen.");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw new InvalidOperationException("Das GitHub-Repository ist nicht erreichbar oder Issues sind deaktiviert.");
        throw new InvalidOperationException("GitHub konnte die Meldung nicht bestätigen. Bitte später erneut versuchen.");
    }
}
