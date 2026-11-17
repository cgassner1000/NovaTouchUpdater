# Bootstrap a real Windows PowerShell console. Its standard streams remain
# interactive; credentials arrive only through a current-user-only named pipe.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# A lost/closed remote session must never expose an unmarked local prompt.
function global:prompt {
    $sessionToClose = Get-Variable -Name NovaTouchUpdaterConsoleSession -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    if ($null -ne $sessionToClose) { Remove-PSSession -Session $sessionToClose -ErrorAction SilentlyContinue }
    Write-Host 'Remote-Sitzung beendet. Konsole wird geschlossen.'
    exit
}

function ReadHostConsoleRequest([string]$PipeName) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $pipe.Connect(15000)
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
        try {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { throw 'Keine Verbindungsdaten erhalten.' }
            $request = $line | ConvertFrom-Json
            $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
            try { $writer.WriteLine('READY'); $writer.Flush() } finally { $writer.Dispose() }
            $request
        } finally { $reader.Dispose(); $line = $null }
    } finally { $pipe.Dispose() }
}

function ConnectHostConsole($Request) {
    if ([string]::IsNullOrWhiteSpace([string]$Request.Host) -or [bool]$Request.LocalExecution) { throw 'Ungültiges Remote-Ziel.' }
    $parameters = @{
        ComputerName = [string]$Request.Host
        Port = 5985
        ConfigurationName = 'Microsoft.PowerShell'
        SessionOption = New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 180000
        ErrorAction = 'Stop'
    }
    if ([bool]$Request.UseSsl) {
        $port = [int]$Request.HttpsPort
        if ($port -lt 1 -or $port -gt 65535) { throw 'Ungültiger HTTPS-Port.' }
        $parameters.UseSSL = $true
        $parameters.Port = $port
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Request.Username)) {
        if ([string]::IsNullOrEmpty([string]$Request.Password)) { throw 'Passwort für den angegebenen Benutzer fehlt.' }
        $secure = ConvertTo-SecureString -String ([string]$Request.Password) -AsPlainText -Force
        $parameters.Credential = [Management.Automation.PSCredential]::new([string]$Request.Username, $secure)
    }
    $session = New-PSSession @parameters
    try {
        $identity = Invoke-Command -Session $session -ErrorAction Stop -ScriptBlock {
            $user = [Security.Principal.WindowsIdentity]::GetCurrent()
            [pscustomobject]@{ Name=$user.Name; ComputerName=$env:COMPUTERNAME;
                IsAdministrator=([Security.Principal.WindowsPrincipal]::new($user)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
        }
        if (-not $identity.IsAdministrator) { throw "Das Konto $($identity.Name) besitzt auf dem Ziel keine Administratorrechte." }
        Write-Host ("Verbunden mit $($Request.Host) · $($identity.ComputerName) · $($identity.Name) · Administrator") -ForegroundColor Green
        $session
    } catch { Remove-PSSession -Session $session -ErrorAction SilentlyContinue; throw }
}

$session = $null
try {
    $request = ReadHostConsoleRequest '__PIPE_NAME__'
    $Host.UI.RawUI.WindowTitle = "NovaTouchUpdater – $($request.Host) – PowerShell / WinRM"
    Write-Host ("Verbindung mit $($request.Host) wird hergestellt …")
    $session = ConnectHostConsole $request
    $global:NovaTouchUpdaterConsoleSession = $session
    $request.Password = $null
    Remove-Variable request
    Write-Host 'Interaktive PowerShell auf dem Ziel. CMD-Syntax: cmd /c "Befehl". Mit exit die Verbindung und dieses Fenster schließen.'
    # Switch the console host itself to the verified remote runspace. Calling
    # Enter-PSSession inside a startup script is not supported by PowerShell.
    $Host.PushRunspace($session.Runspace)
} catch {
    if ($null -ne $session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
    Write-Host ('Remote-Konsole nicht geöffnet: ' + $_.Exception.Message) -ForegroundColor Red
    $null = Read-Host 'Eingabetaste zum Schließen'
    exit 1
}
