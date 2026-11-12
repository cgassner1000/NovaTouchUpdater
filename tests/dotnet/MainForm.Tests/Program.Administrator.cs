using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void AdministratorTests()
    {
        RenderAdministratorShield();
        ProcessElevationTests();
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
        using var canvas = new Panel { Size = form.ClientSize };
        try
        {
            Populate(form);
            Control content = form.Controls[0];
            canvas.Controls.Add(content);
            canvas.CreateControl();
            var button = Field<Button>(form, "btnAdministrator");
            SetField(form, "isProcessElevated", new Func<bool>(() => false));
            Call(form, "UpdateButtons");
            Assert(button.Visible && button.Enabled && button.Image is not null,
                "the header shows an enabled Windows shield action whenever the process is not elevated");
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
            SetField(form, "isProcessElevated", new Func<bool>(() => true));
            Call(form, "UpdateButtons");
            Assert(!button.Visible, "only actual process elevation hides the header action");
            Call(form, "RestartAsAdministrator");
            Assert(launches == 1, "an elevated updater never requests elevation again");
            SetField(form, "isProcessElevated", new Func<bool>(() => false));
            Call(form, "UpdateButtons");
            Assert(button.Visible, "the header offers elevation again when the process is not elevated");
            form.Controls.Add(content);
            SetField(form, "restartAsAdministrator", new Func<bool>(() => true));
            Call(form, "RestartAsAdministrator");
            Assert(form.IsDisposed, "the original updater closes after the administrator launch succeeds");
        }
        finally { if (!form.IsDisposed) form.Dispose(); }
        FeedbackHeaderTests();
    }

    private static void RenderAdministratorShield()
    {
        using var canvas = new Panel { Size = new Size(640, 300), BackColor = UiTheme.Background };
        int top = 16;
        foreach (float scale in new[] { 1F, 1.5F, 2F })
        {
            canvas.Controls.Add(new Label { Text = $"{scale:P0}", Location = new Point(16, top + 8), AutoSize = true, Font = UiTheme.BodyFont() });
            var button = new AdministratorButton { AutoSize = false, Location = new Point(92, top),
                Font = new Font("Segoe UI", 8.5F * scale),
                Padding = new Padding((int)(6 * scale), (int)(2 * scale), (int)(6 * scale), (int)(2 * scale)) };
            Image? previous = button.Image;
            button.Image = AdministratorButton.CreateShield((int)(24 * scale));
            previous?.Dispose();
            button.Size = button.GetPreferredSize(Size.Empty);
            canvas.Controls.Add(button);
            top += button.Height + 18;
        }
        canvas.CreateControl();
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "administrator-shield.png"));
    }

    private static void ProcessElevationTests()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert(AppElevation.IsProcessElevated() == AppElevation.IsTokenElevated(identity.AccessToken),
            "the running process reports the elevation state of its Windows token");

        const int tokenElevationType = 18, tokenLinkedToken = 19;
        if (!ReadElevationType(identity.AccessToken, tokenElevationType, out int type, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (type == 1)
        {
            Console.WriteLine("SKIP split administrator token: this Windows account has no linked UAC token");
            return;
        }
        if (!ReadLinkedToken(identity.AccessToken, tokenLinkedToken, out IntPtr linked, IntPtr.Size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var linkedToken = new SafeAccessTokenHandle(linked);
        SafeAccessTokenHandle standardToken = type == 2 ? linkedToken : identity.AccessToken;
        SafeAccessTokenHandle elevatedToken = type == 2 ? identity.AccessToken : linkedToken;
        using var standardIdentity = new WindowsIdentity(standardToken.DangerousGetHandle());
        using var elevatedIdentity = new WindowsIdentity(elevatedToken.DangerousGetHandle());
        Assert(standardIdentity.User == elevatedIdentity.User &&
            new WindowsPrincipal(elevatedIdentity).IsInRole(WindowsBuiltInRole.Administrator) &&
            !AppElevation.IsTokenElevated(standardToken),
            "a Windows administrator's standard UAC token is correctly detected as NOT elevated");
        Assert(AppElevation.IsTokenElevated(elevatedToken),
            "the same administrator's elevated UAC token is detected as elevated");
        bool threadResult = WindowsIdentity.RunImpersonated(standardToken, AppElevation.IsProcessElevated);
        Assert(threadResult == AppElevation.IsProcessElevated(),
            "thread impersonation does not change the process elevation used by the header and its workers");
    }

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadElevationType(SafeAccessTokenHandle token, int informationClass,
        out int information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadLinkedToken(SafeAccessTokenHandle token, int informationClass,
        out IntPtr information, int informationLength, out int returnLength);
}
