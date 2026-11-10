using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NovaTouchUpdater;

// Explorer cannot use OLE drag and drop into an elevated process. Register the
// shell's file-only protocol on these controls instead, with per-window filters.
internal sealed class ShellFileDropTarget : NativeWindow, IDisposable
{
    internal const int DropFilesMessage = 0x0233;
    private const uint CopyGlobalDataMessage = 0x0049;
    private readonly Control surface;
    private readonly Action<string[]> receiveFiles;
    private readonly Action<string> reportError;
    private bool enabled = true;

    internal ShellFileDropTarget(Control surface, Action<string[]> receiveFiles, Action<string> reportError)
    {
        this.surface = surface;
        this.receiveFiles = receiveFiles;
        this.reportError = reportError;
        surface.HandleCreated += HandleCreated;
        surface.HandleDestroyed += HandleDestroyed;
        surface.Disposed += SurfaceDisposed;
        if (surface.IsHandleCreated) Attach();
    }

    internal bool Enabled
    {
        get => enabled;
        set { enabled = value; if (Handle != IntPtr.Zero) DragAcceptFiles(Handle, enabled); }
    }

    private void HandleCreated(object? sender, EventArgs e) => Attach();
    private void HandleDestroyed(object? sender, EventArgs e) => Detach();
    private void SurfaceDisposed(object? sender, EventArgs e) => Dispose();

    private void Attach()
    {
        AssignHandle(surface.Handle);
        if (!ChangeWindowMessageFilterEx(Handle, DropFilesMessage, 1, IntPtr.Zero) ||
            !ChangeWindowMessageFilterEx(Handle, CopyGlobalDataMessage, 1, IntPtr.Zero))
        {
            reportError("Dateien aus dem Explorer können nicht angenommen werden: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return;
        }
        DragAcceptFiles(Handle, enabled);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != DropFilesMessage) { base.WndProc(ref m); return; }
        string[] files = [];
        try
        {
            if (enabled)
            {
                uint count = DragQueryFile(m.WParam, uint.MaxValue, null, 0);
                if (count > 10000) throw new InvalidDataException("Zu viele Dateien auf einmal abgelegt.");
                var paths = new List<string>();
                for (uint index = 0; index < count; index++)
                {
                    uint length = DragQueryFile(m.WParam, index, null, 0);
                    if (length == 0 || length > 32767) continue;
                    var path = new StringBuilder((int)length + 1);
                    if (DragQueryFile(m.WParam, index, path, (uint)path.Capacity) > 0) paths.Add(path.ToString());
                }
                files = paths.ToArray();
            }
        }
        catch (InvalidDataException ex) { reportError(ex.Message); }
        finally { DragFinish(m.WParam); }
        m.Result = IntPtr.Zero;
        if (files.Length > 0) receiveFiles(files);
    }

    private void Detach()
    {
        if (Handle == IntPtr.Zero) return;
        DragAcceptFiles(Handle, false);
        ChangeWindowMessageFilterEx(Handle, DropFilesMessage, 0, IntPtr.Zero);
        ChangeWindowMessageFilterEx(Handle, CopyGlobalDataMessage, 0, IntPtr.Zero);
        ReleaseHandle();
    }

    public void Dispose()
    {
        surface.HandleCreated -= HandleCreated;
        surface.HandleDestroyed -= HandleDestroyed;
        surface.Disposed -= SurfaceDisposed;
        Detach();
    }

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint capacity);
    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr drop);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeFilter);
}
