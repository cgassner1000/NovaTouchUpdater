using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaTouchUpdater;

public sealed class WorkerRequest
{
    public string Action { get; set; } = "";
    public bool LocalExecution { get; set; }
    public MsiPackageRequest? MsiPackage { get; set; }
    public MsiPackageRequest[] MsiPackages { get; set; } = [];
    public IrisUploadTarget[] DatabaseTargets { get; set; } = [];
    public string Host { get; set; } = "";
    public string[] Hosts { get; set; } = [];
    public int Parallelism { get; set; } = 40;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseSsl { get; set; }
    public int HttpsPort { get; set; } = 5986;
    public bool ForceUpdate { get; set; }
    public bool RetryUpdate { get; set; }
    public string RemoteInstallPath { get; set; } = "C:\\install\\Novacom";
    public bool SkipZipTransfer { get; set; }
    public bool SkipLocalPackageCopy { get; set; }
    public bool CheckUpdateUpload { get; set; }
    public string ExpectedDirectorySnapshot { get; set; } = "";
    public string PaymentZip { get; set; } = "";
    public string FiscalZip { get; set; } = "";
    public string PaymentVersion { get; set; } = "";
    public string FiscalVersion { get; set; } = "";
    public string PaymentSha256 { get; set; } = "";
    public string FiscalSha256 { get; set; } = "";
    public string ServerSuiteZip { get; set; } = "";
    public string ServerSuiteVersion { get; set; } = "";
    public string ServerSuiteSha256 { get; set; } = "";
    public TransferFileRequest[] TransferFiles { get; set; } = [];
    public bool RestartPayment { get; set; }
    public bool RestartFiscal { get; set; }
    public bool StartServerSuite { get; set; }
}

public sealed class IrisUploadTarget
{
    public string Instance { get; set; } = "";
    public string Directory { get; set; } = "";
    public int Port { get; set; }
    public string[] Namespaces { get; set; } = [];
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Ssl { get; set; }
}

public sealed class TransferFileRequest
{
    public string LocalPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class MsiPackageRequest
{
    public string Kind { get; set; } = "";
    public string PayloadPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string RelativeMsiPath { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string UpgradeCode { get; set; } = "";
    public TransferFileRequest[] CompanionFiles { get; set; } = [];
}

public sealed class InstalledProgramInfo
{
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string InstallLocation { get; set; } = "";
}

public sealed class ServiceInfo
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public string StartType { get; set; } = "";
    public string ExePath { get; set; } = "";
    // Suite services use the resolved nt3-server-suite root, even with external Python.
    public string InstallDir { get; set; } = "";
    // For server suite this is the version from version.txt; FileVersion stays empty.
    public string ProductVersion { get; set; } = "";
    public string FileVersion { get; set; } = "";
}

public sealed class WorkerEvent
{
    public string Event { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Message { get; set; } = "";
    public string Host { get; set; } = "";
    public bool? Connected { get; set; }
    public int? ProgressPercent { get; set; }
}

public sealed class WorkerResult
{
    public string Event { get; set; } = "result";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Error { get; set; } = "";
    public string Tcp { get; set; } = "Nicht getestet";
    public string Session { get; set; } = "Nicht getestet";
    public string Command { get; set; } = "Nicht getestet";
    public bool Changed { get; set; }
    public bool Skipped { get; set; }
    public bool? DotNet48Installed { get; set; }
    public string PosVersion { get; set; } = "";
    public string NtcVersion { get; set; } = "";
    public bool RebootRequired { get; set; }
    public InstalledProgramInfo[] InstalledPrograms { get; set; } = [];
    public ServiceInfo[] Services { get; set; } = [];
    public bool UploadRequired { get; set; } = true;
    public UploadDirectoryInfo? UploadDirectory { get; set; }
}

public sealed class UploadDirectoryInfo
{
    public string Path { get; set; } = "";
    public int EntryCount { get; set; }
    public string Snapshot { get; set; } = "";
    public bool CanClean { get; set; }
    public string ProtectionReason { get; set; } = "";
    public bool Cleaned { get; set; }
}

internal interface IWorkerRunner : IDisposable
{
    Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct);
}

public sealed class WorkerRunner : IWorkerRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string scriptFile;

    public WorkerRunner(string resourceName = "NovaTouchUpdater.RemoteWorker.ps1")
    {
        string directory = Path.Combine(Path.GetTempPath(), "NovaTouchUpdater");
        Directory.CreateDirectory(directory);
        scriptFile = Path.Combine(directory, "Worker-" + Guid.NewGuid().ToString("N") + ".ps1");
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Die eingebettete Ressource {resourceName} ist nicht vorhanden.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        string script = reader.ReadToEnd();
        if (resourceName == "NovaTouchUpdater.RemoteWorker.ps1")
        {
            foreach (string helper in new[] { "UpdateSteps", "UploadDirectory", "MsiUpdates", "SoftwareDatabase" })
            {
                using Stream steps = Assembly.GetExecutingAssembly().GetManifestResourceStream($"NovaTouchUpdater.{helper}.ps1")
                    ?? throw new InvalidOperationException($"Die eingebetteten Schritte {helper} fehlen.");
                using var stepsReader = new StreamReader(steps, Encoding.UTF8);
                script = stepsReader.ReadToEnd() + Environment.NewLine + script;
            }
        }
        File.WriteAllText(scriptFile, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var bytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(bytes);
    }

    public async Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptFile);

        WorkerResult? outcome = null;
        var stderr = new StringBuilder();
        object gate = new();
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                string? type = doc.RootElement.TryGetProperty("Event", out var p) ? p.GetString() : null;
                if (type == "result")
                {
                    var result = JsonSerializer.Deserialize<WorkerResult>(e.Data, JsonOptions);
                    lock (gate) outcome = result;
                }
                else if (type == "progress")
                {
                    var ev = JsonSerializer.Deserialize<WorkerEvent>(e.Data, JsonOptions);
                    if (ev is not null) onProgress(ev);
                }
            }
            catch (JsonException)
            {
                onProgress(new WorkerEvent { Event = "progress", Phase = "PowerShell", Message = e.Data });
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (gate) { if (stderr.Length < 4000) stderr.AppendLine(e.Data); }
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows PowerShell konnte nicht gestartet werden.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            // WaitForExit() also waits for the asynchronous stdout/stderr reader callbacks.
            process.WaitForExit();
            lock (gate)
            {
                return outcome ?? new WorkerResult
                {
                    Success = false,
                    Message = "Keine gültige Rückmeldung aus PowerShell",
                    Error = stderr.Length == 0 ? $"PowerShell ExitCode {process.ExitCode}" : stderr.ToString().Trim()
                };
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        catch (Exception ex)
        {
            return new WorkerResult { Success = false, Message = "PowerShell-Prozessfehler", Error = ex.Message };
        }
    }

    public void Dispose()
    {
        try { if (File.Exists(scriptFile)) File.Delete(scriptFile); } catch { /* temporary file will remain */ }
    }
}
