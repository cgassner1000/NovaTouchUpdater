using System.Text.RegularExpressions;

namespace NovaTouchUpdater.Feedback;

public sealed record FeedbackSubmission(Guid SubmissionId, string Kind, string Title, string Description, string Name, string AppVersion)
{
    public FeedbackSubmission Validate()
    {
        string title = (Title ?? "").Trim(), description = (Description ?? "").Trim(), name = (Name ?? "").Trim();
        if (SubmissionId == Guid.Empty || Kind is not ("bug" or "enhancement")) throw new ArgumentException("Bitte Fehler oder Verbesserungsvorschlag auswählen.");
        if (title.Length is < 3 or > 160 || title.Any(char.IsControl)) throw new ArgumentException("Bitte einen Titel mit 3 bis 160 Zeichen eingeben.");
        if (description.Length is < 10 or > 12000) throw new ArgumentException("Bitte eine Beschreibung mit 10 bis 12.000 Zeichen eingeben.");
        if (name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Der freiwillige Name darf höchstens 80 Zeichen enthalten.");
        if (AppVersion is null || !Regex.IsMatch(AppVersion, @"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)) throw new ArgumentException("Die Updater-Version ist ungültig.");
        return this with { Title = title, Description = description, Name = name };
    }
}

public sealed record FeedbackReceipt(int IssueNumber);
