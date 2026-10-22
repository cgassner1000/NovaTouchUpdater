$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)

function Emit([hashtable]$message) {
	[Console]::Out.WriteLine(($message | ConvertTo-Json -Compress -Depth 12))
	[Console]::Out.Flush()
}

function Progress([string]$phase, [string]$message) {
	Emit @{ Event='progress'; Phase=$phase; Message=$message }
}

function RequestTerminalRestart {
	$shutdown = Join-Path $env:SystemRoot 'System32\shutdown.exe'
	$output = @(& $shutdown /r /t 30 /d 'p:4:1' /c 'Terminal-Neustart durch NovaTouch Updater' 2>&1)
	$exitCode = $LASTEXITCODE
	if ($exitCode -ne 0) {
		throw "Windows-Neustart konnte nicht angefordert werden (Exitcode ${exitCode}): $($output -join ' ')"
	}
	[pscustomobject]@{ Results=@('Windows-Neustart in 30 Sekunden angefordert'); Failures=@() }
}

function RequestPosRestart {
	$ErrorActionPreference = 'Stop'
	Set-StrictMode -Version Latest
	$exe = 'C:\Program Files (x86)\novatouch\bin\KassaPreh.exe'
	$outcome = @{ Results=@(); Failures=@(); Skipped=$false; Changed=$false }
	if (-not (Test-Path -LiteralPath $exe -PathType Leaf -ErrorAction Stop)) {
		return [pscustomobject]@{ Results=@("KassaPreh.exe nicht gefunden - übersprungen ($exe)"); Failures=@(); Skipped=$true; Changed=$false }
	}

	function Get-PosProcesses {
		@(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'KassaPreh.exe'" -ErrorAction Stop |
			Where-Object { $_.ExecutablePath -ieq $exe })
	}
	function Get-PosOwnerSid($processInfo) {
		$owner = Invoke-CimMethod -InputObject $processInfo -MethodName GetOwnerSid -ErrorAction Stop
		if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace([string]$owner.Sid)) {
			throw 'Der Benutzer von KassaPreh konnte nicht ermittelt werden. Es wurde kein weiterer Prozess beendet.'
		}
		[string]$owner.Sid
	}
	function Get-PosArguments([string]$commandLine) {
		$text = $commandLine.TrimStart()
		foreach ($prefix in @(
			('"' + $exe + '"')
			$exe
			'"KassaPreh.exe"'
			'KassaPreh.exe'
		)) {
			if ($text.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
				($text.Length -eq $prefix.Length -or [char]::IsWhiteSpace($text[$prefix.Length]))) {
				return $text.Substring($prefix.Length).TrimStart()
			}
		}
		throw 'Die ursprünglichen Startparameter von KassaPreh konnten nicht eindeutig gelesen werden. Der Prozess wurde nicht beendet.'
	}

	$named = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'KassaPreh.exe'" -ErrorAction Stop)
	if (@($named | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) }).Count -gt 0) {
		throw 'Der Pfad eines KassaPreh-Prozesses ist nicht lesbar. Der Prozess wurde nicht beendet.'
	}
	$processes = @($named | Where-Object { $_.ExecutablePath -ieq $exe })
	if ($processes.Count -eq 0) {
		return [pscustomobject]@{ Results=@('KassaPreh.exe läuft nicht - übersprungen'); Failures=@(); Skipped=$true; Changed=$false }
	}
	if ($processes.Count -ne 1) {
		throw 'Mehrere KassaPreh-Prozesse gefunden; keine eindeutige Benutzersitzung. Es wurde kein Prozess beendet.'
	}
	$original = $processes[0]
	$sessionId = [int]$original.SessionId
	if ($sessionId -le 0) {
		return [pscustomobject]@{ Results=@('Keine angemeldete Benutzersitzung für KassaPreh gefunden - übersprungen'); Failures=@(); Skipped=$true; Changed=$false }
	}
	$ownerSid = Get-PosOwnerSid $original
	if ($ownerSid -in @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')) {
		return [pscustomobject]@{ Results=@('KassaPreh läuft unter einem Dienstkonto - übersprungen'); Failures=@(); Skipped=$true; Changed=$false }
	}
	$ownerName = $ownerSid
	$ownerInfo = Invoke-CimMethod -InputObject $original -MethodName GetOwner -ErrorAction Stop
	if ($ownerInfo.ReturnValue -eq 0) { $ownerName = "$($ownerInfo.Domain)\$($ownerInfo.User)" }
	$arguments = Get-PosArguments ([string]$original.CommandLine)
	$originalId = [int]$original.ProcessId
	$taskName = 'NovaTouchUpdater-POS-' + [Guid]::NewGuid().ToString('N')
	$folder = $null
	$taskCreated = $false
	$registrationAttempted = $false
	try {
		# Prepare the exact interactive user/session before stopping the application.
		$scheduler = New-Object -ComObject 'Schedule.Service'
		[void]$scheduler.Connect()
		$folder = $scheduler.GetFolder('\')
		$definition = $scheduler.NewTask(0)
		$definition.RegistrationInfo.Description = 'Temporärer KassaPreh-Neustart durch NovaTouch Updater'
		$definition.Principal.UserId = $ownerSid
		$definition.Principal.LogonType = 3 # TASK_LOGON_INTERACTIVE_TOKEN; no password
		$definition.Principal.RunLevel = 0
		$definition.Settings.Enabled = $true
		$definition.Settings.AllowDemandStart = $true
		$definition.Settings.DisallowStartIfOnBatteries = $false
		$definition.Settings.StopIfGoingOnBatteries = $false
		$definition.Settings.ExecutionTimeLimit = 'PT0S'
		$action = $definition.Actions.Create(0)
		$action.Path = $exe
		$action.WorkingDirectory = Split-Path -Path $exe -Parent
		$action.Arguments = $arguments
		$registrationAttempted = $true
		$task = $folder.RegisterTaskDefinition($taskName, $definition, 2, $ownerSid, $null, 3, $null)
		$taskCreated = $true

		$current = @(Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $originalId" -ErrorAction Stop)
		if ($current.Count -ne 1 -or $current[0].ExecutablePath -ine $exe -or
			$current[0].CreationDate -ne $original.CreationDate -or [int]$current[0].SessionId -ne $sessionId -or
			(Get-PosOwnerSid $current[0]) -ne $ownerSid) {
			throw 'Der KassaPreh-Prozess hat sich während der Vorbereitung geändert. Es wurde kein Prozess beendet.'
		}
		$taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
		$killOutput = @(& $taskkill /PID $originalId /F 2>&1)
		$killExitCode = $LASTEXITCODE
		if ($killExitCode -ne 0) { throw "KassaPreh konnte nicht beendet werden (taskkill Exitcode ${killExitCode}): $($killOutput -join ' ')" }
		$outcome.Changed = $true
		for ($attempt = 0; $attempt -lt 25; $attempt++) {
			$remaining = @(Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $originalId" -ErrorAction Stop |
				Where-Object { $_.CreationDate -eq $original.CreationDate })
			if ($remaining.Count -eq 0) { break }
			Start-Sleep -Milliseconds 200
		}
		if ($remaining.Count -gt 0) { throw 'KassaPreh wurde nach taskkill nicht beendet. Es wurde kein zweiter Prozess gestartet.' }

		# Some installations have a watchdog. Do not launch a duplicate if it
		# already brought the application back in the intended user session.
		$reappeared = @(Get-PosProcesses | Where-Object { [int]$_.SessionId -eq $sessionId })
		if ($reappeared.Count -eq 0) {
			# TASK_RUN_USE_SESSION_ID | TASK_RUN_USER_SID: never the WinRM account.
			[void]$task.RunEx($null, 12, $sessionId, $ownerSid)
		}
		$verified = $false
		for ($attempt = 0; $attempt -lt 60; $attempt++) {
			$candidates = @(Get-PosProcesses | Where-Object { [int]$_.SessionId -eq $sessionId })
			foreach ($candidate in $candidates) {
				if ((Get-PosOwnerSid $candidate) -ne $ownerSid) { continue }
				Start-Sleep -Milliseconds 500
				$stable = @(Get-PosProcesses | Where-Object { $_.ProcessId -eq $candidate.ProcessId -and $_.CreationDate -eq $candidate.CreationDate -and [int]$_.SessionId -eq $sessionId })
				if ($stable.Count -eq 1 -and (Get-PosOwnerSid $stable[0]) -eq $ownerSid) { $verified = $true; break }
			}
			if ($verified) { break }
			Start-Sleep -Milliseconds 500
		}
		if (-not $verified) { throw "KassaPreh-Start konnte innerhalb von 30 Sekunden nicht für $ownerName in Sitzung $sessionId bestätigt werden." }
		$outcome.Results += "KassaPreh läuft als $ownerName in Sitzung $sessionId (Prozess und Benutzer geprüft)"
	}
	catch { $outcome.Failures += $_.Exception.Message }
	finally {
		# Also handle a registration that threw after creating its task.
		if ($registrationAttempted -and -not $taskCreated) {
			try { $taskCreated = $null -ne $folder.GetTask($taskName) } catch { }
		}
		if ($taskCreated) {
			$cleanupError = ''
			for ($attempt = 0; $attempt -lt 3; $attempt++) {
				try { [void]$folder.DeleteTask($taskName, 0); $cleanupError = ''; break }
				catch { $cleanupError = $_.Exception.Message; Start-Sleep -Milliseconds 250 }
			}
			if ($cleanupError) {
				$outcome.Failures += "Temporäre Aufgabe '$taskName' konnte nicht gelöscht werden: $cleanupError"
			}
			else { $outcome.Results += 'Temporäre Windows-Aufgabe gelöscht' }
		}
	}
	[pscustomobject]$outcome
}

try {
	$requestLine = [Console]::In.ReadLine()
	if ([string]::IsNullOrWhiteSpace($requestLine)) { throw 'Keine Anforderung erhalten.' }
	$req = $requestLine | ConvertFrom-Json
	if ($req.Action -notin @('restart-services', 'restart-terminal', 'restart-pos')) { throw "Unbekannte Aktion: $($req.Action)" }

	$restartPayment = $false
	$restartFiscal = $false
	$startServerSuite = $false
	if ($req.Action -eq 'restart-services') {
		$restartPayment = [bool]$req.RestartPayment
		$restartFiscal = [bool]$req.RestartFiscal
		$startServerSuite = [bool]$req.StartServerSuite
		if (-not $restartPayment -and -not $restartFiscal -and -not $startServerSuite) {
			throw 'Kein Dienst ausgewählt.'
		}
	}

	$sessionOptions = New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 180000
	$sessionArgs = @{
		ComputerName = [string]$req.Host
		SessionOption = $sessionOptions
		ErrorAction = 'Stop'
	}
	if ([bool]$req.UseSsl) { $sessionArgs.UseSSL = $true }
	if (-not [string]::IsNullOrWhiteSpace([string]$req.Username)) {
		$secure = ConvertTo-SecureString -String ([string]$req.Password) -AsPlainText -Force
		$sessionArgs.Credential = New-Object System.Management.Automation.PSCredential([string]$req.Username, $secure)
	}

	Progress 'Verbindung' 'WinRM-Verbindung wird aufgebaut'
	$session = New-PSSession @sessionArgs
	try {
		if ($req.Action -eq 'restart-terminal') {
			Progress 'Windows-Neustart' 'Windows-Neustart in 30 Sekunden wird angefordert'
			$restartResult = Invoke-Command -Session $session -ScriptBlock ${function:RequestTerminalRestart} -ErrorAction Stop
		}
		elseif ($req.Action -eq 'restart-pos') {
			Progress 'POS-Neustart' 'KassaPreh wird in der angemeldeten Benutzersitzung neu gestartet; die temporäre Aufgabe wird anschließend gelöscht'
			$restartResult = Invoke-Command -Session $session -ScriptBlock ${function:RequestPosRestart} -ErrorAction Stop
		}
		else {
			$restartResult = Invoke-Command -Session $session -ArgumentList $restartPayment, $restartFiscal, $startServerSuite -ErrorAction Stop -ScriptBlock {
			param([bool]$restartPayment, [bool]$restartFiscal, [bool]$startServerSuite)
			$selectedServices = @(Get-Service -ErrorAction Stop | Where-Object {
				($restartPayment -and $_.Name -match '^nt\.payment(?:\.|$)') -or
				($restartFiscal -and $_.Name -match '^nt\.fiscal(?:\.|$)') -or
				($startServerSuite -and $_.Name -match '^nt3_server_suite')
			})
			$results = @()
			$failures = @()
			foreach ($selection in @(
				@{ Selected = $restartPayment; Pattern = '^nt\.payment(?:\.|$)'; Label = 'nt.payment' },
				@{ Selected = $restartFiscal; Pattern = '^nt\.fiscal(?:\.|$)'; Label = 'nt.fiscal' },
				@{ Selected = $startServerSuite; Pattern = '^nt3_server_suite'; Label = 'nt3_server_suite' }
			)) {
				$matchingServices = @($selectedServices | Where-Object { $_.Name -match $selection.Pattern })
				if ($selection.Selected -and $matchingServices.Count -eq 0) {
					$failures += "$($selection.Label): kein passender Dienst gefunden"
				}
			}
			foreach ($serviceInfo in $selectedServices) {
				$name = [string]$serviceInfo.Name
				try {
					if ($name -notmatch '^(nt\.(payment|fiscal)(?:\.|$)|nt3_server_suite)') {
						throw "Nicht erlaubter Dienstname: $name"
					}
					$service = Get-Service -Name $name -ErrorAction Stop
					if ($service.Status -ne 'Stopped') {
						Stop-Service -Name $name -Force -ErrorAction Stop
						$service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
					}
					Start-Service -Name $name -ErrorAction Stop
					(Get-Service -Name $name -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
					$results += "$name läuft"
				}
				catch {
					$failures += "$name`: $($_.Exception.Message)"
				}
			}
			[pscustomobject]@{ Results = @($results); Failures = @($failures) }
			}
		}
	}
	finally {
		Remove-PSSession -Session $session -ErrorAction SilentlyContinue
	}

	$successMessages = @($restartResult.Results)
	$failureMessages = @($restartResult.Failures)
	$skipped = $restartResult.PSObject.Properties['Skipped'] -and [bool]$restartResult.Skipped
	$changed = $successMessages.Count -gt 0
	if ($restartResult.PSObject.Properties['Changed']) { $changed = [bool]$restartResult.Changed }
	$message = (@($successMessages) + @($failureMessages | ForEach-Object { "FEHLER: $_" }) -join '; ')
	Emit @{
		Event = 'result'
		Success = ($failureMessages.Count -eq 0)
		Message = $message
		Error = ($failureMessages -join '; ')
		Changed = $changed
		Skipped = [bool]$skipped
		Services = @()
	}
}
catch {
	$errorText = $_.Exception.Message
	if ($_.InvocationInfo -and -not [string]::IsNullOrWhiteSpace([string]$_.InvocationInfo.PositionMessage)) {
		$position = ([string]$_.InvocationInfo.PositionMessage -replace '[\r\n]+', ' ').Trim()
		$errorText += " | Position: $position"
	}
	Emit @{ Event='result'; Success=$false; Message='Fehler'; Error=$errorText; Services=@() }
	exit 1
}
