using System.ComponentModel;
using System.Diagnostics;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void AdministratorTests()
    {
        var start = AppElevation.CreateStartInfo();
        Assert(start.FileName == Environment.ProcessPath && start.UseShellExecute && start.Verb == "runas" &&
            start.WorkingDirectory == AppContext.BaseDirectory &&
            start.ArgumentList.TakeLast(Environment.GetCommandLineArgs().Length - 1).SequenceEqual(Environment.GetCommandLineArgs().Skip(1)),
            "administrator restart uses the current executable, arguments and application folder with Windows UAC");
        Assert(!AppElevation.Restart(_ => throw new Win32Exception(1223)), "cancelling Windows UAC reports an unsuccessful restart");
        bool failed = false;
        try { AppElevation.Restart(_ => throw new Win32Exception(5)); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { failed = true; }
        Assert(failed, "administrator launch failures remain available for the UI error message");
        Assert(AppElevation.Restart(_ => Process.GetCurrentProcess()), "a successful launch allows the original window to close");

        var form = new MainForm(preview: true);
        try
        {
            Populate(form);
            var button = Field<Button>(form, "btnAdministrator");
            SetField(form, "isLocalAdministrator", new Func<bool>(() => false));
            Call(form, "UpdateButtons");
            // The preview form stays hidden; a parent hidden by Windows makes Visible false.
            Assert(button.Parent!.Controls.Contains(button) && button.Enabled && button.Image is not null,
                "the header offers an enabled Windows shield action without administrator rights");
            int launches = 0;
            SetField(form, "restartAsAdministrator", new Func<bool>(() =>
            {
                launches++;
                Assert(!button.Enabled, "the administrator action is locked while Windows handles the launch");
                Call(form, "RestartAsAdministrator");
                return false;
            }));
            foreach (string state in new[] { "busy", "selfUpdating", "loadingPackages", "preparingDatabaseUpload" })
            {
                SetField(form, state, true);
                Call(form, "UpdateButtons");
                Assert(!button.Enabled, "administrator restart is disabled during " + state);
                Call(form, "RestartAsAdministrator");
                SetField(form, state, false);
            }
            Assert(launches == 0, "active work never starts a second updater");
            Call(form, "UpdateButtons");
            Call(form, "RestartAsAdministrator");
            Assert(launches == 1 && !form.IsDisposed && button.Enabled,
                "cancelled elevation leaves the existing updater usable and blocks duplicate launches");
            SetField(form, "isLocalAdministrator", new Func<bool>(() => true));
            Call(form, "UpdateButtons");
            Call(form, "RestartAsAdministrator");
            Assert(launches == 1, "an elevated updater never requests elevation again");
            SetField(form, "isLocalAdministrator", new Func<bool>(() => false));
            SetField(form, "restartAsAdministrator", new Func<bool>(() => true));
            Call(form, "RestartAsAdministrator");
            Assert(form.IsDisposed, "the original updater closes after the administrator launch succeeds");
        }
        finally { if (!form.IsDisposed) form.Dispose(); }
        FeedbackHeaderTests();
    }
}
