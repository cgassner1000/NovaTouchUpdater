using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using NovaTouchUpdater;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static void Run()
    {
        // Validate the ABI from the documented Windows SDK, not a managed
        // round-trip that could repeat the same incorrectly declared layout.
        var scopes = ActiveDirectoryComputerPicker.CreateScopes();
        Assert(Marshal.SizeOf<ActiveDirectoryComputerPicker.DsOpUplevelFilterFlags>() == 12 &&
            Marshal.SizeOf<ActiveDirectoryComputerPicker.DsOpFilterFlags>() == 16,
            "native filter contains all three up-level ULONGs plus the down-level ULONG");
        Type scope = typeof(ActiveDirectoryComputerPicker.DsOpScopeInitInfo);
        Assert(Marshal.SizeOf(scope) == (IntPtr.Size == 8 ? 56 : 40) &&
            Marshal.OffsetOf(scope, "pwzDcName").ToInt32() == (IntPtr.Size == 8 ? 32 : 28) &&
            Marshal.OffsetOf(scope, "hr").ToInt32() == (IntPtr.Size == 8 ? 48 : 36),
            "scope size and native pointer offsets match objsel.h");
        Type init = typeof(ActiveDirectoryComputerPicker).GetNestedType("DsOpInitInfo", BindingFlags.NonPublic)!;
        Assert(Marshal.SizeOf(init) == (IntPtr.Size == 8 ? 48 : 28), "picker initialization layout matches the Windows ABI");
        Assert(scopes.All(s => (s.flScope & 0x100) != 0 && s.FilterFlags.Uplevel.flBothModes == 0x800 &&
            s.FilterFlags.Uplevel.flMixedModeOnly == 0 && s.FilterFlags.Uplevel.flNativeModeOnly == 0),
            "all scopes select only computer objects instead of well-known principals");
        Assert(scopes.Count(s => (s.flScope & 1) != 0) == 1 && scopes[0].flType == 2 &&
            (scopes[1].flType & (8 | 16 | 32 | 256)) == (8 | 16 | 32 | 256),
            "joined domain is the single starting scope; other domains and entered domain paths remain available");

        using (var data = new SelectionData([
            new("POS01", "pos01.example.test"), new("POS02", null), new("duplicate", "POS01.EXAMPLE.TEST"),
            new("POS01", "pos01.branch.example.test"), new("not a computer", null, "user"),
            new("invalid host", null), new("  POS03  ", "")
        ]))
        {
            string[] hosts = ActiveDirectoryComputerPicker.ReadSelections(data);
            Assert(data.ObservedFormat is >= 0xC000 and <= 0xFFFF, "registered clipboard IDs above signed-short range retain all 16 bits");
            Assert(hosts.SequenceEqual(new[] { "pos01.example.test", "POS02", "pos01.branch.example.test", "POS03" }),
                "native selection imports DNS names, supports name fallback, ignores non-computers and removes duplicates");
        }
        using (var empty = new SelectionData([]))
            Assert(ActiveDirectoryComputerPicker.ReadSelections(empty).Length == 0, "empty Windows selection imports no hosts");
        using (var noAttributes = new SelectionData([new("POS04", null)], fetchedAttributes: 0))
            Assert(ActiveDirectoryComputerPicker.ReadSelections(noAttributes).SequenceEqual(new[] { "POS04" }),
                "selection without fetched attributes uses the computer name");
        using (var malformed = new SelectionData([new("POS05", null)], countOverride: uint.MaxValue))
        {
            bool rejected = false;
            try { ActiveDirectoryComputerPicker.ReadSelections(malformed); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "invalid native array count is rejected before accessing pointers outside the buffer");
        }
    }

    private sealed record Item(string Name, string? Dns, string ObjectClass = "computer");

    // A managed IDataObject supplies actual movable HGLOBAL/VARIANT data with
    // the documented native layout. No dialog, clipboard contents or AD query.
    private sealed class SelectionData(Item[] items, uint fetchedAttributes = 1, uint? countOverride = null) : ComDataObject, IDisposable
    {
        private readonly List<IntPtr> strings = [];
        private readonly List<IntPtr> variants = [];
        internal ushort ObservedFormat { get; private set; }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            ObservedFormat = unchecked((ushort)format.cfFormat);
            Assert(ObservedFormat == RegisterClipboardFormat("CFSTR_DSOP_DS_SELECTION_LIST") && format.tymed == TYMED.TYMED_HGLOBAL && format.dwAspect == DVASPECT.DVASPECT_CONTENT,
                "selection requests the documented Windows data format");
            int stride = IntPtr.Size == 8 ? 48 : 24;
            IntPtr handle = GlobalAlloc(0x42, (UIntPtr)(8 + stride * items.Length));
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            IntPtr memory = GlobalLock(handle);
            try
            {
                Marshal.WriteInt32(memory, unchecked((int)(countOverride ?? (uint)items.Length)));
                Marshal.WriteInt32(memory, 4, (int)fetchedAttributes);
                for (int i = 0; i < items.Length; i++)
                {
                    IntPtr selection = IntPtr.Add(memory, 8 + i * stride);
                    Marshal.WriteIntPtr(selection, String(items[i].Name));
                    Marshal.WriteIntPtr(selection, IntPtr.Size * 2, String(items[i].ObjectClass));
                    if (fetchedAttributes > 0)
                    {
                        IntPtr variant = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 24 : 16);
                        Marshal.GetNativeVariantForObject(items[i].Dns, variant);
                        variants.Add(variant);
                        Marshal.WriteIntPtr(selection, IntPtr.Size * 4, variant);
                    }
                }
            }
            finally { GlobalUnlock(handle); }
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
            // ReadSelections owns the returned medium and releases the HGLOBAL.
        }

        private IntPtr String(string text)
        {
            IntPtr pointer = Marshal.StringToHGlobalUni(text);
            strings.Add(pointer);
            return pointer;
        }

        public void Dispose()
        {
            foreach (IntPtr variant in variants) { VariantClear(variant); Marshal.FreeHGlobal(variant); }
            foreach (IntPtr pointer in strings) Marshal.FreeHGlobal(pointer);
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int QueryGetData(ref FORMATETC format) => 0;
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; return 1; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? advise) { advise = null; return unchecked((int)0x80040003); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(IntPtr variant);
}
