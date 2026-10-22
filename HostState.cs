namespace NovaTouchUpdater;

internal sealed class HostState
{
    private string result = "Offen";
    private bool resultFromCommand;
    private bool retryableFailure;

    public string Host { get; init; } = "";
    public ConnectionState ConnectionState { get; } = new();
    public string Connection { get => ConnectionState.Status; set => ConnectionState.SetForegroundStatus(value); }
    public bool? DotNet48Installed { get; set; }
    public string Payment { get; set; } = "–";
    public string Fiscal { get; set; } = "–";
    public string PosVersion { get; set; } = "–";
    public string PaymentBefore { get; set; } = "–";
    public string FiscalBefore { get; set; } = "–";
    public string Phase { get; set; } = "Bereit";
    public string Result
    {
        get => result;
        set { result = value; resultFromCommand = false; retryableFailure = value == "Fehler"; }
    }
    public string Error { get; set; } = "";
    public int Attempt { get; set; }
    public bool Scanned { get; set; }
    public bool UpdateFailed { get; set; }
    // Command results preserve the last scan/update retry eligibility. A reboot
    // cannot repair an update failure or introduce a new reason to run an update.
    public bool CanRetryUpdate => UpdateFailed || retryableFailure;
    public bool CommandSucceeded => resultFromCommand && (Result is "Wieder online" or "Dienste OK" or "POS OK");
    public List<ServiceInfo> Services { get; set; } = [];
    public DataGridViewRow? Row { get; set; }
    public List<string> Log { get; } = [];

    public void BeginCommand(bool restartTerminals, bool restartPos = false)
    {
        Phase = restartTerminals ? "Windows-Neustart wird angefordert" : restartPos ? "KassaPreh wird neu gestartet" : "Dienste werden neu gestartet";
        SetCommandResult("Läuft");
        Error = "";
        Attempt = 0;
    }

    public void CompleteCommand(bool restartTerminals, DateTimeOffset completedAt, bool restartPos = false)
    {
        Error = "";
        if (restartTerminals)
        {
            Phase = "Windows-Neustart angefordert (30 Sekunden)";
            SetCommandResult("Neustart angefordert");
            ConnectionState.ExpectRestart(completedAt);
            Scanned = false;
        }
        else if (restartPos)
        {
            Phase = "KassaPreh in der angemeldeten Benutzersitzung neu gestartet";
            SetCommandResult("POS OK");
        }
        else
        {
            Phase = "Dienste neu gestartet";
            SetCommandResult("Dienste OK");
        }
    }

    public void SkipCommand(string reason)
    {
        Phase = reason;
        Error = "";
        SetCommandResult("Übersprungen");
    }

    public void FailCommand(bool restartTerminals, string error, bool restartPos = false)
    {
        Phase = restartTerminals ? "Windows-Neustart fehlgeschlagen" : restartPos ? "POS-Neustart fehlgeschlagen" : "Dienstbefehl fehlgeschlagen";
        SetCommandResult("Fehler");
        Error = error;
    }

    public void CancelCommand()
    {
        Phase = "Befehl abgebrochen; Dienstzustand erneut prüfen";
        SetCommandResult("Abgebrochen");
    }

    public bool ApplyConnectionCheck(int revision, bool? connected, string detail, DateTimeOffset checkedAt)
    {
        if (!ConnectionState.Apply(revision, connected, detail, checkedAt)) return false;
        // Only finish a pending restart. An online host alone must never clear a
        // failed command or a scan/update error. During the 30-second countdown
        // ConnectionState deliberately retains "Neustart angefordert".
        if (resultFromCommand && Result == "Neustart angefordert" && Connection == "Verbunden")
        {
            Phase = "Nach Neustartauftrag wieder über WinRM erreichbar";
            SetCommandResult("Wieder online");
        }
        return true;
    }

    private void SetCommandResult(string value)
    {
        result = value;
        resultFromCommand = true;
    }
}
