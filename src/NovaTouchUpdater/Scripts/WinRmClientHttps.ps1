# Run locally in an elevated Windows PowerShell on the named client.
# Contains only this client's private key, never the private issuer key.
$ErrorActionPreference = 'Stop'
$env:PSModulePath = "$PSHOME\Modules;$env:ProgramFiles\WindowsPowerShell\Modules"
Set-StrictMode -Version Latest
$setup = [Console]::ReadLine() | ConvertFrom-Json
$Port = [int]$setup.Port
Write-Host "WinRM HTTPS für $($setup.ExpectedHost) auf TCP-Port $Port einrichten."
$CertificateData = $setup.CertificateData
$CertificatePassword = $setup.CertificatePassword
$ExpectedThumbprint = $setup.ExpectedThumbprint
$IssuerData = $setup.IssuerData
$RevocationData = $setup.RevocationData
$setup = $null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Diesen Einzeiler in einer PowerShell als Administrator auf dem Client ausführen.'
}
$flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($CertificateData), $CertificatePassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    if (-not $certificate.HasPrivateKey -or $certificate.Thumbprint -ine $ExpectedThumbprint -or $certificate.NotAfter -le (Get-Date)) { throw 'Das Clientzertifikat ist ungültig oder abgelaufen.' }
    $listenerValues = @{ Hostname = $certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false); CertificateThumbprint = $certificate.Thumbprint; Port = $Port; Enabled = 'true' }
}
finally { $certificate.Dispose() }
$Transport = 'HTTPS'
$configureEndpoint = {
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
    $certificate = $null
    $rsa = $null
    try {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
        try {
            $store.Open('ReadWrite')
            $existing = @($store.Certificates.Find('FindByThumbprint', $ExpectedThumbprint, $false) | Where-Object { $_.HasPrivateKey })
            if ($existing.Count) { $certificate = $existing[0] }
            else {
                $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($CertificateData), $CertificatePassword, $flags)
                $store.Add($certificate)
            }
        }
        finally { $store.Close() }
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if ($rsa -is [Security.Cryptography.RSACng]) { $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName) }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName) }
        else { throw 'Der private RSA-Schlüssel wird nicht unterstützt.' }
        $acl = Get-Acl -LiteralPath $keyFile
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-20'), 'Read', 'Allow'))
        Set-Acl -LiteralPath $keyFile -AclObject $acl
    }
    finally {
        if ($rsa) { $rsa.Dispose() }
        if ($certificate) { $certificate.Dispose() }
        $CertificateData = ''; $CertificatePassword = ''
    }
    if (Test-Path -LiteralPath 'WSMan:\localhost\Plugin\Microsoft.PowerShell') {
        Set-PSSessionConfiguration -Name Microsoft.PowerShell -AccessMode Remote -Force | Out-Null
    }
    else {
        Register-PSSessionConfiguration -Name Microsoft.PowerShell -AccessMode Remote -Force | Out-Null
    }
}
