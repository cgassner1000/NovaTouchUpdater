namespace NovaTouchUpdater;

// The Framework DLLs are restored by NuGet at build time and embedded in the
// single-file application. Only the local database worker needs them on disk.
internal sealed class BundledIrisProvider : IDisposable
{
    private readonly string root;
    private static readonly string[] Files = ["InterSystems.Data.IRISClient.dll", "InterSystems.Data.Bindings.dll", "IRISClient.LICENSE.txt", "Bindings.LICENSE.txt"];
    private readonly object gate = new();
    private string? directory;
    private bool disposed;

    internal BundledIrisProvider(string? temporaryRoot = null) =>
        root = Path.GetFullPath(temporaryRoot ?? Path.Combine(Path.GetTempPath(), "NovaTouchUpdater"));

    internal string EnsureExtracted()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (directory is not null) return directory;
            string target = Path.Combine(root, "IrisProvider-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            try
            {
                foreach (string file in Files)
                {
                    using Stream resource = typeof(BundledIrisProvider).Assembly.GetManifestResourceStream("NovaTouchUpdater.IrisProvider." + file)
                        ?? throw new InvalidOperationException("Die eingebettete IRIS-Abhängigkeit fehlt: " + file);
                    using var output = new FileStream(Path.Combine(target, file), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    resource.CopyTo(output);
                }
                directory = target;
                return target;
            }
            catch { DeleteOwnedDirectory(target); throw; }
        }
    }

    private void DeleteOwnedDirectory(string path)
    {
        string resolved = Path.GetFullPath(path);
        string name = Path.GetFileName(resolved);
        if (!string.Equals(Path.GetDirectoryName(resolved), root, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("IrisProvider-", StringComparison.Ordinal) || !Guid.TryParseExact(name["IrisProvider-".Length..], "N", out _))
            throw new InvalidOperationException("Ungültiger temporärer IRIS-Providerpfad.");
        try { Directory.Delete(resolved, recursive: true); }
        catch (IOException) { /* The CLR may retain a DLL lock until the worker exits. */ }
        catch (UnauthorizedAccessException) { /* Leave only this instance's private directory. */ }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (directory is not null) DeleteOwnedDirectory(directory);
            directory = null;
        }
    }
}
