using System.Diagnostics;
using System.Text;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void BundledIrisProviderTests()
    {
        string root = Path.GetFullPath(Path.Combine(TestWorkspace.Artifacts, "bundled-iris-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "bundled provider test fixtures stay inside test artifacts");
        Directory.CreateDirectory(root);
        string keep = Path.Combine(root, "keep.txt");
        File.WriteAllText(keep, "Unrelated file must remain");
        try
        {
            string extracted;
            using (var payload = new BundledIrisProvider(root))
            {
                extracted = payload.EnsureExtracted();
                Assert(payload.EnsureExtracted() == extracted && new[] { "InterSystems.Data.IRISClient.dll", "InterSystems.Data.Bindings.dll", "IRISClient.LICENSE.txt", "Bindings.LICENSE.txt" }
                    .All(file => new FileInfo(Path.Combine(extracted, file)).Length > 0), "NuGet client, bindings and license notices are embedded and extracted once per provider owner");
                string script;
                using (Stream resource = typeof(WorkerRunner).Assembly.GetManifestResourceStream("NovaTouchUpdater.SoftwareDatabase.ps1")!)
                using (var reader = new StreamReader(resource)) script = reader.ReadToEnd();
                script = "$ErrorActionPreference = 'Stop'\n" + script;
                script += "\nfunction Progress { param($Phase,$Message,$Percent) }\n" + """
                    function GetSoftwareDatabaseProviderDirectories { throw 'Installed provider discovery must never run' }
                    $provider = InitializeSoftwareDatabaseProvider @($args[0]) @('IRIS')
                    if ($provider.BuilderType.Assembly.Location -ine (Join-Path $args[0] 'InterSystems.Data.IRISClient.dll')) { throw 'Client is not the bundled assembly' }
                    if ($provider.ObjectType.Assembly.Location -ine (Join-Path $args[0] 'InterSystems.Data.Bindings.dll')) { throw 'Bindings are not the bundled assembly' }
                    $connection = [Activator]::CreateInstance($provider.ConnectionType)
                    try {
                        if ($connection.State -ne [Data.ConnectionState]::Closed) { throw 'Unexpected database connection' }
                        $builder = [Activator]::CreateInstance($provider.BuilderType)
                        $builder.set_Server('127.0.0.1'); $builder.set_Port(1972); $builder.set_Namespace('USER')
                        $builder.set_Pooling($false); $builder.set_ConnectionTimeout(15)
                        if ($provider.BinaryStreamType.FullName -ne 'InterSystems.Data.IRISTypes.IRISBinaryStream') { throw 'Binary stream API unavailable' }
                        if ($null -eq $provider.ObjectType.GetConstructor([type[]]@($provider.ConnectionType,[string],[object[]]))) { throw 'Dynamic object constructor unavailable' }
                        if ($null -eq $provider.BinaryStreamType.GetMethod('Write',[type[]]@([byte[]],[int],[int]))) { throw 'Binary stream Write API unavailable' }
                        Write-Output 'BUNDLED_PROVIDER_OK'
                    } finally { $connection.Dispose() }
                    """;
                string scriptPath = Path.Combine(root, "load-bundled-provider.ps1");
                File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, extracted }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Bundled provider load did not finish"); }
                Assert(process.ExitCode == 0 && output.Result.Contains("BUNDLED_PROVIDER_OK") && error.Result.Length == 0,
                    "Windows PowerShell loads the actual packaged Framework DLLs and creates connection/stream types without opening a database: " + error.Result);
            }
            Assert(!Directory.Exists(extracted) && File.Exists(keep), "provider disposal removes only its own extracted directory");
            using var runner = new WorkerRunner();
            var request = new WorkerRequest { Action = "publish-msi-packages", LocalExecution = true, Host = "LOCALHOST" };
            Task<WorkerResult> result = runner.RunAsync(request, _ => { }, CancellationToken.None);
            Complete(result);
            Assert(!result.Result.Success && result.Result.Error.Contains("Lokale IRIS-Konfiguration") && File.Exists(Path.Combine(request.DatabaseProviderDirectory, "InterSystems.Data.Bindings.dll")),
                "the actual worker receives its extracted provider before safely rejecting a request with no database targets");
            var remote = new WorkerRequest { Action = "publish-msi-packages", LocalExecution = false, Host = "fixture.invalid" };
            Task<WorkerResult> remoteResult = runner.RunAsync(remote, _ => { }, CancellationToken.None);
            Complete(remoteResult);
            Assert(!remoteResult.Result.Success && remoteResult.Result.Error.Contains("ausschließlich lokal") && remote.DatabaseProviderDirectory.Length == 0,
                "remote publication is rejected without extracting or forwarding a provider path");
            runner.Dispose();
            Assert(!Directory.Exists(request.DatabaseProviderDirectory), "worker disposal cleans up the bundled provider payload");
        }
        finally
        {
            Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "bundled provider fixture cleanup stays scoped to test artifacts");
            Directory.Delete(root, recursive: true);
        }
    }
}
