# Run locally in Windows PowerShell as administrator on the client.
$ErrorActionPreference = 'Stop';
$Transport = 'HTTP';
$Port = 5985;
Enable-PSRemoting -SkipNetworkProfileCheck -Force;
$selector = @{ Address = '*'; Transport = $Transport };
$http = Get-WSManInstance winrm/config/Listener -Enumerate | Where-Object { $_.Transport -eq 'HTTP' -and $_.Address -eq '*' };
if (!$http) { New-WSManInstance winrm/config/Listener -SelectorSet $selector -ValueSet @{ Port = $Port; Enabled = 'true' } | Out-Null }
elseif ($http.Port -ne $Port -or $http.Enabled -ne 'true') { Set-WSManInstance winrm/config/Listener -SelectorSet $selector -ValueSet @{ Port = $Port; Enabled = 'true' } | Out-Null };
