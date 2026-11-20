# Run locally in Windows PowerShell as administrator on the client.
$ErrorActionPreference = 'Stop';
$Transport = 'HTTP';
$Port = 5985;
$listenerValues = @{ Port = $Port; Enabled = 'true' };
$configureEndpoint = { Enable-PSRemoting -SkipNetworkProfileCheck -Force };
