namespace NovaTouchUpdater;

internal interface IFeedbackCredentials
{
    string? Read();
}

internal sealed class EmbeddedFeedbackCredentials : IFeedbackCredentials
{
    internal const string ResourceName = "NovaTouchUpdater.Feedback.Token";
    private readonly Lazy<string?> token;

    internal EmbeddedFeedbackCredentials() : this(() => typeof(EmbeddedFeedbackCredentials).Assembly.GetManifestResourceStream(ResourceName)) { }

    internal EmbeddedFeedbackCredentials(Func<Stream?> openResource)
    {
        token = new(() =>
        {
            using Stream? stream = openResource();
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            string value = reader.ReadToEnd().Trim();
            return HasTokenFormat(value) ? value : null;
        });
    }

    public string? Read() => token.Value;

    // Check only the shape of a GitHub-issued PAT. GitHub verifies its validity and permissions.
    internal static bool HasTokenFormat(string? value) => value is { Length: >= 40 and <= 256 } &&
        (value.StartsWith("github_pat_", StringComparison.Ordinal) || value.StartsWith("ghp_", StringComparison.Ordinal)) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
