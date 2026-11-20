# Shared final steps, also rendered as a readable HTTP one-liner.
$baseName = "NovaTouchUpdater-WinRM-$Transport-$Port";
foreach ($scope in 'Domain,Private','Public') {
    $name = $baseName;
    $remote = 'Any';
    if ($scope -eq 'Public') { $name += '-Public'; $remote = 'LocalSubnet' };
    $display = "NovaTouch Updater WinRM $Transport (TCP $Port, $scope)";
    $rule = @{ Name = $name; Direction = 'Inbound'; Action = 'Allow'; Enabled = 'True'; Protocol = 'TCP'; LocalPort = $Port; Profile = $scope.Split(','); RemoteAddress = $remote };
    if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) { Set-NetFirewallRule @rule -NewDisplayName $display }
    else { New-NetFirewallRule @rule -DisplayName $display | Out-Null };
    if ((Get-NetFirewallRule -PolicyStore ActiveStore -Name $name -ErrorAction Stop).Enabled -ne 'True') { throw "Firewallregel $name ist nicht aktiv." };
};
# Only retire other WinRM access after the selected listener responds.
Test-WSMan -ComputerName localhost -Port $Port -UseSSL:($Transport -eq 'HTTPS') | Out-Null;
$otherListeners = { Get-WSManInstance winrm/config/Listener -Enumerate | Where-Object { $_.Enabled -eq 'true' -and ($_.Transport -ne $Transport -or [int]$_.Port -ne $Port) } };
foreach ($item in (& $otherListeners)) {
    Set-WSManInstance winrm/config/Listener -SelectorSet @{ Transport = $item.Transport; Address = $item.Address } -ValueSet @{ Enabled = 'false' } | Out-Null;
};
foreach ($setting in 'EnableCompatibilityHttpListener','EnableCompatibilityHttpsListener') {
    if ((Get-WSManInstance winrm/config/service).$setting -eq 'true') {
        Set-WSManInstance winrm/config/service -ValueSet @{ $setting = 'false' } | Out-Null;
        if ((Get-WSManInstance winrm/config/service).$setting -eq 'true') { throw "WinRM-Richtlinie erzwingt $setting." };
    };
};
if (@(& $otherListeners).Count) { throw 'Ein weiterer WinRM-Listener ist aktiv. Gruppenrichtlinie auf dem Client prüfen.' };
$keepRules = @($baseName, "$baseName-Public");
$standardRules = "WINRM-$Transport-In-TCP*";
Get-NetFirewallRule -Name 'NovaTouchUpdater-WinRM-*','WINRM-HTTP-In-TCP*','WINRM-HTTPS-In-TCP*' -ErrorAction SilentlyContinue | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' -and $_.Enabled -eq 'True' -and $_.Name -notin $keepRules -and !($_.Name -like $standardRules -and ($Transport -eq 'HTTP' -or $Port -eq 5986)) } | Disable-NetFirewallRule | Out-Null;
Write-Host "WinRM $Transport / TCP $Port eingerichtet. Firewallregeln aktiv. Im Updater erneut prüfen." -ForegroundColor Green;
