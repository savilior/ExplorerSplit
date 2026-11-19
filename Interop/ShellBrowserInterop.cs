using System.Runtime.InteropServices;

namespace ExplorerSplit.Interop;

internal static class ShellBrowserInterop
{
    internal static readonly Guid ClsidExplorerBrowser = new("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");
    internal static readonly Guid ClsidNamespaceTreeControl = new("AE054212-3535-4430-83ED-D501AA6680E6");
    internal static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static readonly Guid IidIShellItemArray = new("B63EA76D-1F85-456F-A19C-48159EFA858B");
    internal static readonly Guid IidIShellView = new("000214E3-0000-0000-C000-000000000046");
    internal static readonly Guid IidIFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    internal static readonly Guid IidIFolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    internal static readonly Guid IidIContextMenu = new("000214E4-0000-0000-C000-000000000046");

    private static readonly Guid StorageFmtId = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");
    internal static readonly PROPERTYKEY PKEY_ItemNameDisplay = new() { fmtid = StorageFmtId, pid = 10 };
    internal static readonly PROPERTYKEY PKEY_Size = new() { fmtid = StorageFmtId, pid = 12 };
    internal static readonly PROPERTYKEY PKEY_DateModified = new() { fmtid = StorageFmtId, pid = 14 };
    internal static readonly PROPERTYKEY PKEY_ItemTypeText = new() { fmtid = StorageFmtId, pid = 4 };

    internal const int SORT_ASCENDING = 1;
    internal const int SORT_DESCENDING = -1;

    internal const uint FVM_AUTO = unchecked((uint)-1);
    internal const uint FVM_ICON = 1;
    internal const uint FVM_SMALLICON = 2;
    internal const uint FVM_LIST = 3;
    internal const uint FVM_DETAILS = 4;
    internal const uint FVM_THUMBNAIL = 5;
    internal const uint FVM_TILE = 6;
    internal const uint FVM_THUMBSTRIP = 7;

    internal const uint FWF_NONE = 0x00000000;
    internal const uint FWF_AUTOARRANGE = 0x00000001;
    internal const uint FWF_SHOWSELALWAYS = 0x00002000;
    internal const uint FWF_NOWEBVIEW = 0x00010000;

    internal const uint EBO_NONE = 0x00000000;
    internal const uint EBO_NAVIGATEONCE = 0x00000001;
    internal const uint EBO_SHOWFRAMES = 0x00000020;
    internal const uint EBO_ALWAYSNAVIGATE = 0x00000040;
    internal const uint EBO_NOBORDER = 0x00000200;

    internal const uint SBSP_ABSOLUTE = 0x00000000;
    internal const uint SBSP_PARENT = 0x00002000;
    internal const uint SBSP_NAVIGATEBACK = 0x00004000;
    internal const uint SBSP_NAVIGATEFORWARD = 0x00008000;

    internal const uint SIGDN_NORMALDISPLAY = 0x00000000;
    internal const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    internal const uint SIGDN_FILESYSPATH = 0x80058000;

    internal const uint SVGIO_BACKGROUND = 0x00000000;
    internal const uint SVGIO_SELECTION = 0x00000001;
    internal const uint SVGIO_ALLVIEW = 0x00000002;
    internal const uint CMF_NORMAL = 0x00000000;

    // INameSpaceTreeControl styles & flags
    internal const uint NSTCS_HASEXPANDOS = 0x00000001;
    internal const uint NSTCS_SINGLECLICKEXPAND = 0x00000004;
    internal const uint NSTCS_FULLROWSELECT = 0x00000008;
    internal const uint NSTCS_SPRINGEXPAND = 0x00000010;
    internal const uint NSTCS_HORIZONTALSCROLL = 0x00000020;
    internal const uint NSTCS_ROOTHASEXPANDO = 0x00000040;
    internal const uint NSTCS_SHOWSELECTIONALWAYS = 0x00000080;
    internal const uint NSTCS_NOINFOTIP = 0x00000200;
    internal const uint NSTCS_EVENHEIGHT = 0x00000400;
    internal const uint NSTCS_EXPLORER = 0x00010000;
    internal const uint NSTCS_AUTOHSCROLL = 0x00020000;
    internal const uint NSTCS_FADEINOUTEXPANDOS = 0x00040000;

    internal const uint SHCONTF_FOLDERS = 0x00000020;
    internal const uint SHCONTF_NONFOLDERS = 0x00000040;
    internal const uint NSTCRS_VISIBLE = 0x0000;
    internal const uint NSTCRS_HIDDEN = 0x0001;
    internal const uint NSTCRS_EXPANDED = 0x0002;

    internal const uint NSTCEHT_ONITEMICON = 0x0002;
    internal const uint NSTCEHT_ONITEMLABEL = 0x0004;
    internal const uint NSTCEHT_ONITEMBUTTON = 0x0010;
    internal const uint NSTCEHT_ONITEMRIGHT = 0x0020;
    internal const uint NSTCECT_LBUTTON = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FOLDERSETTINGS
    {
        public uint ViewMode;
        public uint fFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SORTCOLUMN
    {
        public PROPERTYKEY propkey;
        public int direction;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPStr)]
        public string lpVerb;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6")]
    internal interface IExplorerBrowser
    {
        [PreserveSig]
        int Initialize(IntPtr hwndParent, ref NativeMethods.RECT prc, ref FOLDERSETTINGS pfs);

