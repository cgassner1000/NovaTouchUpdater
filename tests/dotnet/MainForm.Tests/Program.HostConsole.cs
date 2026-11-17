using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void HostConsoleTests()
    {
        ProcessStartInfo local = HostConsole.LocalStartInfo();
        Assert(Path.GetFileName(local.FileName) == "cmd.exe" && local.UseShellExecute && local.Verb == "runas" && local.Arguments == "/d" &&
            local.WindowStyle == ProcessWindowStyle.Normal && !local.RedirectStandardInput && !local.RedirectStandardOutput,
            "local launches a real interactive cmd with Windows UAC and attached console streams");
        Complete(LocalCases());
        string pipe = "NovaTouchUpdater-Console-" + Guid.NewGuid().ToString("N");
        ProcessStartInfo remote = HostConsole.RemoteStartInfo(pipe);
        string script = Encoding.Unicode.GetString(Convert.FromBase64String(remote.ArgumentList.Last()));
        Assert(Path.GetFileName(remote.FileName) == "powershell.exe" && !remote.UseShellExecute && !remote.CreateNoWindow &&
            remote.ArgumentList.Contains("-NoExit") && !remote.ArgumentList.Contains("-NonInteractive") &&
            !remote.RedirectStandardInput && !remote.RedirectStandardOutput && !remote.RedirectStandardError &&
            script.Contains(pipe) && !script.Contains("__PIPE_NAME__"),
            "remote starts a visible persistent PowerShell console while preserving interactive stdin and output");
        Complete(PipeCase(false));
        Complete(PipeCase(true));
        Complete(CancelBootstrap());
        Complete(InteractiveRunspaceProbe(), TimeSpan.FromSeconds(30));

        async Task LocalCases()
        {
            bool opened = await HostConsole.OpenAsync(new("LOCALHOST", true), startProcess: _ => throw new Win32Exception(1223));
            Assert(!opened, "cancelled local UAC leaves the updater running without an error");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool invoked = false;
            try { await HostConsole.OpenAsync(new("LOCALHOST", true), cancelled.Token, _ => { invoked = true; return null; }); }
            catch (OperationCanceledException) { }
            Assert(!invoked, "a cancelled request never launches a console");
        }

        async Task PipeCase(bool reject)
        {
            var options = new HostConsoleOptions("literal.invalid'; throw 'data only", false, "SYNTHETIC\\admin", "Synthetic-pipe-only-secret", true, 443);
            Process? observer = null;
            try
            {
                bool failed = false;
                try
                {
                    bool result = await HostConsole.OpenAsync(options, startProcess: start =>
                    {
                        Assert(!start.ArgumentList.Any(arg => arg.Contains(options.Username) || arg.Contains(options.Password)) &&
                            !options.ToString().Contains(options.Password), "credentials never appear in process arguments or option diagnostics");
                        string bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList.Last()));
                        // Run the production pipe reader in an isolated hidden process.
                        // Replace only the later network connection with an explicit exit.
                        string validation = "$request = $line | ConvertFrom-Json; if ($request.Password -cne 'Synthetic-pipe-only-secret' -or $request.Username -cne 'SYNTHETIC\\admin' -or -not $request.UseSsl -or $request.HttpsPort -ne 443) { throw 'Payload mismatch' };";
                        if (reject) validation += " throw 'Synthetic receiver rejection';";
                        bootstrap = bootstrap.Replace("$request = $line | ConvertFrom-Json", validation, StringComparison.Ordinal)
                            .Replace("$session = ConnectHostConsole $request", "exit 0", StringComparison.Ordinal)
                            .Replace("$null = Read-Host 'Eingabetaste zum Schließen'", "$null = $null", StringComparison.Ordinal);
                        start.ArgumentList[start.ArgumentList.Count - 1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
                        start.CreateNoWindow = true; // Tests must never show a terminal or UAC.
                        Process child = Process.Start(start)!;
                        observer = Process.GetProcessById(child.Id); _ = observer.Handle;
                        return child;
                    });
                    Assert(result, "the real current-user-only pipe transfers and acknowledges the startup credentials");
                }
                catch (IOException) { failed = true; }
                Assert(failed == reject, "unacknowledged handoff cannot report a successfully opened console; rejected=" + reject);
                await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(reject ? observer.ExitCode != 0 : observer.ExitCode == 0, "the isolated console receiver exits without making any remote connection");
            }
            finally { observer?.Dispose(); }
        }

        async Task CancelBootstrap()
        {
            using var cancel = new CancellationTokenSource();
            Process? observer = null;
            try
            {
                bool cancelled = false;
                try
                {
                    await HostConsole.OpenAsync(new("synthetic.invalid", false), cancel.Token, start =>
                    {
                        start.ArgumentList.Clear(); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Seconds 30");
                        start.CreateNoWindow = true;
                        Process child = Process.Start(start)!;
                        observer = Process.GetProcessById(child.Id); _ = observer.Handle;
                        cancel.CancelAfter(200);
                        return child;
                    });
                }
                catch (OperationCanceledException) { cancelled = true; }
                await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(cancelled && observer.HasExited, "cancelled startup stops only its own unconnected console process");
            }
            finally { observer?.Dispose(); }
        }

        async Task InteractiveRunspaceProbe()
        {
            // A real remote runspace in an isolated child PowerShell process:
            // no WinRM listener, UAC, existing host or real credentials required.
            string marker = Path.Combine(TestWorkspace.Artifacts, "console-prompt-" + Guid.NewGuid().ToString("N") + ".txt");
            const string bootstrap = "function global:prompt { $global:rs.Dispose(); Write-Output 'LOCAL-FALLBACK-CLOSED'; exit }; $global:rs=[System.Management.Automation.Runspaces.RunspaceFactory]::CreateOutOfProcessRunspace($null); $global:rs.Open(); $Host.PushRunspace($global:rs)";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)) }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            try
            {
                Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
                await child.StandardInput.WriteLineAsync("$probe='NTU-PERSISTENT-PROBE'\r\n");
                await child.StandardInput.WriteLineAsync("[IO.File]::WriteAllText('" + marker.Replace("'", "''", StringComparison.Ordinal) + "',([string]$PID+';'+$probe))\r\n");
                await child.StandardInput.WriteLineAsync("exit\r\n");
                child.StandardInput.Close();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                string[] state = File.ReadAllText(marker).Split(';');
                Assert(child.ExitCode == 0 && int.Parse(state[0]) != child.Id && state[1] == "NTU-PERSISTENT-PROBE",
                    "real console input executes in the pushed process and preserves state across separate commands");
                _ = await output; _ = await error;
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                File.Delete(marker);
            }
        }
    }
}
