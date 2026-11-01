using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using NovaTouchUpdater.Feedback;

namespace NovaTouchUpdater;

internal interface IFeedbackClient
{
    bool IsConfigured { get; }
    Task<FeedbackReceipt> SendAsync(FeedbackSubmission submission, CancellationToken ct);
}

internal sealed class FeedbackClient(HttpClient http, Uri? endpoint) : IFeedbackClient
{
    internal static Uri? ConfiguredEndpoint
    {
        get
        {
            string? value = typeof(FeedbackClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "FeedbackEndpoint")?.Value;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri : null;
        }
    }

    public bool IsConfigured => endpoint is { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0 };

    public async Task<FeedbackReceipt> SendAsync(FeedbackSubmission submission, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Das Senden von Meldungen ist noch nicht eingerichtet.");
        submission = submission.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(submission) };
        request.Headers.UserAgent.ParseAdd("NovaTouchUpdater/" + submission.AppVersion);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("Zu viele Meldungen. Bitte später erneut versuchen.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Die Meldung konnte nicht bestätigt werden. Bitte später erneut versuchen.");
        var receipt = await response.Content.ReadFromJsonAsync<FeedbackReceipt>(timeout.Token).ConfigureAwait(false);
        return receipt is { IssueNumber: > 0 } ? receipt : throw new InvalidDataException("Die Meldung wurde nicht bestätigt.");
    }
}