        [PreserveSig]
        int Destroy();

        [PreserveSig]
        int SetRect(IntPtr phdwp, NativeMethods.RECT rcBrowser);

        [PreserveSig]
        int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);

        [PreserveSig]
        int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);

        [PreserveSig]
        int SetFolderSettings(ref FOLDERSETTINGS pfs);

        [PreserveSig]
        int Advise([MarshalAs(UnmanagedType.Interface)] IExplorerBrowserEvents psbe, out uint pdwCookie);

        [PreserveSig]
        int Unadvise(uint dwCookie);

        [PreserveSig]
        int SetOptions(uint dwFlag);

        [PreserveSig]
        int GetOptions(out uint pdwFlag);

        [PreserveSig]
        int BrowseToIDList(IntPtr pidl, uint uFlags);

        [PreserveSig]
        int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);

        [PreserveSig]
        int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint dwFlags);

        [PreserveSig]
        int RemoveAll();

        [PreserveSig]
        int GetCurrentView(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    internal interface IShellView
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);

        [PreserveSig]
        int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

        [PreserveSig]
        int TranslateAccelerator(ref NativeMethods.MSG pmsg);

        [PreserveSig]
        int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);

        [PreserveSig]
        int UIActivate(uint uState);

        [PreserveSig]
        int Refresh();

        [PreserveSig]
        int CreateViewWindow(IntPtr psvPrevious, ref FOLDERSETTINGS pfs, IntPtr psb, ref NativeMethods.RECT prcView, out IntPtr phWnd);

        [PreserveSig]
        int DestroyViewWindow();

        [PreserveSig]
        int GetCurrentInfo(out FOLDERSETTINGS pfs);

        [PreserveSig]
        int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lParam);

        [PreserveSig]
        int SaveViewState();

        [PreserveSig]
        int SelectItem(IntPtr pidlItem, uint uFlags);

        [PreserveSig]
        int GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    internal interface IFolderView
    {
        [PreserveSig]
        int GetCurrentViewMode(out uint pViewMode);

        [PreserveSig]
        int SetCurrentViewMode(uint ViewMode);

        [PreserveSig]
        int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

        [PreserveSig]
        int Item(int iItemIndex, out IntPtr ppidl);

        [PreserveSig]
        int ItemCount(uint uFlags, out int pcItems);

        [PreserveSig]
        int Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
    internal interface IFolderView2
    {
        // IFolderView methods
        [PreserveSig]
        int GetCurrentViewMode(out uint pViewMode);

        [PreserveSig]
        int SetCurrentViewMode(uint ViewMode);

        [PreserveSig]
        int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

        [PreserveSig]
        int Item(int iItemIndex, out IntPtr ppidl);

        [PreserveSig]
        int ItemCount(uint uFlags, out int pcItems);

        [PreserveSig]
        int Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

        [PreserveSig]
        int GetSelectionMarkedItem(out int piItem);

        [PreserveSig]
        int GetFocusedItem(out int piItem);

        [PreserveSig]
        int GetItemPosition(IntPtr pidl, out NativeMethods.POINT ppt);

        [PreserveSig]
        int GetSpacing(ref NativeMethods.POINT ppt);

        [PreserveSig]
        int GetDefaultSpacing(out NativeMethods.POINT ppt);

        [PreserveSig]
        int GetAutoArrange();

        [PreserveSig]
        int SelectItem(int iItem, uint dwFlags);

        [PreserveSig]
        int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);

        // IFolderView2 methods
        [PreserveSig]
        int SetGroupBy(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] bool fAscending);

        [PreserveSig]
        int GetGroupBy(out PROPERTYKEY pkey, [MarshalAs(UnmanagedType.Bool)] out bool pfAscending);

        [PreserveSig]
        int SetViewProperty(IntPtr pidl, ref PROPERTYKEY propkey, [MarshalAs(UnmanagedType.Struct)] object propvar);

        [PreserveSig]
        int GetViewProperty(IntPtr pidl, ref PROPERTYKEY propkey, [MarshalAs(UnmanagedType.Struct)] out object ppropvar);

        [PreserveSig]
        int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);

        [PreserveSig]
        int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);

        [PreserveSig]
        int SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);

        [PreserveSig]
        int SetCurrentFolderFlags(uint dwMask, uint dwFlags);

        [PreserveSig]
        int GetCurrentFolderFlags(out uint pdwFlags);

        [PreserveSig]
        int GetSortColumnCount(out int pcColumns);

        [PreserveSig]
        int SetSortColumns([MarshalAs(UnmanagedType.LPArray)] SORTCOLUMN[] rgSortColumns, int cColumns);

        [PreserveSig]
        int GetSortColumns([Out, MarshalAs(UnmanagedType.LPArray)] SORTCOLUMN[] rgSortColumns, int cColumns);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    internal interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFO pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F")]
    internal interface IExplorerBrowserEvents
    {
        [PreserveSig]
        int OnNavigationPending(IntPtr pidlFolder);

        [PreserveSig]
        int OnViewCreated([MarshalAs(UnmanagedType.IUnknown)] object psv);

        [PreserveSig]
        int OnNavigationComplete(IntPtr pidlFolder);

        [PreserveSig]
        int OnNavigationFailed(IntPtr pidlFolder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    internal interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent(out IShellItem ppsi);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    internal interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int GetCount(out uint pdwNumItems);

        [PreserveSig]
        int GetItemAt(uint dwIndex, out IShellItem? ppsi);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("028212A3-B627-47E9-8856-C14265554E4F")]
    internal interface INameSpaceTreeControl
    {
        [PreserveSig]
        int Initialize(IntPtr hwndParent, ref NativeMethods.RECT prc, uint nsctsFlags);

        [PreserveSig]
        int TreeAdvise([MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);

        [PreserveSig]
        int TreeUnadvise(uint dwCookie);

        [PreserveSig]
        int AppendRoot([MarshalAs(UnmanagedType.Interface)] IShellItem psiRoot, uint grfEnumFlags, uint grfRootStyle, IntPtr pif);

        [PreserveSig]
        int InsertRoot(int iIndex, [MarshalAs(UnmanagedType.Interface)] IShellItem psiRoot, uint grfEnumFlags, uint grfRootStyle, IntPtr pif);

        [PreserveSig]
        int RemoveRoot([MarshalAs(UnmanagedType.Interface)] IShellItem psiRoot);

        [PreserveSig]
        int RemoveAllRoots();

        [PreserveSig]
        int GetRootItems(out IntPtr ppsiaRootItems);

        [PreserveSig]
        int SetItemState([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint nstcisMask, uint nstcisFlags);

        [PreserveSig]
        int GetItemState([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint nstcisMask, out uint pnstcisFlags);

        [PreserveSig]
        int GetSelectedItems([MarshalAs(UnmanagedType.Interface)] out IShellItemArray? psiaItems);

        [PreserveSig]
        int GetItemCustomState([MarshalAs(UnmanagedType.Interface)] IShellItem psi, out int piStateNumber);

        [PreserveSig]
        int SetItemCustomState([MarshalAs(UnmanagedType.Interface)] IShellItem psi, int iStateNumber);

        [PreserveSig]
        int EnsureItemVisible([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int SetTheme([MarshalAs(UnmanagedType.LPWStr)] string pszTheme);

        [PreserveSig]
        int GetNextItem([MarshalAs(UnmanagedType.Interface)] IShellItem? psi, uint nstcgi, [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppsiNext);

        [PreserveSig]
        int HitTest(ref NativeMethods.POINT ppt, [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppsiOut, out uint pnstceHitTest);

        [PreserveSig]
        int CollapseAll();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("93D77985-B3D8-4484-8318-672CDDA002CE")]
    internal interface INameSpaceTreeControlEvents
    {
        [PreserveSig]
        int OnItemClick([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint nstceHitTest, uint nstceClickType);

        [PreserveSig]
        int OnPropertyItemCommit([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int OnItemStateChanging([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint nstcisMask, uint nstcisState);

        [PreserveSig]
        int OnItemStateChanged([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint nstcisMask, uint nstcisState);

        [PreserveSig]
        int OnSelectionChanged([MarshalAs(UnmanagedType.Interface)] IShellItemArray psiaSelection);

        [PreserveSig]
        int OnKeyboardInput(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int OnBeforeExpand([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int OnAfterExpand([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int OnBeginLabelEdit([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int OnEndLabelEdit([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int OnGetToolTip([MarshalAs(UnmanagedType.Interface)] IShellItem psi, IntPtr pszTip, int cchTip);

        [PreserveSig]
        int OnBeforeItemDelete([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int OnItemAdded([MarshalAs(UnmanagedType.Interface)] IShellItem psi, [MarshalAs(UnmanagedType.Bool)] bool fIsRoot);

        [PreserveSig]
        int OnItemDeleted([MarshalAs(UnmanagedType.Interface)] IShellItem psi, [MarshalAs(UnmanagedType.Bool)] bool fIsRoot);

        [PreserveSig]
        int OnBeforeContextMenu([MarshalAs(UnmanagedType.Interface)] IShellItem? psi, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int OnAfterContextMenu([MarshalAs(UnmanagedType.Interface)] IShellItem? psi, IntPtr pcmIn, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int OnBeforeStateImageChange([MarshalAs(UnmanagedType.Interface)] IShellItem psi);

        [PreserveSig]
        int OnGetDefaultIconIndex([MarshalAs(UnmanagedType.Interface)] IShellItem psi, out int piDefaultIcon, out int piOpenIcon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHCreateItemFromIDList(
        IntPtr pidl,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);
}
