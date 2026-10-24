using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NovaTouchUpdater;

// The helper is outside the running EXE. Only its own staged files and the exact
// executable paths are touched; config.json and all other neighbouring files stay put.
internal sealed class AppSelfUpdate : IDisposable
{
    private readonly string directory;
    private readonly string original;
    private readonly string target;
    private Process? helper;
    private bool handedOff;
    internal string DownloadPath => Path.Combine(directory, "new.exe");

    internal AppSelfUpdate()
    {
        original = Environment.ProcessPath ?? throw new InvalidOperationException("Der Pfad der laufenden EXE ist unbekannt.");
        target = Path.Combine(Path.GetDirectoryName(original)!, "NovaTouchUpdater.exe");
        // A framework-dependent apphost also needs matching DLLs. Releases are
        // single-file EXEs; never replace a development apphost on its own.
#pragma warning disable IL3000
        if (typeof(AppSelfUpdate).Assembly.Location.Length != 0)
#pragma warning restore IL3000
            throw new InvalidOperationException("Automatisches Aktualisieren ist nur mit der eigenständigen Release-EXE möglich. Bitte die EXE aus den GitHub-Releases verwenden.");
        directory = Path.Combine(Path.GetDirectoryName(target)!, ".NovaTouchUpdater-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    internal async Task StartHelperAsync(AppRelease release, CancellationToken cancellationToken)
    {
        string script = Path.Combine(directory, "SelfUpdate.ps1");
        using (var resource = typeof(AppSelfUpdate).Assembly.GetManifestResourceStream("NovaTouchUpdater.SelfUpdate.ps1")
            ?? throw new InvalidOperationException("Der Updatehelfer fehlt."))
        using (var reader = new StreamReader(resource))
            await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(cancellationToken), new UTF8Encoding(true), cancellationToken);

        string manifest = Path.Combine(directory, "update.json");
        using var current = Process.GetCurrentProcess();
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
        {
            ProcessId = current.Id,
            ProcessStartTimeTicks = current.StartTime.ToUniversalTime().Ticks,
            OriginalExecutablePath = original,
            ExecutablePath = target,
            StagedPath = DownloadPath,
            Sha256 = release.Sha256,
            Size = release.Size
        }), new UTF8Encoding(true), cancellationToken);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(target)! };
        // A launch from PowerShell 7 may otherwise pass incompatible module
        // paths into Windows PowerShell 5.1. Let it build its own defaults.
        start.Environment.Remove("PSModulePath");
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-ManifestPath", manifest })
            start.ArgumentList.Add(argument);
        helper = Process.Start(start) ?? throw new InvalidOperationException("Der Updatehelfer konnte nicht gestartet werden.");
        string ready = Path.Combine(directory, "ready");
        string errorLog = Path.Combine(directory, "update.log");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (helper.HasExited || File.Exists(errorLog) || watch.Elapsed > TimeSpan.FromSeconds(20))
                throw new InvalidOperationException("Der Updatehelfer ist nicht bereit. Die bisherige EXE bleibt erhalten.\n" +
                    (File.Exists(errorLog) ? await File.ReadAllTextAsync(errorLog, cancellationToken) : "Protokollpfad: " + errorLog));
            await Task.Delay(100, cancellationToken);
        }
    }

    internal void HandOff() => handedOff = true;

    public void Dispose()
    {
        if (!handedOff)
        {
            if (helper is { HasExited: false }) { helper.Kill(); helper.WaitForExit(5000); }
            // No recursive deletion: remove only the known files we created.
            foreach (string file in new[] { "new.exe", "SelfUpdate.ps1", "update.json", "ready" })
            {
                try { File.Delete(Path.Combine(directory, file)); } catch { }
            }
            try { Directory.Delete(directory); } catch { /* keep an error log if one exists */ }
        }
        helper?.Dispose();
    }
}
