using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NovaTouchUpdater;

internal static class ExecutableRenameTests
{
    internal const string MarkerName = "rename-test.marker";

    // Only the published TEST executable enters this branch. It never constructs
    // MainForm, reads real credentials, connects to hosts or downloads anything.
    internal static async Task<int> RunFixtureAsync(string[] args)
    {
        string directory = AppContext.BaseDirectory;
        try
        {
            File.AppendAllLines(Path.Combine(directory, "launches.txt"), [Environment.ProcessPath!]);
            if (!args.Contains(AppSelfUpdate.SkipRenameArgument, StringComparer.Ordinal) &&
                await AppSelfUpdate.NormalizeExecutableNameAsync(CancellationToken.None)) return 0;
            string result = Path.Combine(directory, "completed.json");
            await File.WriteAllTextAsync(result + ".tmp", JsonSerializer.Serialize(new { Path = Environment.ProcessPath, ProcessId = Environment.ProcessId }));
            File.Move(result + ".tmp", result);
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), ex.ToString());
            return 1;
        }
    }

    internal static async Task RunAsync(string fixtureExecutable)
    {
        string workspace = TestWorkspace.Root;
        string artifacts = TestWorkspace.Artifacts + Path.DirectorySeparatorChar;
        string source = Path.GetFullPath(fixtureExecutable);
        if (!File.Exists(Path.Combine(workspace, "NovaTouchUpdater.slnx")) ||
            !source.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(source) != "AppRelease.Tests.exe")
            throw new InvalidOperationException("The published test executable must be inside workspace artifacts/test-results.");
        string root = Path.Combine(artifacts, "rename-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] expectedHash;
        using (var input = File.OpenRead(source)) expectedHash = SHA256.HashData(input);
        try
        {
            foreach (var variant in new[] { "versioned name", "existing target", "already canonical" })
            {
                string directory = Path.Combine(root, variant + " with spaces and ü");
                Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, "NovaTouchUpdater.exe");
                string original = variant == "already canonical" ? target : Path.Combine(directory, "NovaTouchUpdater-v9.9.9.exe");
                File.Copy(source, original);
                if (variant == "existing target") await File.WriteAllTextAsync(target, "old fixed-name test fixture - never executed");
                await File.WriteAllTextAsync(Path.Combine(directory, MarkerName), "synthetic fixture only");
                string config = "{\"Username\":\"test-only\",\"EncryptedPassword\":\"\",\"Hosts\":[\"TEST-ONLY\"]}";
                await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), config);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                using var process = Process.Start(new ProcessStartInfo(original) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory })
                    ?? throw new InvalidOperationException("Could not start the rename fixture.");
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) throw new InvalidOperationException("Fixture failed: " + await File.ReadAllTextAsync(Path.Combine(directory, "error.txt")));
                string result = Path.Combine(directory, "completed.json");
                while (!File.Exists(result) || Directory.EnumerateDirectories(directory, ".NovaTouchUpdater-update-*").Any())
                {
                    string error = Path.Combine(directory, "error.txt");
                    if (File.Exists(error)) throw new InvalidOperationException(await File.ReadAllTextAsync(error));
                    await Task.Delay(100, timeout.Token);
                }
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result));
                int restartedId = document.RootElement.GetProperty("ProcessId").GetInt32();
                try
                {
                    using var restarted = Process.GetProcessById(restartedId);
                    await restarted.WaitForExitAsync(timeout.Token);
                }
                catch (ArgumentException) { /* fixture has already exited */ }
                Assert(document.RootElement.GetProperty("Path").GetString() == target, variant + ": real restart runs NovaTouchUpdater.exe");
                Assert(original == target || !File.Exists(original), variant + ": old versioned executable is removed");
                using (var input = File.OpenRead(target)) Assert(SHA256.HashData(input).SequenceEqual(expectedHash), variant + ": renamed EXE is byte-for-byte identical");
                Assert(await File.ReadAllTextAsync(Path.Combine(directory, "config.json")) == config, variant + ": saved config is unchanged");
                string[] launches = await File.ReadAllLinesAsync(Path.Combine(directory, "launches.txt"));
                Assert(launches.SequenceEqual(original == target ? new[] { target } : new[] { original, target }),
                    variant + ": startup renames once and does not loop");
            }
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("rename-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid fixture cleanup path.");
            // Allow the helper a moment to release its working-directory handle.
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Delete(resolved, true); break; }
                catch (IOException) when (attempt < 20) { await Task.Delay(100); }
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
