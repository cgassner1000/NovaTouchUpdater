# One local process per polling round; bounded parallel, read-only WinRM probes.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)

function Emit([hashtable]$message) {
    [Console]::Out.WriteLine(($message | ConvertTo-Json -Compress -Depth 6))
    [Console]::Out.Flush()
}

function TestHostConnection {
    param([string]$computer, [pscredential]$credential, [bool]$useSsl, [int]$httpsPort = 5986)
    $ErrorActionPreference = 'Stop'
    $session = $null
    $connected = $false
    $message = ''
    try {
        if ($useSsl -and ($httpsPort -lt 1 -or $httpsPort -gt 65535)) { throw 'HTTPS-Port muss zwischen 1 und 65535 liegen.' }
        $sessionArgs = @{
            ComputerName = $computer
            Port = $(if ($useSsl) { $httpsPort } else { 5985 })
            SessionOption = (New-PSSessionOption -OpenTimeout 3000 -OperationTimeout 3000 -CancelTimeout 500)
            ErrorAction = 'Stop'
        }
        if ($useSsl) { $sessionArgs.UseSSL = $true }
        if ($null -ne $credential) { $sessionArgs.Credential = $credential }
        $session = New-PSSession @sessionArgs
        $connected = $true
        $message = 'WinRM erreichbar und Anmeldung erfolgreich.'
    }
    catch { $message = $_.Exception.Message }
    finally {
        if ($null -ne $session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
    }
    [pscustomobject]@{ Host=$computer; Connected=$connected; Message=$message }
}

$pool = $null
$active = [Collections.Generic.List[object]]::new()
try {
    $req = [Console]::In.ReadLine() | ConvertFrom-Json
    if ($req.Action -ne 'monitor-connections') { throw 'Unbekannte Verbindungsprüfung.' }
    $httpsPort = 5986
    if ([bool]$req.UseSsl -and $null -ne $req.PSObject.Properties['HttpsPort']) { $httpsPort = [int]$req.HttpsPort }
    if ([bool]$req.UseSsl -and ($httpsPort -lt 1 -or $httpsPort -gt 65535)) { throw 'HTTPS-Port muss zwischen 1 und 65535 liegen.' }
    $parallelism = [Math]::Max(1, [Math]::Min(40, [int]$req.Parallelism))
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($computer in @($req.Hosts | Select-Object -Unique)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$computer)) { $queue.Enqueue(([string]$computer).Trim()) }
    }
    $credential = $null
    if (-not [string]::IsNullOrWhiteSpace([string]$req.Username)) {
        $secure = ConvertTo-SecureString ([string]$req.Password) -AsPlainText -Force
        $credential = [Management.Automation.PSCredential]::new([string]$req.Username, $secure)
    }
    $pool = [RunspaceFactory]::CreateRunspacePool(1, $parallelism)
    $pool.Open()
    while ($queue.Count -gt 0 -or $active.Count -gt 0) {
        while ($queue.Count -gt 0 -and $active.Count -lt $parallelism) {
            $computer = $queue.Dequeue()
            $pipeline = [PowerShell]::Create()
            $pipeline.RunspacePool = $pool
            [void]$pipeline.AddScript(${function:TestHostConnection}.ToString()).AddArgument($computer).AddArgument($credential).AddArgument([bool]$req.UseSsl).AddArgument($httpsPort)
            try {
                $active.Add([pscustomobject]@{
                    Computer=$computer; Pipeline=$pipeline; Handle=$pipeline.BeginInvoke(); Started=[Diagnostics.Stopwatch]::StartNew()
                })
            }
            catch { $pipeline.Dispose(); throw }
        }
        foreach ($job in $active.ToArray()) {
            if (-not $job.Handle.IsCompleted -and $job.Started.ElapsedMilliseconds -lt 5000) { continue }
            $connected = $null
            $message = ''
            try {
                if (-not $job.Handle.IsCompleted) {
                    $job.Pipeline.Stop()
                    $connected = $false
                    $message = 'WinRM-Prüfung nach 5 Sekunden abgebrochen (Timeout).'
                }
                else {
                    $result = @($job.Pipeline.EndInvoke($job.Handle))
                    if ($result.Count -ne 1) { throw 'Keine eindeutige Antwort der WinRM-Prüfung.' }
                    $connected = [bool]$result[0].Connected
                    $message = [string]$result[0].Message
                }
            }
            catch { $message = $_.Exception.Message }
            finally { $job.Pipeline.Dispose(); [void]$active.Remove($job) }
            Emit @{ Event='progress'; Host=$job.Computer; Connected=$connected; Phase='Verbindung'; Message=$message }
        }
        if ($active.Count -gt 0) { Start-Sleep -Milliseconds 25 }
    }
    Emit @{ Event='result'; Success=$true; Message='Verbindungsprüfung abgeschlossen'; Services=@() }
}
catch { Emit @{ Event='result'; Success=$false; Error=$_.Exception.Message; Services=@() } }
finally {
    foreach ($job in $active) {
        try { $job.Pipeline.Stop() } catch { }
        $job.Pipeline.Dispose()
    }
    if ($null -ne $pool) { $pool.Close(); $pool.Dispose() }
}
