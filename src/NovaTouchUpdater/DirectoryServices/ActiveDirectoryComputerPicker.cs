using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace NovaTouchUpdater;

internal static class ActiveDirectoryComputerPicker
{
    private const uint ScopeTypeUplevelJoinedDomain = 0x00000002;
    private const uint OtherDomainScopes = 0x00000008 | 0x00000010 | 0x00000020 | 0x00000100;
    private const uint ScopeFlagStartingScope = 0x00000001;
    private const uint ScopeFlagDefaultFilterComputers = 0x00000100;
    private const uint FilterIncludeComputers = 0x00000800;
    private const uint PickerFlagMultiSelect = 0x00000001;

    public static string[] SelectComputers(IntPtr owner)
    {
        var picker = (IDsObjectPicker)new DsObjectPicker();
        IntPtr scopes = IntPtr.Zero;
        IntPtr attribute = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        ComDataObject? selections = null;
        try
        {
            DsOpScopeInitInfo[] definitions = CreateScopes();
            int scopeSize = Marshal.SizeOf<DsOpScopeInitInfo>();
            scopes = Marshal.AllocHGlobal(scopeSize * definitions.Length);
            for (int i = 0; i < definitions.Length; i++)
                Marshal.StructureToPtr(definitions[i], IntPtr.Add(scopes, i * scopeSize), false);
            attribute = Marshal.StringToHGlobalUni("dNSHostName");
            attributes = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(attributes, attribute);
            var init = new DsOpInitInfo
            {
                cbSize = (uint)Marshal.SizeOf<DsOpInitInfo>(),
                cDsScopeInfos = (uint)definitions.Length,
                aDsScopeInfos = scopes,
                flOptions = PickerFlagMultiSelect,
                cAttributesToFetch = 1,
                apwzAttributeNames = attributes
            };

            Marshal.ThrowExceptionForHR(picker.Initialize(ref init));
            int result = picker.InvokeDialog(owner, out selections);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            if (result != 0 || selections is null) return [];
            return ReadSelections(selections);
        }
        finally
        {
            if (selections is not null && Marshal.IsComObject(selections)) Marshal.ReleaseComObject(selections);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (attribute != IntPtr.Zero) Marshal.FreeHGlobal(attribute);
            if (scopes != IntPtr.Zero) Marshal.FreeHGlobal(scopes);
            if (Marshal.IsComObject(picker)) Marshal.ReleaseComObject(picker);
        }
    }

    // Layouts and flags follow objsel.h, including all THREE ULONG fields in
    // DSOP_UPLEVEL_FILTER_FLAGS. Incorrect nesting silently filters out computers.
    internal static DsOpScopeInitInfo[] CreateScopes() =>
    [
        CreateScope(ScopeTypeUplevelJoinedDomain, true),
        CreateScope(OtherDomainScopes, false)
    ];

    private static DsOpScopeInitInfo CreateScope(uint types, bool startingScope) => new()
    {
        cbSize = (uint)Marshal.SizeOf<DsOpScopeInitInfo>(),
        flType = types,
        flScope = ScopeFlagDefaultFilterComputers | (startingScope ? ScopeFlagStartingScope : 0),
        FilterFlags = new DsOpFilterFlags { Uplevel = new DsOpUplevelFilterFlags { flBothModes = FilterIncludeComputers } }
    };

    internal static string[] ReadSelections(ComDataObject selections)
    {
        int format = RegisterClipboardFormat("CFSTR_DSOP_DS_SELECTION_LIST");
        if (format == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var formatEtc = new FORMATETC
        {
            // Registered clipboard IDs are unsigned 16-bit values >= 0xC000.
            // FORMATETC exposes a signed short; preserve its bits, not its sign.
            cfFormat = unchecked((short)format),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };
        selections.GetData(ref formatEtc, out STGMEDIUM medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL) throw new InvalidDataException("Die Computerauswahl enthält kein gültiges Windows-Datenformat.");
            IntPtr data = GlobalLock(medium.unionmember);
            if (data == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                ulong length = GlobalSize(medium.unionmember).ToUInt64();
                if (length < sizeof(uint) * 2) throw new InvalidDataException("Die Computerauswahl ist unvollständig.");
                uint count = unchecked((uint)Marshal.ReadInt32(data));
                uint fetchedAttributes = unchecked((uint)Marshal.ReadInt32(data, sizeof(uint)));
                int firstSelectionOffset = sizeof(uint) * 2;
                int selectionSize = Marshal.SizeOf<DsSelection>();
                if (count > int.MaxValue || (ulong)firstSelectionOffset + (ulong)count * (ulong)selectionSize > Math.Min(length, int.MaxValue))
                    throw new InvalidDataException("Die Computerauswahl enthält eine ungültige Anzahl von Einträgen.");
                var hosts = new List<string>((int)count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr address = IntPtr.Add(data, checked(firstSelectionOffset + i * selectionSize));
                    DsSelection selection = Marshal.PtrToStructure<DsSelection>(address);
                    string? objectClass = Marshal.PtrToStringUni(selection.pwzClass);
                    if (!string.IsNullOrEmpty(objectClass) && !objectClass.Equals("computer", StringComparison.OrdinalIgnoreCase)) continue;
                    string? name = fetchedAttributes > 0 && selection.pvarFetchedAttributes != IntPtr.Zero
                        ? Marshal.GetObjectForNativeVariant(selection.pvarFetchedAttributes) as string : null;
                    if (string.IsNullOrWhiteSpace(name)) name = Marshal.PtrToStringUni(selection.pwzName);
                    name = name?.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !name.Any(char.IsWhiteSpace)) hosts.Add(name);
                }
                return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [StructLayout(LayoutKind.Sequential)]
    private struct DsOpInitInfo
    {
        public uint cbSize;
        public IntPtr pwzTargetComputer;
        public uint cDsScopeInfos;
        public IntPtr aDsScopeInfos;
        public uint flOptions;
        public uint cAttributesToFetch;
        public IntPtr apwzAttributeNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DsOpScopeInitInfo
    {
        public uint cbSize;
        public uint flType;
        public uint flScope;
        public DsOpFilterFlags FilterFlags;
        public IntPtr pwzDcName;
        public IntPtr pwzADsPath;
        public int hr;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DsOpFilterFlags
    {
        public DsOpUplevelFilterFlags Uplevel;
        public uint Downlevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DsOpUplevelFilterFlags
    {
        public uint flBothModes;
        public uint flMixedModeOnly;
        public uint flNativeModeOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsSelection
    {
        public IntPtr pwzName;
        public IntPtr pwzADsPath;
        public IntPtr pwzClass;
        public IntPtr pwzUPN;
        public IntPtr pvarFetchedAttributes;
        public uint flScopeType;
    }

    [ComImport]
    [Guid("0C87E64E-3B7A-11D2-B9E0-00C04FD8DBF7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDsObjectPicker
    {
        [PreserveSig]
        int Initialize(ref DsOpInitInfo initInfo);

        [PreserveSig]
        int InvokeDialog(IntPtr owner, [MarshalAs(UnmanagedType.Interface)] out ComDataObject? selections);
    }

    [ComImport]
    [Guid("17D6CCD8-3B7B-11D2-B9E0-00C04FD8DBF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private class DsObjectPicker;
}
