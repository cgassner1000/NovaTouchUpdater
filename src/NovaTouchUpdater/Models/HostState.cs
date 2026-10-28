namespace NovaTouchUpdater;

internal sealed class HostState
{
    private string result = "Offen";
    private bool resultFromCommand;
    private bool retryableFailure;
    private string phase = "Bereit";

    public string Host { get; init; } = "";
    public bool IsLocal => Host.Equals("LOCALHOST", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => IsLocal ? "LOKAL" : Host;
    public bool IsSelected { get; set; } = true;
    public ConnectionState ConnectionState { get; } = new();
    public string Connection { get => ConnectionState.Status; set => ConnectionState.SetForegroundStatus(value); }
    public bool? DotNet48Installed { get; set; }
    public string Payment { get; set; } = "–";
    public string Fiscal { get; set; } = "–";
    public string ServerSuite { get; set; } = "–";
    public string PosVersion { get; set; } = "–";
    public string NtcVersion { get; set; } = "–";
    public string NtcBefore { get; set; } = "–";
    public string PosBefore { get; set; } = "–";
    public bool RebootRequired { get; set; }
    public List<InstalledProgramInfo> InstalledPrograms { get; set; } = [];
    public string PaymentBefore { get; set; } = "–";
    public string FiscalBefore { get; set; } = "–";
    public string ServerSuiteBefore { get; set; } = "–";
    public string Phase
    {
        get => phase;
        set { phase = value; ProgressPercent = null; }
    }
    public int? ProgressPercent { get; private set; }
    public void SetProgress(int? percent) => ProgressPercent = percent is int value ? Math.Clamp(value, 0, 100) : null;
    public void ApplyProgress(WorkerEvent progress)
    {
        Phase = progress.Phase + " – " + progress.Message;
        SetProgress(progress.ProgressPercent);
    }
    public string Result
    {
        get => result;
        set { result = value; resultFromCommand = false; retryableFailure = value == "Fehler"; }
    }
    public string Error { get; set; } = "";
    public int Attempt { get; set; }
    public bool Scanned { get; set; }
    public bool UpdateFailed { get; set; }
    public HostUpdateState? Updates { get; set; }
    // Command results preserve the last scan/update retry eligibility. A reboot
    // cannot repair an update failure or introduce a new reason to run an update.
    public bool CanRetryUpdate => UpdateFailed || retryableFailure;
    public bool CommandSucceeded => resultFromCommand && (Result is "Wieder online" or "Dienste OK" or "POS OK");
    public List<ServiceInfo> Services { get; set; } = [];
    public DataGridViewRow? Row { get; set; }
    public List<string> Log { get; } = [];

    public void BeginCommand(bool restartTerminals, bool restartPos = false)
    {
        Phase = restartTerminals ? "Windows-Neustart wird angefordert" : restartPos ? "KassaPreh wird gestartet / neu gestartet" : "Dienste werden neu gestartet";
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
            Phase = "KassaPreh in der angemeldeten Benutzersitzung gestartet";
            SetCommandResult("POS OK");
        }
        else
        {
            Phase = "Dienste neu gestartet";
            SetProgress(100);
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
