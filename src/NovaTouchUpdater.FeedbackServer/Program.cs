using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using NovaTouchUpdater.Feedback;

namespace NovaTouchUpdater.FeedbackServer;

public static class Program
{
    public static Task Main(string[] args) => CreateApp(args).RunAsync();

    internal static WebApplication CreateApp(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
        builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = 65536);
        builder.Services.AddHttpClient("GitHub", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton(services => new GitHubFeedbackPublisher(services.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub"),
            builder.Configuration["Feedback:GitHubToken"] ?? ""));
        builder.Services.AddSingleton<SubmissionCache>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("all",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
            options.AddPolicy("feedback", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Clamp(builder.Configuration.GetValue("Feedback:PerIpLimit", 5), 1, 20), Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
        });
        configure?.Invoke(builder);
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/api/feedback", async (FeedbackSubmission? submission, SubmissionCache submissions, GitHubFeedbackPublisher publisher, ILoggerFactory logs) =>
        {
            if (submission is null) return Results.BadRequest(new { message = "Bitte eine Meldung eingeben." });
            try
            {
                submission = submission.Validate();
                var receipt = await submissions.PublishAsync(submission, publisher);
                return Results.Ok(receipt);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SubmissionConflictException) { return Results.Conflict(new { message = "Diese Meldungsnummer wurde bereits für andere Eingaben verwendet." }); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or InvalidOperationException)
            {
                logs.CreateLogger("Feedback").LogWarning("GitHub konnte eine Meldung nicht bestätigen ({ErrorType}).", ex.GetType().Name);
                return Results.Json(new { message = "Die Meldung konnte nicht bestätigt werden." }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireRateLimiting("feedback");
        return app;
    }
}

internal sealed class SubmissionConflictException : Exception;

internal sealed class SubmissionCache
{
    private sealed record Entry(FeedbackSubmission Submission, DateTimeOffset Created, Lazy<Task<FeedbackReceipt>> Result);
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();

    internal Task<FeedbackReceipt> PublishAsync(FeedbackSubmission submission, GitHubFeedbackPublisher publisher)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        foreach (var entry in entries) if (entry.Value.Created < cutoff) entries.TryRemove(entry);
        var selected = entries.GetOrAdd(submission.SubmissionId, _ => new Entry(submission, DateTimeOffset.UtcNow,
            new Lazy<Task<FeedbackReceipt>>(() => publisher.PublishAsync(submission), LazyThreadSafetyMode.ExecutionAndPublication)));
        if (selected.Submission != submission) throw new SubmissionConflictException();
        return selected.Result.Value;
    }
}
