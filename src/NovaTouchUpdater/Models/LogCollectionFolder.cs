using System.Globalization;
using System.Text;

namespace NovaTouchUpdater;

internal static class LogCollectionFolder
{
    internal static string Create(string executableDirectory, string host, DateTimeOffset collectedAt)
    {
        string root = Path.Combine(executableDirectory, "logs");
        Directory.CreateDirectory(root);
        char[] invalid = Path.GetInvalidFileNameChars();
        string hostName = new(host.Trim().Select(character => character == ':' ? '-' : invalid.Contains(character) ? '_' : character).Take(200).ToArray());
        hostName = hostName.TrimEnd(' ', '.');
        if (hostName.Length == 0) throw new ArgumentException("Ein Hostname für die Logsammlung ist erforderlich.", nameof(host));
        string timestamp = "NovaTouch LOG " + hostName + " " + collectedAt.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
        for (int attempt = 0; ; attempt++)
        {
            string directory = Path.Combine(root, timestamp + (attempt == 0 ? "" : " (" + (attempt + 1) + ")"));
            if (Directory.Exists(directory) || File.Exists(directory) || File.Exists(directory + ".zip") || File.Exists(directory + ".zip.partial")) continue;
            Directory.CreateDirectory(directory);
            FileStream file;
            try { file = new FileStream(Path.Combine(directory, "Sammlung.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
            catch (IOException) when (File.Exists(Path.Combine(directory, "Sammlung.txt"))) { continue; }
            using (file)
            {
                using var writer = new StreamWriter(file, new UTF8Encoding(true));
                writer.WriteLine("Host: " + host);
                writer.WriteLine("Gesammelt am: " + collectedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
                writer.WriteLine("Sammlung gestartet. Bis zum Abschluss können Dateien unvollständig sein.");
                return directory;
            }
        }
    }
}
