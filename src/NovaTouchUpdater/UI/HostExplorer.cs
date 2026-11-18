using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NovaTouchUpdater;

internal static class HostExplorer
{
    internal static ProcessStartInfo CreateStartInfo(HostState host)
    {
        string target;
        if (host.IsLocal)
        {
            target = Path.GetPathRoot(Environment.SystemDirectory)!;
        }
        else
        {
            string server;
            try { server = WinRmClientSetup.ValidateHost(host.Host); }
            catch (ArgumentException) { throw new ArgumentException("Für den Explorer ist ein gültiger Hostname oder eine IP-Adresse erforderlich."); }
            if (IPAddress.TryParse(server, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6)
                server = address.ToString().Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net";
            target = @"\\" + server + @"\C$";
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = true
        };
        start.ArgumentList.Add(target);
        return start;
    }
}
