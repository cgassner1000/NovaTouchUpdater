# Run locally in an elevated Windows PowerShell on the named client.
# Contains only this client's private key, never the private issuer key.
$ErrorActionPreference = 'Stop'
$env:PSModulePath = "$PSHOME\Modules;$env:ProgramFiles\WindowsPowerShell\Modules"
Set-StrictMode -Version Latest
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Diesen Einzeiler in einer PowerShell als Administrator auf dem Client ausführen.'
}
$flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($CertificateData), $CertificatePassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    if (-not $certificate.HasPrivateKey -or $certificate.Thumbprint -ine $ExpectedThumbprint -or $certificate.NotAfter -le (Get-Date)) { throw 'Das Clientzertifikat ist ungültig oder abgelaufen.' }
    $issuer = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($IssuerData))
    $rootStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    try {
        $rootStore.Open('ReadWrite')
        if (-not $rootStore.Certificates.Find('FindByThumbprint', $issuer.Thumbprint, $false).Count) { $rootStore.Add($issuer) }
    }
    finally { $rootStore.Close(); $issuer.Dispose() }
    $crlFile = Join-Path ([IO.Path]::GetTempPath()) ('NovaTouchUpdater-' + [Guid]::NewGuid().ToString('N') + '.crl')
    try {
        [IO.File]::WriteAllBytes($crlFile, [Convert]::FromBase64String($RevocationData))
        & "$env:SystemRoot\System32\certutil.exe" -f -addstore CA $crlFile | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Die signierte CA-Sperrliste konnte nicht installiert werden.' }
    }
    finally { [IO.File]::Delete($crlFile) }
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
    try {
        $store.Open('ReadWrite')
        $existing = @($store.Certificates.Find('FindByThumbprint', $certificate.Thumbprint, $false) | Where-Object { $_.HasPrivateKey })
        $certificate.Dispose()
        if ($existing.Count) { $certificate = $existing[0] }
        else {
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($CertificateData), $CertificatePassword, $flags)
            $store.Add($certificate)
        }
    }
    finally { $store.Close() }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    try {
        if ($rsa -is [Security.Cryptography.RSACng]) { $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName) }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName) }
        else { throw 'Der private RSA-Schlüssel wird nicht unterstützt.' }
        $acl = Get-Acl -LiteralPath $keyFile
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-20'), 'Read', 'Allow'))
        Set-Acl -LiteralPath $keyFile -AclObject $acl
    }
    finally { if ($rsa) { $rsa.Dispose() } }
    Set-Service -Name WinRM -StartupType Automatic
    Start-Service -Name WinRM
    $previousHttp = @(Get-ChildItem WSMan:\localhost\Listener | Where-Object { $_.Keys -contains 'Transport=HTTP' })
    $enabledRules = @(Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' } | ForEach-Object { $_.Name })
    try { Enable-PSRemoting -SkipNetworkProfileCheck -Force }
    finally {
        Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' -ErrorAction SilentlyContinue | Where-Object { $enabledRules -notcontains $_.Name } | Disable-NetFirewallRule | Out-Null
    }
    $selector = @{ Address = '*'; Transport = 'HTTPS' }
    $values = @{ Hostname = $certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false); CertificateThumbprint = $certificate.Thumbprint; Port = $Port; Enabled = 'true' }
    $listener = @(Get-ChildItem WSMan:\localhost\Listener | Where-Object { $_.Keys -contains 'Transport=HTTPS' -and $_.Keys -contains 'Address=*' })
    if ($listener.Count) { Set-WSManInstance -ResourceURI winrm/config/Listener -SelectorSet $selector -ValueSet $values | Out-Null }
    else { New-WSManInstance -ResourceURI winrm/config/Listener -SelectorSet $selector -ValueSet $values | Out-Null }
    if (-not $previousHttp.Count) {
        Remove-WSManInstance -ResourceURI winrm/config/Listener -SelectorSet @{ Address = '*'; Transport = 'HTTP' }
    }
    $ruleName = 'NovaTouchUpdater-WinRM-HTTPS-' + $Port
    if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) { Remove-NetFirewallRule -Name $ruleName }
    if (Get-NetFirewallRule -Name ($ruleName + '-Public') -ErrorAction SilentlyContinue) { Remove-NetFirewallRule -Name ($ruleName + '-Public') }
    New-NetFirewallRule -Name $ruleName -DisplayName 'NovaTouch Updater WinRM HTTPS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Domain,Private -RemoteAddress Any | Out-Null
    New-NetFirewallRule -Name ($ruleName + '-Public') -DisplayName 'NovaTouch Updater WinRM HTTPS (Public)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Public -RemoteAddress LocalSubnet | Out-Null
    Test-WSMan -ComputerName localhost -UseSSL -Port $Port -ErrorAction Stop | Out-Null
    Write-Host "WinRM HTTPS / $Port für $ExpectedHost eingerichtet. Im Updater erneut prüfen." -ForegroundColor Green
}
finally { $certificate.Dispose(); $CertificateData = ''; $CertificatePassword = '' }
