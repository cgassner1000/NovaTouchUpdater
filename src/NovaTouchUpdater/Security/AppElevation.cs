using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NovaTouchUpdater;

internal static class AppElevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    // UAC elevation changes require a new process. Read this once on startup.
    private static readonly Lazy<bool> processElevation = new(ReadProcessElevation);

    internal static bool IsProcessElevated() => processElevation.Value;

    private static bool ReadProcessElevation()
    {
        // Check the token inherited by our worker processes, independently of
        // account group membership or a thread's impersonated identity.
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;
        using (token) return IsTokenElevated(token);
    }

    internal static bool IsTokenElevated(SafeAccessTokenHandle token) =>
        // If the status cannot be read, keep the elevation action available.
        GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int informationClass,
        out int information, int informationLength, out int returnLength);

    internal static ProcessStartInfo CreateStartInfo()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Der Pfad der laufenden EXE ist unbekannt.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        string[] arguments = Environment.GetCommandLineArgs();
        // Also support development launches through the dotnet host.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(arguments[0]);
        foreach (string argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
        return start;
    }

    internal static bool Restart() => Restart(Process.Start);

    internal static bool Restart(Func<ProcessStartInfo, Process?> startProcess)
    {
        try
        {
            using var process = startProcess(CreateStartInfo())
                ?? throw new InvalidOperationException("Der Updater konnte nicht als Administrator gestartet werden.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Cancelling UAC leaves the current application open.
            return false;
        }
    }
}
