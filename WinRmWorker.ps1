# Read-only WinRM diagnostics; executed locally by WorkerRunner.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)

function Test-TcpPort([string]$computer, [int]$port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($computer, $port)
        if (-not $connect.Wait(5000)) { throw "TCP-Verbindung zu ${computer}:$port nach 5 Sekunden abgebrochen (Timeout)." }
        $connect.GetAwaiter().GetResult()
    }
    finally { $client.Dispose() }
}

function Error-Details($record) {
    $text = $record.Exception.ToString()
    $text += "`r`nFehler-ID: $($record.FullyQualifiedErrorId)`r`nKategorie: $($record.CategoryInfo)"
    if ($record.ErrorDetails) { $text += "`r`n$($record.ErrorDetails.Message)" }
    if ($record.InvocationInfo) { $text += "`r`n$($record.InvocationInfo.PositionMessage)" }
    return $text
}

$result = @{ Event='result'; Success=$false; Tcp='Nicht getestet'; Session='Nicht getestet'; Command='Nicht getestet'; Error=''; Message=''; Services=@() }
$session = $null
$details = [Collections.Generic.List[string]]::new()
try {
    $req = [Console]::In.ReadLine() | ConvertFrom-Json
    $port = if ([bool]$req.UseSsl) { 5986 } else { 5985 }
    if ([bool]$req.UseSsl -and $null -ne $req.PSObject.Properties['HttpsPort']) { $port = [int]$req.HttpsPort }
    if ($port -lt 1 -or $port -gt 65535) { throw 'HTTPS-Port muss zwischen 1 und 65535 liegen.' }
    try {
        Test-TcpPort ([string]$req.Host) $port
        $result.Tcp = 'OK'
    }
    catch {
        $result.Tcp = 'Fehler'
        $details.Add("TCP ${port}:`r`n$(Error-Details $_)")
    }

    # Attempt remoting independently, even if the preceding TCP probe failed.
    $sessionArgs = @{
        ComputerName = [string]$req.Host
        Port = $port
        SessionOption = (New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 20000 -CancelTimeout 5000)
        ErrorAction = 'Stop'
    }
    if ([bool]$req.UseSsl) { $sessionArgs.UseSSL = $true }
    if (-not [string]::IsNullOrWhiteSpace([string]$req.Username)) {
        $secure = ConvertTo-SecureString ([string]$req.Password) -AsPlainText -Force
        $sessionArgs.Credential = [Management.Automation.PSCredential]::new([string]$req.Username, $secure)
    }
    try {
        $session = New-PSSession @sessionArgs
        $result.Session = 'OK'
    }
    catch {
        $result.Session = 'Fehler'
        $result.Command = 'Übersprungen (keine Sitzung)'
        $details.Add("New-PSSession:`r`n$(Error-Details $_)")
    }
    if ($null -ne $session) {
        try {
            # Only returns a constant. No files, services or settings are modified.
            $reply = @(Invoke-Command -Session $session -ScriptBlock { 'NovaTouchUpdater.WinRM.OK' } -ErrorAction Stop)
            if ($reply.Count -ne 1 -or [string]$reply[0] -ne 'NovaTouchUpdater.WinRM.OK') {
                throw 'Unerwartete Antwort des WinRM-Testbefehls.'
            }
            $result.Command = 'OK'
        }
        catch {
            $result.Command = 'Fehler'
            $details.Add("Invoke-Command:`r`n$(Error-Details $_)")
        }
    }
}
catch { $details.Add((Error-Details $_)) }
finally {
    if ($null -ne $session) {
        try { Remove-PSSession -Session $session -ErrorAction Stop }
        catch { $details.Add("Remove-PSSession:`r`n$(Error-Details $_)") }
    }
}
$result.Success = $result.Tcp -eq 'OK' -and $result.Session -eq 'OK' -and $result.Command -eq 'OK' -and $details.Count -eq 0
$result.Message = if ($result.Success) { 'TCP, New-PSSession und Invoke-Command erfolgreich.' } else { 'WinRM-Test fehlgeschlagen; siehe Details.' }
$result.Error = $details -join "`r`n`r`n"
[Console]::Out.WriteLine(($result | ConvertTo-Json -Compress -Depth 6))
