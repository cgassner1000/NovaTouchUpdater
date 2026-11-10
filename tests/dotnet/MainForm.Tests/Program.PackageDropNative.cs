using System.Runtime.InteropServices;
using System.Text;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void NativePackageDropTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "native-drop-Grüße-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string payment = Path.Combine(root, "nt.payment_4.0.0.zip"), fiscal = Path.Combine(root, "nt.fiscal_4.0.0.zip");
        File.WriteAllText(payment, "fixture, never installed"); File.WriteAllText(fiscal, "fixture, never installed");
        var worker = new FakeRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        Populate(form);
        var drop = All(form).Single(control => control.Name == "packageDropArea");
        var errors = new List<string>();
        using var native = AppElevation.IsProcessElevated() ? null :
            new ShellFileDropTarget(drop, paths => Call(form, "AcceptPackageDrop", drop, paths), errors.Add);
        _ = drop.Handle;
        Assert((DropWindowStyle(drop.Handle) & 0x10) != 0 && errors.Count == 0,
            "a real package drop HWND is registered for Explorer file messages");
        if (AppElevation.IsProcessElevated()) Assert(!drop.AllowDrop,
            "the elevated process uses shell file drops instead of the blocked OLE target");
        NativeDrop(drop, payment, fiscal);
        Complete(Field<Task>(form, "packageDropTask"));
        Assert(Field<string>(form, "paymentZip") == payment && Field<string>(form, "fiscalZip") == fiscal,
            "real WM_DROPFILES messages import every selected file through the package UI pipeline");
        typeof(Control).GetMethod("RecreateHandle", Private)!.Invoke(drop, []);
        Assert((DropWindowStyle(drop.Handle) & 0x10) != 0,
            "Explorer file registration survives handle recreation");
        string replacement = Path.Combine(root, "nt.payment_5.0.0.zip"); File.WriteAllText(replacement, "fixture");
        SetField(form, "busy", true); Call(form, "UpdateButtons");
        NativeDrop(drop, replacement); Complete(Field<Task>(form, "packageDropTask"));
        Assert(Field<string>(form, "paymentZip") == payment,
            "native shell drops cannot replace packages during active work");
        SetField(form, "busy", false); Call(form, "UpdateButtons");
        NativeDrop(drop, replacement); Complete(Field<Task>(form, "packageDropTask"));
        Assert(Field<string>(form, "paymentZip") == replacement && worker.Requests.IsEmpty && File.Exists(payment),
            "native file drops resume after active work without moving files or starting hosts");
        if (AppElevation.IsProcessElevated())
        {
            var row = Field<Button>(form, "btnFiscal");
            NativeDrop(row, payment, fiscal); Complete(Field<Task>(form, "packageDropTask"));
            Assert(Field<string>(form, "paymentZip") == replacement && Field<string>(form, "fiscalZip") == fiscal,
                "native drops on individual selectors still affect only the matching product");
        }
    }

    private static void NativeDrop(Control surface, params string[] paths)
    {
        byte[] names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        IntPtr drop = DropGlobalAlloc(0x42, (UIntPtr)(20 + names.Length));
        if (drop == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            IntPtr data = DropGlobalLock(drop);
            if (data == IntPtr.Zero) throw new InvalidOperationException("GlobalLock failed");
            try { Marshal.WriteInt32(data, 0, 20); Marshal.WriteInt32(data, 16, 1); Marshal.Copy(names, 0, data + 20, names.Length); }
            finally { DropGlobalUnlock(drop); }
            DropSendMessage(surface.Handle, ShellFileDropTarget.DropFilesMessage, drop, IntPtr.Zero);
            Assert(DropGlobalFlags(drop) == 0x8000, "the native drop handler releases the shell's HDROP memory");
        }
        finally { if (DropGlobalFlags(drop) != 0x8000) DropGlobalFree(drop); }
    }

    private static long DropWindowStyle(IntPtr window) => IntPtr.Size == 8 ? DropGetWindowLongPtr(window, -20).ToInt64() : DropGetWindowLong(window, -20);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr DropGetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int DropGetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr DropSendMessage(IntPtr window, int message, IntPtr value, IntPtr other);
    [DllImport("kernel32.dll", EntryPoint = "GlobalAlloc")] private static extern IntPtr DropGlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll", EntryPoint = "GlobalLock")] private static extern IntPtr DropGlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalUnlock")] private static extern bool DropGlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalFlags")] private static extern uint DropGlobalFlags(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalFree")] private static extern IntPtr DropGlobalFree(IntPtr memory);
}
