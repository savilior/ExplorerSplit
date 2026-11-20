using System.Collections;
using System.Runtime.InteropServices;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

[ComVisible(true)]
internal sealed class LeftSourceTreePane : Panel, ShellBrowserInterop.INameSpaceTreeControlEvents
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint WM_PAINT = 0x000F;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint TVM_EXPAND = 0x1102;
    private const uint TVM_GETITEMRECT = 0x1104;
    private const uint TVM_GETNEXTITEM = 0x110A;
    private const uint TVM_HITTEST = 0x1111;
    private const uint TVM_SETITEMHEIGHT = 0x111B;
    private const uint TVM_SETBKCOLOR = 0x111D;
    private const uint TVM_SETTEXTCOLOR = 0x111E;
    private const uint TVM_SETITEMW = 0x113F;
    private const uint TVGN_ROOT = 0;
    private const uint TVGN_NEXT = 1;
    private const uint TVIF_CHILDREN = 0x0040;
    private const uint TVE_TOGGLE = 0x0003;
    private const uint TVHT_ONITEMBUTTON = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct TVHITTESTINFO
    {
        public NativeMethods.POINT pt;
        public uint flags;
        public IntPtr hItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TVITEMCHILDREN
    {
        public uint mask;
        public IntPtr hItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public int iSelectedImage;
        public int cChildren;
        public IntPtr lParam;
    }

    private readonly Panel _hostPanel;
    private readonly bool _isDarkMode;
    private readonly Color _bgColor;
    private readonly Color _fgColor;
    private readonly Color _dividerColor;

    private ShellBrowserInterop.INameSpaceTreeControl? _nstc;
    private IntPtr _nstcHwnd;
    private IntPtr _sysTreeHwnd;
    private SysTreeSubclass? _treeSubclass;
    private IntPtr _firstPinnedItem;
    private IntPtr _thisPcItem;
    private uint _eventsCookie;
    private bool _initialized;
    private bool _disposed;

    private string? _lastEmittedPath;
    private long _lastEmittedTick;

    public event Action<string>? SourceSelected;

    public LeftSourceTreePane(bool isDarkMode)
    {
        _isDarkMode = isDarkMode;
        _bgColor = isDarkMode ? Color.FromArgb(25, 25, 28) : Color.FromArgb(255, 255, 255);
        _fgColor = isDarkMode ? Color.FromArgb(242, 242, 246) : Color.FromArgb(24, 24, 28);
        _dividerColor = isDarkMode ? Color.FromArgb(52, 52, 58) : Color.FromArgb(226, 226, 232);
        Color borderColor = isDarkMode ? Color.FromArgb(56, 56, 62) : Color.FromArgb(218, 218, 224);

        Dock = DockStyle.Fill;
        BackColor = _bgColor;
        Padding = new Padding(8, 0, 0, 0);
        Margin = Padding.Empty;

        _hostPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _bgColor,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        _hostPanel.HandleCreated += (_, _) =>
        {
            if (!_disposed && _hostPanel.IsHandleCreated)
            {
                _hostPanel.BeginInvoke(EnsureInitialized);
            }
        };
        _hostPanel.Resize += (_, _) => SyncTreeBounds();

        Panel rightBorder = new()
        {
            Dock = DockStyle.Right,
            Width = 1,
            BackColor = borderColor
        };

        Controls.Add(_hostPanel);
        Controls.Add(rightBorder);
    }

    public void SyncHighlightToPath(string? currentPath)
    {
        // Native NamespaceTreeControl manages its own item state; we avoid forcing
        // tree re-expansion on tab switch so user's expanded tree state stays undisturbed.
    }

    private void EnsureInitialized()
    {
        if (_disposed || _initialized || !_hostPanel.IsHandleCreated)
        {
            return;
        }

        _initialized = true;

        Type? nstcType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidNamespaceTreeControl);
        if (nstcType is null)
        {
            return;
        }

        if (Activator.CreateInstance(nstcType) is not ShellBrowserInterop.INameSpaceTreeControl nstc)
        {
            return;
        }

        _nstc = nstc;

        NativeMethods.RECT rc = new(
            0,
            0,
            Math.Max(100, _hostPanel.ClientSize.Width),
            Math.Max(100, _hostPanel.ClientSize.Height));

        uint flags =
            ShellBrowserInterop.NSTCS_HASEXPANDOS |
            ShellBrowserInterop.NSTCS_FULLROWSELECT |
            ShellBrowserInterop.NSTCS_HORIZONTALSCROLL |
            ShellBrowserInterop.NSTCS_ROOTHASEXPANDO |
            ShellBrowserInterop.NSTCS_SHOWSELECTIONALWAYS |
            ShellBrowserInterop.NSTCS_NOINFOTIP |
            ShellBrowserInterop.NSTCS_EVENHEIGHT |
            ShellBrowserInterop.NSTCS_EXPLORER |
            ShellBrowserInterop.NSTCS_AUTOHSCROLL |
            ShellBrowserInterop.NSTCS_FADEINOUTEXPANDOS;

        int hr = _nstc.Initialize(_hostPanel.Handle, ref rc, flags);
        if (hr != 0)
        {
            DestroyTreeControl();
            return;
        }

        _nstc.SetTheme(_isDarkMode ? "DarkMode_Explorer" : "Explorer");

        _nstcHwnd = NativeMethods.FindWindowEx(_hostPanel.Handle, IntPtr.Zero, "NamespaceTreeControl", null);
        if (_nstcHwnd != IntPtr.Zero)
        {
            _sysTreeHwnd = NativeMethods.FindWindowEx(_nstcHwnd, IntPtr.Zero, "SysTreeView32", null);
            if (_sysTreeHwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowTheme(
                    _sysTreeHwnd,
                    _isDarkMode ? "DarkMode_Explorer" : "Explorer",
                    null);

                int win32Bg = ColorTranslator.ToWin32(_bgColor);
                int win32Fg = ColorTranslator.ToWin32(_fgColor);
                NativeMethods.SendMessage(_sysTreeHwnd, TVM_SETBKCOLOR, IntPtr.Zero, new IntPtr(win32Bg));
                NativeMethods.SendMessage(_sysTreeHwnd, TVM_SETTEXTCOLOR, IntPtr.Zero, new IntPtr(win32Fg));
                NativeMethods.SendMessage(_sysTreeHwnd, TVM_SETITEMHEIGHT, new IntPtr(28), IntPtr.Zero);
            }
        }

        // Populate roots matching native Windows 11 File Explorer Navigation Pane:
        // 1. Home (standalone top item, no expand chevron)
        AppendShellRoot(
            ExplorerDetector.HomeShellGuid,
            0,
            ShellBrowserInterop.NSTCRS_VISIBLE);

        // 2. Gallery (standalone top item, no expand chevron)
        AppendShellRoot(
            "::{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}",
            0,
            ShellBrowserInterop.NSTCRS_VISIBLE);

        // 3. OneDrive (if present)
        HashSet<string> addedPaths = new(StringComparer.OrdinalIgnoreCase);
        string? oneDrivePath = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrivePath) && Directory.Exists(oneDrivePath))
        {
            addedPaths.Add(oneDrivePath.TrimEnd('\\'));
            AppendShellRoot(
                oneDrivePath,
                ShellBrowserInterop.SHCONTF_FOLDERS,
                ShellBrowserInterop.NSTCRS_VISIBLE);
        }

        // 4. Pinned Quick Access folders at root level (each with its own '>' expand chevron)
        int pinnedCount = 0;
        int rootCountBeforePinned = CountRootItems();
        foreach (string pinnedPath in GetPinnedQuickAccessFolders())
        {
            string normalized = pinnedPath.TrimEnd('\\');
            if (addedPaths.Add(normalized))
            {
                AppendShellRoot(
                    pinnedPath,
                    ShellBrowserInterop.SHCONTF_FOLDERS,
                    ShellBrowserInterop.NSTCRS_VISIBLE);
                pinnedCount++;
            }
        }

        // 5. This PC (expanded to show Desktop, Downloads, Documents, Pictures, Music, Videos, and Drives)
        int thisPcIndex = CountRootItems();
        AppendShellRoot(
            "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            ShellBrowserInterop.SHCONTF_FOLDERS,
            ShellBrowserInterop.NSTCRS_VISIBLE | ShellBrowserInterop.NSTCRS_EXPANDED);

        // 6. Network
        AppendShellRoot(
            "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}",
            ShellBrowserInterop.SHCONTF_FOLDERS,
            ShellBrowserInterop.NSTCRS_VISIBLE);

        ClearRootExpandoForStandaloneItems(2);
        _firstPinnedItem = pinnedCount > 0 ? GetRootItemAt(rootCountBeforePinned) : IntPtr.Zero;
        _thisPcItem = GetRootItemAt(thisPcIndex);

        if (_sysTreeHwnd != IntPtr.Zero && _treeSubclass is null)
        {
            _treeSubclass = new SysTreeSubclass(this);
            _treeSubclass.AssignHandle(_sysTreeHwnd);
        }

        _nstc.TreeAdvise(this, out _eventsCookie);
        SyncTreeBounds();
    }

    private int CountRootItems()
    {
        if (_sysTreeHwnd == IntPtr.Zero)
        {
            return 0;
        }

        int count = 0;
        IntPtr hItem = NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETNEXTITEM, new IntPtr(TVGN_ROOT), IntPtr.Zero);
        while (hItem != IntPtr.Zero)
        {
            count++;
            hItem = NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETNEXTITEM, new IntPtr(TVGN_NEXT), hItem);
        }

        return count;
    }

    private IntPtr GetRootItemAt(int index)
    {
        if (_sysTreeHwnd == IntPtr.Zero || index < 0)
        {
            return IntPtr.Zero;
        }

        IntPtr hItem = NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETNEXTITEM, new IntPtr(TVGN_ROOT), IntPtr.Zero);
        for (int i = 0; i < index && hItem != IntPtr.Zero; i++)
        {
            hItem = NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETNEXTITEM, new IntPtr(TVGN_NEXT), hItem);
        }

        return hItem;
    }

    private void ClearRootExpandoForStandaloneItems(int count)
    {
        if (_sysTreeHwnd == IntPtr.Zero || count <= 0)
        {
            return;
        }

        IntPtr hItem = NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETNEXTITEM, new IntPtr(TVGN_ROOT), IntPtr.Zero);
        for (int i = 0; i < count && hItem != IntPtr.Zero; i++)
        {
            TVITEMCHILDREN tvItem = new()
            {
                mask = TVIF_CHILDREN,
                hItem = hItem,
                cChildren = 0
            };

            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TVITEMCHILDREN>());
            try
            {
                Marshal.StructureToPtr(tvItem, ptr, false);
                NativeMethods.SendMessage(_sysTreeHwnd, TVM_SETITEMW, IntPtr.Zero, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            hItem = NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETNEXTITEM, new IntPtr(TVGN_NEXT), hItem);
        }
    }

    private static List<string> GetPinnedQuickAccessFolders()
    {
        List<string> results = new();
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return results;
        }

        object? shell = null;
        object? nsObj = null;
        object? itemsObj = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return results;
            }

            dynamic dynShell = shell;
            nsObj = dynShell.NameSpace($"shell:{ExplorerDetector.HomeShellGuid}");
            if (nsObj is null)
            {
                return results;
            }

            dynamic dynNs = nsObj;
            itemsObj = dynNs.Items();
            if (itemsObj is not IEnumerable items)
            {
                return results;
            }

            foreach (object? item in items)
            {
                if (item is null)
                {
                    continue;
                }

                try
                {
                    dynamic dynItem = item;
                    bool isFolder = Convert.ToBoolean(dynItem.IsFolder);
                    string? path = dynItem.Path as string;
                    if (isFolder && !string.IsNullOrWhiteSpace(path))
                    {
                        results.Add(path);
                    }
                }
                catch
                {
                    // Ignore non-folder or inaccessible items
                }
                finally
                {
                    if (Marshal.IsComObject(item))
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
        }
        catch
        {
            // Fall back gracefully if Quick Access enumeration is unavailable
        }
        finally
        {
            if (itemsObj is not null && Marshal.IsComObject(itemsObj)) Marshal.ReleaseComObject(itemsObj);
            if (nsObj is not null && Marshal.IsComObject(nsObj)) Marshal.ReleaseComObject(nsObj);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }

        return results;
    }

    private void AppendShellRoot(string parsingName, uint enumFlags, uint rootStyle)
    {
        if (_nstc is null)
        {
            return;
        }

        Guid iid = ShellBrowserInterop.IidIShellItem;
        if (ShellBrowserInterop.SHCreateItemFromParsingName(
                parsingName,
                IntPtr.Zero,
                ref iid,
                out ShellBrowserInterop.IShellItem? item) == 0 &&
            item is not null)
        {
            try
            {
                _nstc.AppendRoot(item, enumFlags, rootStyle, IntPtr.Zero);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    private void SyncTreeBounds()
    {
        if (_disposed || !_hostPanel.IsHandleCreated)
        {
            return;
        }

        int w = Math.Max(50, _hostPanel.ClientSize.Width);
        int h = Math.Max(50, _hostPanel.ClientSize.Height);

        if (_nstcHwnd == IntPtr.Zero)
        {
            _nstcHwnd = NativeMethods.FindWindowEx(_hostPanel.Handle, IntPtr.Zero, "NamespaceTreeControl", null);
        }

        if (_nstcHwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _nstcHwnd,
                IntPtr.Zero,
                0,
                0,
                w,
                h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            if (_sysTreeHwnd == IntPtr.Zero)
            {
                _sysTreeHwnd = NativeMethods.FindWindowEx(_nstcHwnd, IntPtr.Zero, "SysTreeView32", null);
            }

            if (_sysTreeHwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    _sysTreeHwnd,
                    IntPtr.Zero,
                    0,
                    0,
                    w,
                    h,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }
    }

    private void EmitShellItemNavigation(ShellBrowserInterop.IShellItem? item)
    {
        if (_disposed || item is null)
        {
            return;
        }

        item.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? fileSystemPath);
        item.GetDisplayName(ShellBrowserInterop.SIGDN_DESKTOPABSOLUTEPARSING, out string? parsingPath);

        string? resolved = !string.IsNullOrWhiteSpace(fileSystemPath)
            ? fileSystemPath
            : parsingPath;

        if (string.IsNullOrWhiteSpace(resolved))
        {
            return;
        }

        long now = Environment.TickCount64;
        if (string.Equals(resolved, _lastEmittedPath, StringComparison.OrdinalIgnoreCase) &&
            now - _lastEmittedTick < 180)
        {
            return;
        }

        _lastEmittedPath = resolved;
        _lastEmittedTick = now;

        void Notify()
        {
            if (!_disposed)
            {
                SourceSelected?.Invoke(resolved);
            }
        }

        if (InvokeRequired)
        {
            BeginInvoke(Notify);
        }
        else
        {
            Notify();
        }
    }

    // INameSpaceTreeControlEvents implementation
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnItemClick(
        ShellBrowserInterop.IShellItem psi,
        uint nstceHitTest,
        uint nstceClickType)
    {
        // Only navigate when left-clicking the item icon/label/row, NOT the expand/collapse chevron button
        if (nstceClickType == ShellBrowserInterop.NSTCECT_LBUTTON &&
            (nstceHitTest & ShellBrowserInterop.NSTCEHT_ONITEMBUTTON) == 0 &&
            (nstceHitTest & (ShellBrowserInterop.NSTCEHT_ONITEMICON |
                             ShellBrowserInterop.NSTCEHT_ONITEMLABEL |
                             ShellBrowserInterop.NSTCEHT_ONITEMRIGHT)) != 0)
        {
            EmitShellItemNavigation(psi);
        }

        return 0;
    }

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnSelectionChanged(
        ShellBrowserInterop.IShellItemArray psiaSelection)
    {
        if (_disposed || psiaSelection is null)
        {
            return 0;
        }

        if (psiaSelection.GetCount(out uint count) == 0 && count > 0 &&
            psiaSelection.GetItemAt(0, out ShellBrowserInterop.IShellItem? firstItem) == 0 &&
            firstItem is not null)
        {
            try
            {
                EmitShellItemNavigation(firstItem);
            }
            finally
            {
                Marshal.ReleaseComObject(firstItem);
            }
        }

        return 0;
    }

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnPropertyItemCommit(ShellBrowserInterop.IShellItem psi) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnItemStateChanging(ShellBrowserInterop.IShellItem psi, uint nstcisMask, uint nstcisState) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnItemStateChanged(ShellBrowserInterop.IShellItem psi, uint nstcisMask, uint nstcisState) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnKeyboardInput(uint uMsg, IntPtr wParam, IntPtr lParam) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeforeExpand(ShellBrowserInterop.IShellItem psi) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnAfterExpand(ShellBrowserInterop.IShellItem psi) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeginLabelEdit(ShellBrowserInterop.IShellItem psi) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnEndLabelEdit(ShellBrowserInterop.IShellItem psi) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnGetToolTip(ShellBrowserInterop.IShellItem psi, IntPtr pszTip, int cchTip) => E_NOTIMPL;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeforeItemDelete(ShellBrowserInterop.IShellItem psi) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnItemAdded(ShellBrowserInterop.IShellItem psi, bool fIsRoot) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnItemDeleted(ShellBrowserInterop.IShellItem psi, bool fIsRoot) => 0;

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeforeContextMenu(
        ShellBrowserInterop.IShellItem? psi,
        ref Guid riid,
        out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnAfterContextMenu(
        ShellBrowserInterop.IShellItem? psi,
        IntPtr pcmIn,
        ref Guid riid,
        out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeforeStateImageChange(ShellBrowserInterop.IShellItem psi) => 0;

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnGetDefaultIconIndex(
        ShellBrowserInterop.IShellItem psi,
        out int piDefaultIcon,
        out int piOpenIcon)
    {
        piDefaultIcon = -1;
        piOpenIcon = -1;
        return E_NOTIMPL;
    }

    private void DestroyTreeControl()
    {
        if (_nstc is null)
        {
            return;
        }

        try
        {
            if (_eventsCookie != 0)
            {
                _nstc.TreeUnadvise(_eventsCookie);
                _eventsCookie = 0;
            }

            _nstc.RemoveAllRoots();
        }
        catch
        {
            // Ignore teardown errors
        }
        finally
        {
            try
            {
                Marshal.ReleaseComObject(_nstc);
            }
            catch
            {
                // Ignore
            }

            _nstc = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _treeSubclass?.ReleaseHandle();
            _treeSubclass = null;
            DestroyTreeControl();
        }

        base.Dispose(disposing);
    }

    private void DrawGroupDividerAboveItem(IntPtr hItem)
    {
        if (hItem == IntPtr.Zero || _sysTreeHwnd == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.GetClientRect(_sysTreeHwnd, out NativeMethods.RECT clientRect) ||
            clientRect.Width <= 24)
        {
            return;
        }

        // TVM_GETITEMRECT expects the HTREEITEM handle in the first 8 bytes of the RECT buffer
        int structSize = Math.Max(Marshal.SizeOf<NativeMethods.RECT>(), IntPtr.Size * 2);
        IntPtr ptr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.WriteIntPtr(ptr, hItem);
            IntPtr ok = NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETITEMRECT, IntPtr.Zero, ptr);
            if (ok == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.RECT itemRect = Marshal.PtrToStructure<NativeMethods.RECT>(ptr);
            int y = itemRect.Top - 1;
            if (y <= 1 || y >= clientRect.Height - 1)
            {
                return;
            }

            using Graphics g = Graphics.FromHwnd(_sysTreeHwnd);
            using Pen dividerPen = new(_dividerColor, 1f);
            g.DrawLine(dividerPen, 12, y, clientRect.Width - 12, y);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private sealed class SysTreeSubclass : NativeWindow
    {
        private readonly LeftSourceTreePane _owner;

        public SysTreeSubclass(LeftSourceTreePane owner)
        {
            _owner = owner;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is (int)WM_LBUTTONDOWN or (int)WM_LBUTTONDBLCLK)
            {
                int lParam = m.LParam.ToInt32();
                int x = (short)(lParam & 0xFFFF);
                int y = (short)((lParam >> 16) & 0xFFFF);

                TVHITTESTINFO ht = new()
                {
                    pt = new NativeMethods.POINT { X = x, Y = y }
                };

                IntPtr htPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TVHITTESTINFO>());
                try
                {
                    Marshal.StructureToPtr(ht, htPtr, false);
                    NativeMethods.SendMessage(Handle, TVM_HITTEST, IntPtr.Zero, htPtr);
                    ht = Marshal.PtrToStructure<TVHITTESTINFO>(htPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(htPtr);
                }

                if ((ht.flags & TVHT_ONITEMBUTTON) != 0 && ht.hItem != IntPtr.Zero)
                {
                    if (m.Msg == (int)WM_LBUTTONDOWN)
                    {
                        NativeMethods.SendMessage(Handle, TVM_EXPAND, new IntPtr(TVE_TOGGLE), ht.hItem);
                    }

                    m.Result = IntPtr.Zero;
                    return;
                }
            }

            base.WndProc(ref m);

            if (m.Msg == (int)WM_PAINT)
            {
                _owner.DrawGroupDividerAboveItem(_owner._firstPinnedItem);
                _owner.DrawGroupDividerAboveItem(_owner._thisPcItem);
            }
        }
    }
}
