# WSMan policy values can be XML elements with a Source attribute.
function Read-WinRmValue($value) {
    if ($value -is [System.Xml.XmlElement]) { return $value.InnerText };
    return [string]$value;
};
function Read-WinRmBoolean($value) { return [Xml.XmlConvert]::ToBoolean((Read-WinRmValue $value).ToLowerInvariant()) };
function Get-SetupListeners {
    foreach ($entry in (Get-WSManInstance winrm/config/Listener -Enumerate)) {
        [pscustomobject]@{
            Address = Read-WinRmValue $entry.Address;
            Transport = Read-WinRmValue $entry.Transport;
            Port = [int](Read-WinRmValue $entry.Port);
            Enabled = Read-WinRmBoolean $entry.Enabled;
        };
    };
};
function Get-OtherListeners {
    Get-SetupListeners | Where-Object { $_.Enabled -and ($_.Transport -ne $Transport -or $_.Port -ne $Port) };
};
function Assert-ExclusiveTransport {
    $other = Get-OtherListeners | Select-Object -First 1;
    if ($other) { throw "WinRM-Listener $($other.Transport)/$($other.Port) ($($other.Address)) ist weiterhin aktiv. WinRM-Gruppenrichtlinie auf dem Client prüfen." };
    $serviceConfig = Get-WSManInstance winrm/config/service;
    foreach ($setting in $compatibilitySettings) {
        if (Read-WinRmBoolean $serviceConfig.$setting) { throw "WinRM-Kompatibilitätslistener $setting ist weiterhin aktiv. Gruppenrichtlinie prüfen." };
    };
};
function Disable-OtherWinRmAccess {
    foreach ($item in (Get-OtherListeners)) {
        try { Set-WSManInstance winrm/config/Listener -SelectorSet @{ Transport = $item.Transport; Address = $item.Address } -ValueSet @{ Enabled = 'false' } | Out-Null }
        catch { throw "WinRM-Listener $($item.Transport)/$($item.Port) ($($item.Address)) konnte nicht deaktiviert werden. $($_.Exception.Message)" };
    };
    $serviceConfig = Get-WSManInstance winrm/config/service;
    foreach ($setting in $compatibilitySettings) {
        if (Read-WinRmBoolean $serviceConfig.$setting) { Set-WSManInstance winrm/config/service -ValueSet @{ $setting = 'false' } | Out-Null };
    };
    Assert-ExclusiveTransport;
    Get-NetFirewallRule -Name 'NovaTouchUpdater-WinRM-*','WINRM-HTTP-In-TCP*','WINRM-HTTPS-In-TCP*' -ErrorAction SilentlyContinue | Where-Object {
        $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' -and $_.Enabled -eq 'True' -and
        $_.Name -notin $keepRules -and !($_.Name -like $standardRules -and ($Transport -eq 'HTTP' -or $Port -eq 5986))
    } | Disable-NetFirewallRule | Out-Null;
};
$baseName = "NovaTouchUpdater-WinRM-$Transport-$Port";
$keepRules = @($baseName, "$baseName-Public");
$standardRules = "WINRM-$Transport-In-TCP*";
$compatibilitySettings = 'EnableCompatibilityHttpListener','EnableCompatibilityHttpsListener';
Set-Service -Name WinRM -StartupType Automatic;
Start-Service -Name WinRM;
Disable-OtherWinRmAccess;
# Endpoint registration can restart WinRM and reapply listener settings, also on failure.
try {
    . $configureEndpoint;
    Disable-OtherWinRmAccess;
    $selector = @{ Address = '*'; Transport = $Transport };
    $listener = Get-SetupListeners | Where-Object { $_.Transport -eq $Transport -and $_.Address -eq '*' };
    if (!$listener) { New-WSManInstance winrm/config/Listener -SelectorSet $selector -ValueSet $listenerValues | Out-Null }
    elseif ($Transport -eq 'HTTPS' -or !$listener.Enabled -or $listener.Port -ne $Port) { Set-WSManInstance winrm/config/Listener -SelectorSet $selector -ValueSet $listenerValues | Out-Null };
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
    Test-WSMan -ComputerName localhost -Port $Port -UseSSL:($Transport -eq 'HTTPS') | Out-Null;
} finally { Disable-OtherWinRmAccess };
if (!(Get-SetupListeners | Where-Object { $_.Enabled -and $_.Transport -eq $Transport -and $_.Port -eq $Port -and $_.Address -eq '*' })) { throw "Gewählter WinRM-Listener $Transport/$Port ist nicht aktiv." };
Write-Host "Nur WinRM $Transport / TCP $Port ist aktiv. Firewallregeln eingerichtet. Im Updater erneut prüfen." -ForegroundColor Green;
