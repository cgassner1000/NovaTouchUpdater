using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaTouchUpdater;

internal sealed record HostConsoleOptions(string Host, bool LocalExecution, string Username = "", string Password = "", bool UseSsl = false, int HttpsPort = 5986)
{
    public override string ToString() => Host + (LocalExecution ? " · lokal" : " · WinRM");
}

internal static class HostConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    internal static ProcessStartInfo LocalStartInfo() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
    {
        Arguments = "/d", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal,
        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
    };

    internal static ProcessStartInfo RemoteStartInfo(string pipeName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pipeName, "^NovaTouchUpdater-Console-[a-f0-9]{32}$"))
            throw new ArgumentException("Ungültige Konsolen-Pipe.", nameof(pipeName));
        using var resource = typeof(HostConsole).Assembly.GetManifestResourceStream("NovaTouchUpdater.HostConsole.ps1")
            ?? throw new InvalidOperationException("Das Konsolenskript fehlt.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        string script = reader.ReadToEnd().Replace("__PIPE_NAME__", pipeName, StringComparison.Ordinal);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        // Keep the console's stdin/stdout attached. Only the startup payload uses
        // the user-restricted pipe; no credentials go into arguments or files.
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task<bool> OpenAsync(HostConsoleOptions options, CancellationToken ct = default, Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        ct.ThrowIfCancellationRequested();
        startProcess ??= Process.Start;
        if (options.LocalExecution)
        {
            if (!options.Host.Equals("LOCALHOST", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Lokale Konsole benötigt das Ziel LOCALHOST.");
            try { using var local = startProcess(LocalStartInfo()) ?? throw new InvalidOperationException("Die Eingabeaufforderung konnte nicht gestartet werden."); return true; }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // UAC cancelled.
        }
        if (string.IsNullOrWhiteSpace(options.Host)) throw new ArgumentException("Remote-Host fehlt.");
        if (options.UseSsl && options.HttpsPort is < 1 or > 65535) throw new ArgumentException("Ungültiger HTTPS-Port.");
        if (!string.IsNullOrWhiteSpace(options.Username) && string.IsNullOrEmpty(options.Password)) throw new ArgumentException("Passwort für den angegebenen Benutzer fehlt.");

        string pipeName = "NovaTouchUpdater-Console-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = startProcess(RemoteStartInfo(pipeName)) ?? throw new InvalidOperationException("Die Remote-Konsole konnte nicht gestartet werden.");
        bool handedOff = false;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(options);
        try
        {
            Task connected = pipe.WaitForConnectionAsync(timeout.Token);
            Task exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited) == exited && !connected.IsCompletedSuccessfully)
            {
                await exited;
                throw new IOException("Die Remote-Konsole wurde vor der Anmeldung geschlossen.");
            }
            await connected;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (clientId != process.Id) throw new IOException("Unerwarteter Prozess an der Konsolen-Pipe.");
            await pipe.WriteAsync(payload, timeout.Token);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            if (await reader.ReadLineAsync(timeout.Token) != "READY") throw new IOException("Die Konsole hat die Verbindungsdaten nicht bestätigt.");
            handedOff = true;
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Die Remote-Konsole hat die Anmeldedaten nicht rechtzeitig übernommen."); }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            timeout.Cancel();
            // Only stop our own failed bootstrap process, never a connected shell.
            if (!handedOff)
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }
    }
}
