namespace NovaTouchUpdater;

internal sealed class ConnectionState
{
    public string Status { get; private set; } = "Nicht geprüft";
    public int Revision { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public string Detail { get; private set; } = "";
    public bool? UsesHttps { get; private set; }
    private DateTimeOffset? restartNotBefore;

    public void SetForegroundStatus(string status, bool? usesHttps = null)
    {
        Revision++;
        Status = status;
        UsesHttps = status == "Verbunden" ? usesHttps : null;
        Detail = "";
        LastCheckedAt = status is "Verbunden" or "Nicht erreichbar" ? DateTimeOffset.Now : null;
    }

    public void ExpectRestart(DateTimeOffset requestedAt)
    {
        SetForegroundStatus("Neustart angefordert");
        restartNotBefore = requestedAt.AddSeconds(30);
    }

    public bool Apply(int expectedRevision, bool? connected, string detail, DateTimeOffset checkedAt, bool? usesHttps = null)
    {
        // A result started before a manual scan/restart must not overwrite that
        // newer action. Also reject results delivered out of time order.
        if (Revision != expectedRevision || (LastCheckedAt.HasValue && checkedAt < LastCheckedAt.Value)) return false;
        LastCheckedAt = checkedAt;
        Detail = detail;
        Status = connected switch
        {
            true when restartNotBefore.HasValue && checkedAt < restartNotBefore.Value => "Neustart angefordert",
            true => "Verbunden",
            false => "Nicht erreichbar",
            null => "Unbekannt"
        };
        if (Status == "Verbunden") restartNotBefore = null;
        UsesHttps = Status == "Verbunden" ? usesHttps : null;
        return true;
    }

    public string ToolTip =>
        (LastCheckedAt.HasValue ? $"Letzte WinRM-Prüfung: {LastCheckedAt.Value:dd.MM.yyyy HH:mm:ss}" : "Noch keine aktuelle Verbindungsprüfung.") +
        (Status == "Neustart angefordert" ? "\nWindows-Neustart angefordert; der Host kann während der 30 Sekunden Vorlauf noch erreichbar sein." : "") +
        (UsesHttps is true ? "\nBestätigte WinRM-Verbindung über HTTPS (TLS-verschlüsselt)." : UsesHttps is false ? "\nBestätigte WinRM-Verbindung über HTTP." : "") +
        (string.IsNullOrWhiteSpace(Detail) ? "" : "\n" + Detail);
}
