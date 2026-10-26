namespace NovaTouchUpdater;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (!args.Contains(AppSelfUpdate.SkipRenameArgument, StringComparer.Ordinal))
        {
            try
            {
                if (AppSelfUpdate.NormalizeExecutableNameAsync(CancellationToken.None).GetAwaiter().GetResult()) return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Die EXE konnte nicht als NovaTouchUpdater.exe vorbereitet werden. " +
                    "Die Anwendung startet unter ihrem bisherigen Dateinamen.\n\n" + ex.Message,
                    "NovaTouch Updater", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        Application.Run(new MainForm());
    }
}
