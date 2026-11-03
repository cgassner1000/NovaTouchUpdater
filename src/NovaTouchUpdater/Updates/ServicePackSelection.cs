using System.Text;
using System.Xml;

namespace NovaTouchUpdater;

// Keep immutable UTF-8 snapshots until every host finishes. The source files are
// opened read-only and never changed; XML encodings are normalized by XmlReader.
internal sealed class ServicePackSelection : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovaTouchUpdater", "servicepacks-" + Guid.NewGuid().ToString("N"));
    internal ServicePackFile[] Files { get; private set; } = [];

    internal static async Task<ServicePackSelection> CreateAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var selection = new ServicePackSelection();
        Directory.CreateDirectory(selection.directory);
        try
        {
            var files = new List<ServicePackFile>();
            foreach (string path in paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!System.IO.Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Nur XML-Servicepacks auswählen.");
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                string[] items = ReadItems(source, ct);
                source.Position = 0;
                string snapshot = System.IO.Path.Combine(selection.directory, files.Count + ".xml");
                using (var reader = XmlReader.Create(source, ReaderSettings()))
                using (var writer = XmlWriter.Create(snapshot, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                {
                    reader.MoveToContent();
                    writer.WriteStartDocument();
                    writer.WriteNode(reader, true);
                    writer.WriteEndDocument();
                }
                files.Add(new ServicePackFile { Path = snapshot, Name = System.IO.Path.GetFileName(path), Items = items,
                    Sha256 = await WorkerRunner.Sha256Async(snapshot, ct).ConfigureAwait(false) });
            }
            if (files.Count == 0) throw new InvalidDataException("Mindestens ein XML-Servicepack auswählen.");
            selection.Files = files.ToArray();
            return selection;
        }
        catch { selection.Dispose(); throw; }
    }

    private static XmlReaderSettings ReaderSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false };

    private static string[] ReadItems(Stream source, CancellationToken ct)
    {
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(source, ReaderSettings());
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "Export")
            throw new InvalidDataException("Die XML-Datei ist kein InterSystems-Export.");
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1) continue;
            if (reader.LocalName is not ("Class" or "Routine"))
                throw new InvalidDataException("Servicepacks dürfen ausschließlich Klassen und Routinen enthalten: " + reader.LocalName);
            string name = reader.GetAttribute("name") ?? "";
            if (name.Length == 0 || name.Any(character => !(char.IsLetterOrDigit(character) || character is '%' or '.' or '_')))
                throw new InvalidDataException("Ungültiger Klassen-/Routinenname im Servicepack: " + name);
            if (reader.LocalName == "Class") name += name.EndsWith(".cls", StringComparison.OrdinalIgnoreCase) ? "" : ".cls";
            else if (!new[] { ".mac", ".int", ".inc", ".bas" }.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                string type = (reader.GetAttribute("type") ?? "MAC").ToLowerInvariant();
                if (type is not ("mac" or "int" or "inc" or "bas")) throw new InvalidDataException("Unbekannter Routinentyp: " + type);
                name += "." + type;
            }
            if (!items.Add(name)) throw new InvalidDataException("Objekt mehrfach in derselben XML-Datei: " + name);
        }
        if (items.Count == 0) throw new InvalidDataException("Die XML-Datei enthält keine Klassen oder Routinen.");
        return items.ToArray();
    }

    public void Dispose()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NovaTouchUpdater"));
        string full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("servicepacks-", StringComparison.Ordinal))
            throw new InvalidOperationException("Ungültiger temporärer Servicepack-Pfad.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
