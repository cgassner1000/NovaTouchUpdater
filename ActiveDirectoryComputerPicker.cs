using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace NovaTouchUpdater;

internal static class ActiveDirectoryComputerPicker
{
    private const uint ScopeTypeUplevelJoinedDomain = 0x00000002;
    private const uint ScopeFlagStartingScope = 0x00000001;
    private const uint ScopeFlagDefaultFilterComputers = 0x00000010;
    private const uint FilterIncludeComputers = 0x00000008;
    private const uint PickerFlagMultiSelect = 0x00000001;

    public static string[] SelectComputers(IntPtr owner)
    {
        var picker = (IDsObjectPicker)new DsObjectPicker();
        IntPtr scopes = IntPtr.Zero;
        try
        {
            var scope = new DsOpScopeInitInfo
            {
                cbSize = (uint)Marshal.SizeOf<DsOpScopeInitInfo>(),
                flType = ScopeTypeUplevelJoinedDomain,
                flScope = ScopeFlagStartingScope | ScopeFlagDefaultFilterComputers,
                FilterFlags = new DsOpFilterFlags { Uplevel = FilterIncludeComputers }
            };
            scopes = Marshal.AllocHGlobal(Marshal.SizeOf<DsOpScopeInitInfo>());
            Marshal.StructureToPtr(scope, scopes, false);
            var init = new DsOpInitInfo
            {
                cbSize = (uint)Marshal.SizeOf<DsOpInitInfo>(),
                cDsScopeInfos = 1,
                aDsScopeInfos = scopes,
                flOptions = PickerFlagMultiSelect
            };

            Marshal.ThrowExceptionForHR(picker.Initialize(ref init));
            int result = picker.InvokeDialog(owner, out ComDataObject? selections);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            if (result != 0 || selections is null) return [];
            return ReadSelections(selections);
        }
        finally
        {
            if (scopes != IntPtr.Zero) Marshal.FreeHGlobal(scopes);
            if (Marshal.IsComObject(picker)) Marshal.ReleaseComObject(picker);
        }
    }

    private static string[] ReadSelections(ComDataObject selections)
    {
        int format = RegisterClipboardFormat("CFSTR_DSOP_DS_SELECTION_LIST");
        if (format == 0) Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        var formatEtc = new FORMATETC
        {
            cfFormat = checked((short)format),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };
        selections.GetData(ref formatEtc, out STGMEDIUM medium);
        try
        {
            IntPtr data = GlobalLock(medium.unionmember);
            if (data == IntPtr.Zero) Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            try
            {
                uint count = unchecked((uint)Marshal.ReadInt32(data));
                int firstSelectionOffset = sizeof(uint) * 2;
                int selectionSize = Marshal.SizeOf<DsSelection>();
                var hosts = new List<string>((int)count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr address = IntPtr.Add(data, checked(firstSelectionOffset + i * selectionSize));
                    DsSelection selection = Marshal.PtrToStructure<DsSelection>(address);
                    string? name = Marshal.PtrToStringUni(selection.pwzName)?.Trim();
                    if (!string.IsNullOrWhiteSpace(name)) hosts.Add(name);
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
    private struct DsOpScopeInitInfo
    {
        public uint cbSize;
        public uint flType;
        public uint flScope;
        public DsOpFilterFlags FilterFlags;
        public IntPtr pwzDcName;
        public IntPtr pwzADsPath;
        public int hr;
        public uint cAttributesToFetch;
        public IntPtr apwzAttributeNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsOpFilterFlags
    {
        public uint Uplevel;
        public uint Downlevel;
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
