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

try {
	$requestLine = [Console]::In.ReadLine()
	if ([string]::IsNullOrWhiteSpace($requestLine)) { throw 'Keine Anforderung erhalten.' }
	$req = $requestLine | ConvertFrom-Json
	if ($req.Action -ne 'restart-services') { throw "Unbekannte Aktion: $($req.Action)" }

	$restartPayment = [bool]$req.RestartPayment
	$restartFiscal = [bool]$req.RestartFiscal
	$startServerSuite = [bool]$req.StartServerSuite
	if (-not $restartPayment -and -not $restartFiscal -and -not $startServerSuite) {
		throw 'Kein Dienst ausgewählt.'
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
	finally {
		Remove-PSSession -Session $session -ErrorAction SilentlyContinue
	}

	$successMessages = @($restartResult.Results)
	$failureMessages = @($restartResult.Failures)
	$message = (@($successMessages) + @($failureMessages | ForEach-Object { "FEHLER: $_" }) -join '; ')
	Emit @{
		Event = 'result'
		Success = ($failureMessages.Count -eq 0)
		Message = $message
		Error = ($failureMessages -join '; ')
		Changed = ($successMessages.Count -gt 0)
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
