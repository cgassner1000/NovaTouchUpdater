using System.Xml;

namespace NovaTouchUpdater;

// Local imports use the selected originals. Keep byte-for-byte snapshots for
// remote uploads until every host finishes; never rewrite the XML export.
internal sealed class ServicePackSelection : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovaTouchUpdater", "servicepacks-" + Guid.NewGuid().ToString("N"));
    internal ServicePackFile[] Files { get; private set; } = [];
    internal ServicePackFile[] LocalFiles { get; private set; } = [];

    internal static async Task<ServicePackSelection> CreateAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var selection = new ServicePackSelection();
        Directory.CreateDirectory(selection.directory);
        try
        {
            var files = new List<ServicePackFile>();
            var localFiles = new List<ServicePackFile>();
            foreach (string path in paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!System.IO.Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Nur XML-Servicepacks auswählen.");
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                string[] items;
                try { items = ReadItems(source, ct); }
                catch (InvalidDataException ex) { throw new InvalidDataException(System.IO.Path.GetFileName(path) + ": " + ex.Message, ex); }
                source.Position = 0;
                string snapshot = System.IO.Path.Combine(selection.directory, files.Count + ".xml");
                await using (var target = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                string hash = await WorkerRunner.Sha256Async(snapshot, ct).ConfigureAwait(false);
                files.Add(new ServicePackFile { Path = snapshot, Name = System.IO.Path.GetFileName(path), Items = items,
                    Sha256 = hash });
                localFiles.Add(new ServicePackFile { Path = path, Name = System.IO.Path.GetFileName(path), Items = items, Sha256 = hash });
            }
            if (files.Count == 0) throw new InvalidDataException("Mindestens ein XML-Servicepack auswählen.");
            selection.Files = files.ToArray();
            selection.LocalFiles = localFiles.ToArray();
            return selection;
        }
        catch { selection.Dispose(); throw; }
    }

    private static XmlReaderSettings ReaderSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false };

    private static string[] ReadItems(Stream source, CancellationToken ct)
    {
        var items = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(source, ReaderSettings());
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "Export")
            throw new InvalidDataException("Die XML-Datei ist kein InterSystems-Export.");
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1) continue;
            if (reader.LocalName is not ("Class" or "Routine" or "Global"))
                throw new InvalidDataException("Servicepacks dürfen ausschließlich Klassen, Routinen und Globals enthalten: " + reader.LocalName);
            bool isGlobal = reader.LocalName == "Global";
            string attributeName = reader.GetAttribute("name") ?? "";
            string[] objectNames = isGlobal && attributeName.Length == 0 ? ReadGlobalNames(reader, ct) : [attributeName];
            foreach (string objectName in objectNames)
            {
                string name = isGlobal && objectName.StartsWith('^') ? objectName[1..] : objectName;
                if (name.Length == 0 || name.Any(character => !(char.IsLetterOrDigit(character) || character is '%' or '.' or '_')))
                    throw new InvalidDataException("Ungültiger Klassen-/Routinen-/Globalname im Servicepack: " + (name.Length == 0 ? "<fehlt>" : name));
                if (isGlobal) name = "^" + name;
                else if (reader.LocalName == "Class") name += name.EndsWith(".cls", StringComparison.OrdinalIgnoreCase) ? "" : ".cls";
                else if (!new[] { ".mac", ".int", ".inc", ".bas" }.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    string type = (reader.GetAttribute("type") ?? "MAC").ToLowerInvariant();
                    if (type is not ("mac" or "int" or "inc" or "bas")) throw new InvalidDataException("Unbekannter Routinentyp: " + type);
                    name += "." + type;
                }
                if (!names.Add(isGlobal ? name : name.ToUpperInvariant())) throw new InvalidDataException("Objekt mehrfach in derselben XML-Datei: " + name);
                items.Add(name);
            }
        }
        if (items.Count == 0) throw new InvalidDataException("Die XML-Datei enthält keine Klassen, Routinen oder Globals.");
        return items.ToArray();
    }

    private static string[] ReadGlobalNames(XmlReader reader, CancellationToken ct)
    {
        // IRIS exports globals as <Global><Node><Sub>^Name</Sub>...</Node></Global>.
        // Only root Sub elements identify globals; nested Subs are data subscripts.
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var global = reader.ReadSubtree();
        while (global.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (global.NodeType != XmlNodeType.Element || global.Depth != 1 || global.LocalName != "Node") continue;
            using var node = global.ReadSubtree();
            while (node.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (node.NodeType != XmlNodeType.Element || node.Depth != 1 || node.LocalName != "Sub") continue;
                names.Add(node.ReadElementContentAsString());
                break;
            }
        }
        if (names.Count == 0) throw new InvalidDataException("Global-Export enthält keinen Globalnamen unter Node/Sub.");
        return names.ToArray();
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
