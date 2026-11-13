using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void WinRmClientSetupTests()
    {
        using var fixture = new WinRmCaFixture();
        using var authority = WinRmCertificateAuthority.Load(fixture.CertificatePath, fixture.KeyPath);
        using var material = authority.Issue("POS01.example.invalid");
        using var leaf = new X509Certificate2(material.Pfx, material.Password, X509KeyStorageFlags.EphemeralKeySet);
        var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert(names.EnumerateDnsNames().Contains("POS01.example.invalid") && names.EnumerateDnsNames().Contains("POS01") &&
            names.EnumerateDnsNames().Contains("localhost") && names.EnumerateIPAddresses().Contains(IPAddress.Loopback),
            "HTTPS certificate covers the selected FQDN, short hostname and local listener probe");
        Assert(leaf.HasPrivateKey && !leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority &&
            leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Single().Value == "1.3.6.1.5.5.7.3.1",
            "each client receives a non-CA certificate with Server Authentication usage and its own key");
        using (var caKey = fixture.Certificate.GetRSAPublicKey())
        using (var leafKey = leaf.GetRSAPublicKey())
            Assert(!caKey!.ExportSubjectPublicKeyInfo().SequenceEqual(leafKey!.ExportSubjectPublicKeyInfo()), "the common CA private key is never used as the client key");
        var bundle = new X509Certificate2Collection();
        bundle.Import(material.Pfx, material.Password, X509KeyStorageFlags.EphemeralKeySet);
        Assert(bundle.Count == 1 && bundle[0].Thumbprint == leaf.Thumbprint &&
            !new X509Certificate2(material.Issuer).HasPrivateKey, "exported setup contains only the client private key and public CA");
        foreach (var certificate in bundle) certificate.Dispose();
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(fixture.Certificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Isolated crypto test; production TLS verification stays enabled.
            Assert(chain.Build(leaf) && chain.ChainElements.Count == 2, "the generated client certificate is signed by the selected common CA");
        }
        WinRmRevocationTests(fixture.Certificate, leaf, material.RevocationList);
        using (var ipMaterial = authority.Issue("192.0.2.20"))
        using (var ipCertificate = new X509Certificate2(ipMaterial.Pfx, ipMaterial.Password, X509KeyStorageFlags.EphemeralKeySet))
            Assert(ipCertificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses().Contains(IPAddress.Parse("192.0.2.20")),
                "IP-based targets receive an IP subject alternative name");

        string http = WinRmClientSetup.CreateScript("POS01", false, 443);
        string httpCommand = WinRmClientSetup.OneLine(http, false);
        Assert(httpCommand == "powershell.exe -NoLogo -NoProfile -Command { Enable-PSRemoting -SkipNetworkProfileCheck -Force }",
            "HTTP copies only the standard WinRM setup command without host checks, firewall scripting or Base64");
        Assert(httpCommand == WinRmClientSetup.OneLine(WinRmClientSetup.CreateScript("192.0.2.20", false, 0), false),
            "the simple HTTP command works independently of hostname, target IP, HTTPS port and local network adapters");
        WinRmHttpShellTests();
        string https = WinRmClientSetup.CreateScript("POS01.example.invalid", true, 443, material);
        string command = WinRmClientSetup.OneLine(https, true);
        Assert(https.Contains("$Port=443;") && https.Contains("Transport = 'HTTPS'") && https.Contains("-UseSSL -Port $Port") &&
            https.Contains("-Profile Domain,Private -RemoteAddress Any") && https.Contains("-Profile Public -RemoteAddress LocalSubnet") &&
            !https.Contains("ManagementAddresses"), "HTTPS uses the selected port and Windows network profile boundaries without assuming the updater source IP");
        Assert(https.Contains("$env:PSModulePath = \"$PSHOME\\Modules;$env:ProgramFiles\\WindowsPowerShell\\Modules\""),
            "HTTPS setup uses native Windows PowerShell modules even when launched by PowerShell 7 or a separate process");
        Assert(command.Length < 32760 && !command.Contains('\n') &&
            Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1])) == https, "the copied HTTPS command fits one Windows command line and round-trips without quoting loss");
        Assert(!https.Contains(Convert.ToBase64String(fixture.Key.ExportPkcs8PrivateKey())) &&
            !https.Contains("SkipCACheck") && !https.Contains("SkipCNCheck") && !https.Contains("AllowUnencrypted"),
            "the client setup never contains the CA private key or disables TLS certificate checks");
        Assert(RejectsSetup(() => WinRmClientSetup.CreateScript("POS01';exit", false, 0)) &&
            RejectsSetup(() => WinRmClientSetup.CreateScript("POS01", true, 443)) &&
            RejectsSetup(() => WinRmClientSetup.CreateScript("POS01", true, 0, material)),
            "invalid hostnames, missing HTTPS material and invalid HTTPS ports are rejected");
        using (var nonCa = new WinRmCaFixture(isCa: false))
            Assert(RejectsSetup(() => WinRmCertificateAuthority.Load(nonCa.CertificatePath, nonCa.KeyPath)), "a normal server certificate cannot be misused as an issuing CA");
        using (var expired = new WinRmCaFixture(expired: true))
            Assert(RejectsSetup(() => WinRmCertificateAuthority.Load(expired.CertificatePath, expired.KeyPath)), "expired shared certificates are rejected before setup generation");
        using (var other = new WinRmCaFixture())
            Assert(RejectsSetup(() => WinRmCertificateAuthority.Load(fixture.CertificatePath, other.KeyPath)), "a mismatched PEM key cannot sign client certificates");

        WinRmSetupDialogTests(fixture, false);
        WinRmSetupDialogTests(fixture, true);
        WinRmDefaultCertificateTests(fixture);
        material.Dispose();
        Assert(material.Pfx.All(value => value == 0), "cached client private-key bytes are cleared on disposal");
    }

    private static bool RejectsSetup(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or CryptographicException) { return true; }
    }

    private static void WinRmPemTest(string certificatePath, string keyPath)
    {
        using var authority = WinRmCertificateAuthority.Load(certificatePath, keyPath);
        using var issuer = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
        using var material = authority.Issue(Environment.MachineName);
        using var leaf = new X509Certificate2(material.Pfx, material.Password, X509KeyStorageFlags.EphemeralKeySet);
        WinRmRevocationTests(issuer, leaf, material.RevocationList);
        string command = WinRmClientSetup.OneLine(WinRmClientSetup.CreateScript(Environment.MachineName, true, 5986, material), true);
        Console.WriteLine("PASS supplied PEM pair signs a valid host certificate; one-line length: " + command.Length);
    }

    private static void WinRmSetupDialogTests(WinRmCaFixture fixture, bool https)
    {
        UiTheme.SetMode(https ? "Dark" : "Light");
        using var dialog = new WinRmTestForm(["POS01.example.invalid", "POS02.example.invalid"], "setup-user", "synthetic-password", 2, https, 443);
        _ = dialog.Handle;
        T Read<T>(string name) => (T)typeof(WinRmTestForm).GetField(name, Private)!.GetValue(dialog)!;
        void Write(string name, object value) => typeof(WinRmTestForm).GetField(name, Private)!.SetValue(dialog, value);
        string copied = "";
        Write("copyText", (Action<string>)(value => copied = value));
        Write("showConfigurationError", (Action<string>)(_ => throw new InvalidOperationException("Client setup generation unexpectedly failed.")));
        bool trusted = false;
        Write("isAuthorityTrusted", (Func<WinRmCertificateAuthority, bool>)(_ => trusted));
        int trusts = 0;
        Write("trustAuthority", (Action<WinRmCertificateAuthority>)(_ => { trusts++; trusted = true; }));
        if (https) typeof(WinRmTestForm).GetMethod("LoadCertificateFiles", Private)!.Invoke(dialog, [fixture.CertificatePath, fixture.KeyPath, false]);
        var grid = Read<DataGridView>("grid");
        Assert(grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText).SequenceEqual(["Host", "Protokoll / Port", "TCP", "Ergebnis", "Einrichten"]),
            "WinRM shows five columns without New-PSSession and Invoke-Command");
        var worker = new SetupDiagnosticRunner(https);
        Task first = dialog.RunDiagnosticsAsync(worker);
        Assert(grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["clientSetup"].Value is WinRmSetupState.None), "pending diagnostics offer no repair command");
        worker.Prepared.SetResult(true);
        Complete(first);
        Assert(grid.Rows[0].Cells["clientSetup"].Value is WinRmSetupState.None && grid.Rows[1].Cells["clientSetup"].Value is WinRmSetupState.Ready,
            "only failed host tests offer the copy icon");
        Assert(grid.Rows[1].Cells["clientSetup"].AccessibilityObject.Name == "Client-Einrichtung kopieren", "the icon has an accessible action name");
        Preview("copy");
        Complete(dialog.CopyClientSetupAsync(0));
        Assert(copied.Length == 0, "successful rows cannot copy a repair command");
        Complete(dialog.CopyClientSetupAsync(1));
        Assert(copied.StartsWith(https ? "powershell.exe -NoLogo -NoProfile -EncodedCommand " : "powershell.exe -NoLogo -NoProfile -Command { "),
            "the failed row copies a self-contained PowerShell one-liner in the appropriate format");
        Assert(grid.Rows[1].Cells["clientSetup"].Value is WinRmSetupState.Copied && grid.Rows[1].Cells["clientSetup"].AccessibilityObject.Name == "Kopiert; erneut kopieren",
            "successful clipboard copy replaces the icon with a checkmark and accessible confirmation");
        string decoded = https ? Encoding.Unicode.GetString(Convert.FromBase64String(copied.Split(' ')[^1])) : copied;
        Assert(https ? decoded.Contains("$ExpectedHost='POS02.example.invalid';") && decoded.Contains("$Port=443;")
            : decoded.Contains("Enable-PSRemoting -SkipNetworkProfileCheck -Force") && !decoded.Contains("ExpectedHost") && !decoded.Contains("CertificateData"),
            "HTTPS uses the selected host and port while HTTP needs only the standard setup command");
        string previous = copied;
        Complete(dialog.CopyClientSetupAsync(1));
        Assert(previous == copied, "repeated copying reuses the same host certificate within the dialog");
        Assert(trusts == (https ? 1 : 0) && !Read<TextBox>("details").Text.Contains("CertificateData") && grid.Rows[1].Tag is string &&
            grid.Rows.Cast<DataGridViewRow>().SelectMany(row => row.Cells.Cast<DataGridViewCell>()).All(cell => !cell.ToolTipText.Contains("EncodedCommand")),
            "HTTPS automatically trusts the selected CA once; table details and tooltips never expose private material");

        Preview("copied");
        Assert(grid.Columns["clientSetup"].Width is >= 70 and <= 100 &&
            !All(dialog).OfType<Button>().Where(button => button.Name.Contains("Certificate", StringComparison.OrdinalIgnoreCase)).Any(button => !https && button.Visible),
            "the icon column is compact and HTTP hides all certificate controls");
        bool copyFailed = false;
        Write("copyText", (Action<string>)(_ => throw new System.Runtime.InteropServices.ExternalException("Synthetic clipboard failure")));
        Write("showConfigurationError", (Action<string>)(_ => copyFailed = true));
        Complete(dialog.CopyClientSetupAsync(1));
        Assert(copyFailed && grid.Rows[1].Cells["clientSetup"].Value is WinRmSetupState.Ready && !grid.Rows[1].Cells["clientSetup"].ToolTipText.StartsWith("Kopiert"),
            "a clipboard error restores the copy icon without a false copied confirmation");
        if (https)
        {
            var trust = Read<Button>("trustCertificate");
            Assert(trusts == 1 && !trust.Enabled && trust.Text == "CA vertraut", "HTTPS trust is automatic and confirmed without a manual activation step");
        }
        var retryWorker = new SetupDiagnosticRunner(https) { AllSucceed = true };
        Task second = dialog.RunDiagnosticsAsync(retryWorker);
        Assert(grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["result"].Value is "Wartet" && row.Cells["clientSetup"].Value is WinRmSetupState.None && row.Tag is null && row.Cells["clientSetup"].ToolTipText == ""),
            "retry immediately clears old errors, copy actions and failure details");
        retryWorker.Prepared.SetResult(true);
        Complete(second);
        Assert(grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["result"].Value is "OK" && row.Cells["clientSetup"].Value is WinRmSetupState.None) &&
            retryWorker.Requests.Where(request => request.Action == "winrm-test").All(request => request.Password == ""),
            "retry retains credentials only while testing and clears successful repair actions");
        Assert(trusts == (https ? 1 : 0), "repeated diagnostics do not repeat an already completed CA trust change");
        UiTheme.SetMode("System");

        void Preview(string state)
        {
            foreach (Control control in All(dialog)) _ = control.Handle;
            for (int pass = 0; pass < 3; pass++) Layout(dialog);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "winrm-setup-" + (https ? "https-dark-" : "http-light-") + state + ".png"));
        }
    }

    private static void WinRmDefaultCertificateTests(WinRmCaFixture fixture)
    {
        using var embedded = WinRmCertificateAuthority.LoadDefault();
        Assert(embedded.Thumbprint == "E2831830AC94909FF409AB72F9E01581FEADD832", "the supplied common certificate is embedded in the application");
        using (var material = embedded.Issue("BUILTIN.example.invalid"))
        using (var leaf = new X509Certificate2(material.Pfx, material.Password, X509KeyStorageFlags.EphemeralKeySet))
        using (var issuer = new X509Certificate2(material.Issuer))
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(issuer);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert(leaf.HasPrivateKey && chain.Build(leaf), "the built-in certificate pair can issue valid host certificates without external PEM files");
        }
        int savedDefaults = 0, trusts = 0;
        var trusted = new HashSet<string>();
        using var dialog = new WinRmTestForm(["POS02.example.invalid"], "setup-user", "synthetic-password", 1, true, 443,
            certificatePathsChanged: (certificate, key) => { Assert(certificate == "" && key == "", "choosing the standard clears only the optional PEM paths"); savedDefaults++; });
        _ = dialog.Handle;
        T Read<T>(string name) => (T)typeof(WinRmTestForm).GetField(name, Private)!.GetValue(dialog)!;
        void Write(string name, object value) => typeof(WinRmTestForm).GetField(name, Private)!.SetValue(dialog, value);
        Write("isAuthorityTrusted", (Func<WinRmCertificateAuthority, bool>)(ca => trusted.Contains(ca.Thumbprint)));
        Write("trustAuthority", (Action<WinRmCertificateAuthority>)(ca => { trusted.Add(ca.Thumbprint); trusts++; }));
        Assert(Read<WinRmCertificateAuthority>("authority").Thumbprint == embedded.Thumbprint && Read<bool>("usingDefaultCertificate"),
            "HTTPS without a certificate selection immediately uses the built-in certificate");
        string copied = "";
        Write("copyText", (Action<string>)(command => copied = command));
        Write("showConfigurationError", (Action<string>)(_ => throw new InvalidOperationException("Built-in HTTPS setup unexpectedly failed.")));
        var worker = new SetupDiagnosticRunner(true);
        Task operation = dialog.RunDiagnosticsAsync(worker);
        Assert(trusts == 1 && worker.Requests.Count == 1, "the default CA is trusted automatically before any remote diagnostic starts");
        worker.Prepared.SetResult(true); Complete(operation);
        Assert(Read<Label>("certificateStatus").Text.Contains("Standardzertifikat") && Read<TextBox>("details").Text.Contains("Eingebautes HTTPS-Standardzertifikat"),
            "the active built-in certificate and automatic preparation are visible in the dialog");
        Complete(dialog.CopyClientSetupAsync(0));
        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(copied.Split(' ')[^1]));
        Assert(decoded.Contains("$ExpectedHost='POS02.example.invalid';") && decoded.Contains("$CertificateData=") && !decoded.Contains("novacom_default_cert.pem") && trusts == 1,
            "the embedded default produces self-contained HTTPS client setup without file selection or repeated trust changes");
        typeof(WinRmTestForm).GetMethod("LoadCertificateFiles", Private)!.Invoke(dialog, [fixture.CertificatePath, fixture.KeyPath, false]);
        Assert(!Read<bool>("usingDefaultCertificate") && Read<WinRmCertificateAuthority>("authority").Thumbprint != embedded.Thumbprint,
            "an explicitly selected certificate takes precedence over the built-in default");
        typeof(WinRmTestForm).GetMethod("LoadDefaultCertificate", Private)!.Invoke(dialog, [true]);
        Assert(savedDefaults == 1 && Read<bool>("usingDefaultCertificate"), "the standard certificate can be selected again and remembered without file paths");

        using var missing = new WinRmTestForm([], "", "", 1, true, certificatePath: Path.Combine(fixture.CertificatePath, "missing.pem"), privateKeyPath: "missing.key");
        Assert(((WinRmCertificateAuthority)typeof(WinRmTestForm).GetField("authority", Private)!.GetValue(missing)!).Thumbprint == embedded.Thumbprint &&
            All(missing).Single(control => control.Name == "winRmCertificateStatus").Text.Contains("nicht verfügbar"),
            "unavailable custom files fall back to the embedded certificate with a visible explanation");

        Write("trustAuthority", (Action<WinRmCertificateAuthority>)(_ => throw new UnauthorizedAccessException("Synthetic trust permission failure")));
        trusted.Clear();
        var denied = new SetupDiagnosticRunner(true) { AllSucceed = true };
        Task deniedTest = dialog.RunDiagnosticsAsync(denied);
        denied.Prepared.SetResult(true); Complete(deniedTest);
        Assert(Read<Label>("certificateStatus").Text.Contains("Administratorrechte") && Read<TextBox>("details").Text.Contains("nicht automatisch eingerichtet"),
            "missing trust-store permissions are reported while independent diagnostics still complete");
    }

    private static void WinRmHttpShellTests()
    {
        // No WinRM commands: exercise only cross-shell ScriptBlock argument
        // passing and literal quotes/$ so testing cannot configure this PC.
        string probe = WinRmClientSetup.OneLine("$value='a\"b ''c $literal'; if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Wrong PowerShell endpoint host' }; Write-Output ('NTU-HTTP-PROBE:' + $value);", false);
        string windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        string powerShell7 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        foreach (string shell in new[] { windowsPowerShell, powerShell7 }.Where(File.Exists))
        {
            var start = new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + probe)));
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(true); throw new InvalidOperationException("PowerShell argument test timed out."); }
            Assert(process.ExitCode == 0 && output.GetAwaiter().GetResult().Contains("NTU-HTTP-PROBE:a\"b 'c $literal"),
                "readable HTTP command preserves literals and runs in Windows PowerShell when pasted into " + Path.GetFileName(shell) + ": " + error.GetAwaiter().GetResult());
        }
    }

    private sealed class SetupDiagnosticRunner(bool https) : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal TaskCompletionSource<bool> Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool AllSucceed { get; init; }
        public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action == "winrm-prepare")
            {
                await Prepared.Task.WaitAsync(ct);
                return new() { Success = true, Message = "WinRM lokal funktionsfähig. TrustedHosts unverändert." };
            }
            Assert(request.Password == "synthetic-password" && request.Username == "setup-user" && request.UseSsl == https && request.HttpsPort == 443,
                "initial and repeated diagnostics retain the configured credentials and transport");
            bool success = AllSucceed || request.Host.StartsWith("POS01");
            return new() { Success = success, Tcp = success ? "OK" : "Fehler", Session = success ? "OK" : "Fehler",
                Command = success ? "OK" : "Übersprungen (keine Sitzung)", Error = success ? "" : "Synthetischer Verbindungsfehler." };
        }
        public void Dispose() { }
    }

    private sealed class WinRmCaFixture : IDisposable
    {
        private readonly string root = Path.Combine(TestWorkspace.Artifacts, "winrm-ca-" + Guid.NewGuid().ToString("N"));
        internal RSA Key { get; } = RSA.Create(2048);
        internal X509Certificate2 Certificate { get; }
        internal string CertificatePath => Path.Combine(root, "test-ca.pem");
        internal string KeyPath => Path.Combine(root, "test-key.pem");
        internal WinRmCaFixture(bool isCa = true, bool expired = false)
        {
            Directory.CreateDirectory(root);
            var request = new CertificateRequest("CN=Synthetic WinRM test CA", Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(isCa ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 500));
            File.WriteAllText(CertificatePath, Certificate.ExportCertificatePem());
            File.WriteAllText(KeyPath, Key.ExportPkcs8PrivateKeyPem());
        }
        public void Dispose()
        {
            Certificate.Dispose(); Key.Dispose();
            File.Delete(CertificatePath); File.Delete(KeyPath); Directory.Delete(root);
        }
    }
}
