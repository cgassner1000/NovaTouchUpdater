using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace NovaTouchUpdater;

internal static class AppElevation
{
    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

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
