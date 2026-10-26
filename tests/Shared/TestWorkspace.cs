// Find the repository independently of project nesting, build configuration,
// runtime identifier or the directory from which a test was launched.
internal static class TestWorkspace
{
    internal static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "NovaTouchUpdater.slnx")))
                    return directory.FullName;
            throw new InvalidOperationException("Tests must run from inside the NovaTouchUpdater repository.");
        }
    }

    internal static string Artifacts
    {
        get
        {
            string directory = Path.Combine(Root, "artifacts", "test-results");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
