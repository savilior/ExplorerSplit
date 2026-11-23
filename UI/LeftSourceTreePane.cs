using System.Collections;
using System.Runtime.InteropServices;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

[ComVisible(true)]
internal sealed class LeftSourceTreePane : Panel, ShellBrowserInterop.INameSpaceTreeControlEvents, IPinnedRoots
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
    private long _lastUserInputTick = long.MinValue / 2;
    private const int WmKeyDown = 0x0100;
    private const int WmChar = 0x0102;
    private const int WmSetFocus = 0x0007;

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

        _hostPanel = new KeyboardHostPanel
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

    private const uint NSTCIS_SELECTED = 0x0001;
    private const uint NSTCIS_SELECTEDNOEXPAND = 0x0010;
    private const uint TVM_SELECTITEM = 0x110B;
    private const uint TVGN_CARET = 0x0009;

    private string? _highlightPath;
    private bool _syncingSelection;
    private bool _inTreeFocus;
    private bool _reapplyQueued;
    private long _reapplyWindowStart;
    private int _reapplyCount;

    // The tree changes its selection by itself (a tree view selects its first item, Home, when it
    // gets focus with nothing selected). Put the active folder's highlight back afterwards, unless
    // the user is working in the tree.
    private void QueueReapplyHighlight()
    {
        if (_reapplyQueued || _disposed || !IsHandleCreated)
        {
            return;
        }

        // Never fight the tree in a loop: at most a few corrections per couple of seconds
        long now = Environment.TickCount64;
        if (now - _reapplyWindowStart > 2000)
        {
            _reapplyWindowStart = now;
            _reapplyCount = 0;
        }

        if (++_reapplyCount > 4)
        {
            return;
        }

        _reapplyQueued = true;
        BeginInvoke(() =>
        {
            _reapplyQueued = false;
            if (!_disposed && Environment.TickCount64 - _lastUserInputTick > 1000)
            {
                ApplyHighlight();
            }
        });
    }

    /// <summary>
    /// Highlights the active tab's folder in the tree, as File Explorer does. If the folder is not
    /// shown in the tree nothing is highlighted, unless Folder Options "Expand to open folder" is on;
    /// then the tree expands down to it. The programmatic selection never navigates.
    /// </summary>
    /// <summary>True when the keyboard focus is in the tree (or <paramref name="hwnd"/> belongs to it).</summary>
    public bool ContainsWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && _hostPanel.IsHandleCreated &&
        (hwnd == _hostPanel.Handle || NativeMethods.IsChild(_hostPanel.Handle, hwnd));

    /// <summary>True when the tree itself has the keyboard focus.</summary>
    public bool HasKeyboardFocus => _sysTreeHwnd != IntPtr.Zero && NativeMethods.GetFocus() == _sysTreeHwnd;

    /// <summary>Puts the keyboard focus in the tree, on the highlighted folder (Ctrl+Shift+E, Tab).</summary>
    public void FocusTree()
    {
        if (_sysTreeHwnd == IntPtr.Zero || _disposed)
        {
            return;
        }

        NativeMethods.SetFocus(_sysTreeHwnd);

        // Show the focus rectangle (keyboard cues are hidden until the user presses Alt/Tab in it)
        const int WM_UPDATEUISTATE = 0x0128, UIS_CLEAR = 2, UISF_HIDEFOCUS = 1;
        NativeMethods.SendMessage(_sysTreeHwnd, WM_UPDATEUISTATE, new IntPtr(UIS_CLEAR | (UISF_HIDEFOCUS << 16)), IntPtr.Zero);

        // Nothing highlighted (the folder isn't shown in the tree): the tree would select Home by
        // itself; that selection is ignored and never navigates (OnSelectionChanged)
        ApplyHighlight();
    }

    public void SyncHighlightToPath(string? currentPath)
    {
        _highlightPath = currentPath;
        if (_initialized && _nstc is not null && !_disposed)
        {
            ApplyHighlight();
        }
    }

    private static bool ExpandToOpenFolder()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("NavPaneExpandToCurrentFolder") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyHighlight()
    {
        if (_nstc is null || string.IsNullOrWhiteSpace(_highlightPath))
        {
            return;
        }

        // The folder can be in the tree more than once (a pinned folder is also under OneDrive or
        // This PC). Keep the item the user clicked; otherwise prefer the pinned root, as File
        // Explorer does. Selecting by path would expand OneDrive down to its copy.
        if (SelectedItemIs(_highlightPath))
        {
            return;
        }

        int pinned = _pinnedRoots.FindIndex(r => SameFolder(r.Path, _highlightPath));
        if (pinned >= 0 && _pinnedRoots[pinned].Item != IntPtr.Zero && _sysTreeHwnd != IntPtr.Zero)
        {
            _syncingSelection = true;
            try
            {
                NativeMethods.SendMessage(_sysTreeHwnd, TVM_SELECTITEM, new IntPtr(TVGN_CARET), _pinnedRoots[pinned].Item);
            }
            finally
            {
                _syncingSelection = false;
            }

            return;
        }

        Guid iid = ShellBrowserInterop.IidIShellItem;
        if (ShellBrowserInterop.SHCreateItemFromParsingName(
                _highlightPath, IntPtr.Zero, ref iid, out ShellBrowserInterop.IShellItem? item) != 0 ||
            item is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            bool expand = ExpandToOpenFolder();
            if (expand)
            {
                _nstc.EnsureItemVisible(item);
            }

            // Selecting an item the tree hasn't loaded expands its parents to reach it, so without
            // "Expand to open folder" only folders the tree already shows are highlighted.
            const uint state = NSTCIS_SELECTED | NSTCIS_SELECTEDNOEXPAND;
            bool shown = expand || _nstc.GetItemState(item, NSTCIS_SELECTED, out _) == 0;
            if ((!shown || _nstc.SetItemState(item, state, state) != 0) && _sysTreeHwnd != IntPtr.Zero)
            {
                // Folder not shown in the tree: clear the highlight rather than leave a wrong one.
                NativeMethods.SendMessage(_sysTreeHwnd, TVM_SELECTITEM, new IntPtr(TVGN_CARET), IntPtr.Zero);
            }
        }
        catch
        {
            // Items the tree cannot resolve are simply not highlighted
        }
        finally
        {
            _syncingSelection = false;
            Marshal.ReleaseComObject(item);
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the tree's selected item is the folder <paramref name="path"/>.</summary>
    private bool SelectedItemIs(string path)
    {
        if (_nstc is null || _nstc.GetSelectedItems(out ShellBrowserInterop.IShellItemArray? array) != 0 || array is null)
        {
            return false;
        }

        ShellBrowserInterop.IShellItem? item = null;
        try
        {
            return array.GetCount(out uint count) == 0 && count == 1 &&
                   array.GetItemAt(0, out item) == 0 && item is not null &&
                   item.GetDisplayName(0x80028000 /* SIGDN_DESKTOPABSOLUTEPARSING */, out string? selected) == 0 &&
                   selected is not null && SameFolder(selected, path);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }

            Marshal.ReleaseComObject(array);
        }
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
        _oneDriveRootPath = addedPaths.FirstOrDefault();
        _firstPinnedRootIndex = CountRootItems();
        InsertPinnedRoots();

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
        UpdatePinnedRootItems();
        _thisPcItem = GetRootItemAt(thisPcIndex);

        if (_sysTreeHwnd != IntPtr.Zero && _treeSubclass is null)
        {
            _treeSubclass = new SysTreeSubclass(this);
            _treeSubclass.AssignHandle(_sysTreeHwnd);
        }

        HookReorderDropTarget();
        QuickAccessPins.StartWatching();
        QuickAccessPins.Changed += OnQuickAccessPinsChanged;

        _nstc.TreeAdvise(this, out _eventsCookie);
        SyncTreeBounds();

        // A highlight requested before the tree existed (the window's first folder)
        ApplyHighlight();
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

    // Pinned Quick access folders shown as tree roots: path, index in the Quick access pinned list,
    // and tree item. They start at root index _firstPinnedRootIndex.
    private readonly List<(string Path, int PinIndex, IntPtr Item)> _pinnedRoots = new();
    private int _firstPinnedRootIndex;
    private string? _oneDriveRootPath;
    private QuickAccessReorderDropTarget? _reorderDropTarget;
    private IntPtr _dropTargetHwnd;

    /// <summary>
    /// Shows the current pins as roots. Roots already shown in the right place are kept (with their
    /// icons and expanded state); from the first difference on they are replaced.
    /// </summary>
    /// <returns>The number of roots kept; the ones after them were (re)inserted.</returns>
    private int InsertPinnedRoots()
    {
        List<string> pinned = QuickAccessPins.Current;
        HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
        if (_oneDriveRootPath is not null)
        {
            added.Add(_oneDriveRootPath);
        }

        List<(string Path, int PinIndex)> wanted = new();
        for (int i = 0; i < pinned.Count; i++)
        {
            if (added.Add(pinned[i].TrimEnd('\\')))
            {
                wanted.Add((pinned[i], i));
            }
        }

        int keep = 0;
        while (keep < _pinnedRoots.Count && keep < wanted.Count &&
               string.Equals(_pinnedRoots[keep].Path, wanted[keep].Path, StringComparison.OrdinalIgnoreCase))
        {
            _pinnedRoots[keep] = _pinnedRoots[keep] with { PinIndex = wanted[keep].PinIndex };
            keep++;
        }

        for (int i = _pinnedRoots.Count - 1; i >= keep; i--)
        {
            RemoveShellRoot(_pinnedRoots[i].Path);
            _pinnedRoots.RemoveAt(i);
        }

        int rootIndex = _firstPinnedRootIndex + keep;
        for (int i = keep; i < wanted.Count; i++)
        {
            if (InsertShellRoot(rootIndex, wanted[i].Path, ShellBrowserInterop.SHCONTF_FOLDERS, ShellBrowserInterop.NSTCRS_VISIBLE))
            {
                _pinnedRoots.Add((wanted[i].Path, wanted[i].PinIndex, IntPtr.Zero));
                rootIndex++;
            }
        }

        return keep;
    }

    /// <summary>
    /// Sets a root's icon from the system image list (the tree uses it). A root removed and inserted
    /// again sometimes never gets its icon from the tree's background icon loading.
    /// </summary>
    private void SetRootIcon(IntPtr hItem, string path)
    {
        const uint SHGFI_SYSICONINDEX = 0x000004000;
        const uint TVIF_IMAGE = 0x0002;
        const uint TVIF_SELECTEDIMAGE = 0x0020;

        if (_sysTreeHwnd == IntPtr.Zero || hItem == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SHFILEINFO info = new();
        if (NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                SHGFI_SYSICONINDEX | NativeMethods.SHGFI_SMALLICON) == IntPtr.Zero)
        {
            return;
        }

        TVITEMCHILDREN tvItem = new()
        {
            mask = TVIF_IMAGE | TVIF_SELECTEDIMAGE,
            hItem = hItem,
            iImage = info.iIcon,
            iSelectedImage = info.iIcon
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
    }

    private void SetRootIcons(List<(string Path, IntPtr Item)> roots)
    {
        foreach ((string path, IntPtr item) in roots)
        {
            SetRootIcon(item, path);
        }
    }

    private void RemoveShellRoot(string path)
    {
        if (_nstc is null)
        {
            return;
        }

        Guid iid = ShellBrowserInterop.IidIShellItem;
        if (ShellBrowserInterop.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out ShellBrowserInterop.IShellItem? item) == 0 &&
            item is not null)
        {
            try
            {
                _nstc.RemoveRoot(item);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    private void UpdatePinnedRootItems()
    {
        for (int i = 0; i < _pinnedRoots.Count; i++)
        {
            _pinnedRoots[i] = _pinnedRoots[i] with { Item = GetRootItemAt(_firstPinnedRootIndex + i) };
        }

        _firstPinnedItem = _pinnedRoots.Count > 0 ? _pinnedRoots[0].Item : IntPtr.Zero;
    }

    /// <summary>Quick access pins changed (reordered or pinned from Explorer2): rebuild the pinned roots.</summary>
    private void OnQuickAccessPinsChanged()
    {
        if (_disposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (_disposed || _nstc is null)
            {
                return;
            }

            int kept = InsertPinnedRoots();
            UpdatePinnedRootItems();
            List<(string Path, IntPtr Item)> inserted = _pinnedRoots.Skip(kept).Select(r => (r.Path, r.Item)).ToList();
            SetRootIcons(inserted);

            // Once more after the tree's own background icon loading has run
            System.Windows.Forms.Timer timer = new() { Interval = 1500 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                if (!_disposed)
                {
                    SetRootIcons(inserted.Where(r => _pinnedRoots.Any(p => p.Item == r.Item)).ToList());
                }
            };
            timer.Start();

            if (_sysTreeHwnd != IntPtr.Zero)
            {
                NativeMethods.InvalidateRect(_sysTreeHwnd, IntPtr.Zero, true);
            }

            ApplyHighlight();
        });
    }

    /// <summary>
    /// Puts our drop target in front of the tree's own, so dragging a folder to the edge of a pinned
    /// Quick access folder shows an insertion line and reorders Quick access.
    /// </summary>
    private void HookReorderDropTarget()
    {
        foreach (IntPtr hwnd in new[] { _sysTreeHwnd, _nstcHwnd })
        {
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            IntPtr existing = ShellBrowserInterop.GetProp(hwnd, "OleDropTargetInterface");
            if (existing == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                if (Marshal.GetObjectForIUnknown(existing) is not ShellBrowserInterop.IDropTarget inner)
                {
                    return;
                }

                QuickAccessReorderDropTarget wrapper = new(_sysTreeHwnd, inner, this, _fgColor);
                ShellBrowserInterop.RevokeDragDrop(hwnd);
                if (ShellBrowserInterop.RegisterDragDrop(hwnd, wrapper) == 0)
                {
                    _reorderDropTarget = wrapper;
                    _dropTargetHwnd = hwnd;
                }
                else
                {
                    ShellBrowserInterop.RegisterDragDrop(hwnd, inner); // put the original back
                }
            }
            catch
            {
                // Reordering unavailable; the tree keeps its own drop target
            }

            return;
        }
    }

    private void UnhookReorderDropTarget()
    {
        if (_reorderDropTarget is null || _dropTargetHwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (NativeMethods.IsWindow(_dropTargetHwnd))
            {
                ShellBrowserInterop.RevokeDragDrop(_dropTargetHwnd);
                ShellBrowserInterop.RegisterDragDrop(_dropTargetHwnd, _reorderDropTarget.Inner);
            }
        }
        catch
        {
            // Window already gone
        }

        _reorderDropTarget = null;
        _dropTargetHwnd = IntPtr.Zero;
    }

    int IPinnedRoots.PinnedCount => _pinnedRoots.Count;

    IntPtr IPinnedRoots.PinnedItemAt(int index) => _pinnedRoots[index].Item;

    int IPinnedRoots.PinnedIndexOfItem(IntPtr hItem) => _pinnedRoots.FindIndex(r => r.Item == hItem);

    void IPinnedRoots.DropAt(string path, int insertIndex)
    {
        if (_disposed)
        {
            return;
        }

        // Insertion point in Quick access terms (the tree may skip a pin shown elsewhere, like OneDrive)
        int quickAccessInsert = insertIndex < _pinnedRoots.Count
            ? _pinnedRoots[insertIndex].PinIndex
            : (_pinnedRoots.Count > 0 ? _pinnedRoots[^1].PinIndex + 1 : 0);

        List<string> pinned = QuickAccessPins.Current;
        int from = pinned.FindIndex(p => string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        int newIndex = from >= 0 && quickAccessInsert > from ? quickAccessInsert - 1 : quickAccessInsert;
        if (from >= 0 && newIndex == from)
        {
            return; // dropped where it already is
        }

        // The tree shows the new order at once; the pins are saved in the background
        QuickAccessPins.RequestMove(path, newIndex);
    }

    private void AppendShellRoot(string parsingName, uint enumFlags, uint rootStyle) =>
        InsertShellRoot(-1, parsingName, enumFlags, rootStyle);

    /// <summary>Adds a root at <paramref name="index"/> (-1 = at the end). Returns false if it could not be added.</summary>
    private bool InsertShellRoot(int index, string parsingName, uint enumFlags, uint rootStyle)
    {
        if (_nstc is null)
        {
            return false;
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
                return (index < 0
                    ? _nstc.AppendRoot(item, enumFlags, rootStyle, IntPtr.Zero)
                    : _nstc.InsertRoot(index, item, enumFlags, rootStyle, IntPtr.Zero)) == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        return false;
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
        // The tree also changes its selection on its own (e.g. selecting the first root, Home, when
        // it is created or first focused). Only follow selection changes the user caused with the
        // keyboard or mouse; otherwise a window opened on a folder would jump to Home.
        // SyncHighlightToPath's own selection changes never navigate either.
        if (_disposed || _syncingSelection || psiaSelection is null)
        {
            return 0;
        }

        if (_inTreeFocus || Environment.TickCount64 - _lastUserInputTick > 1000)
        {
            QueueReapplyHighlight();
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
        // A menu given here is added to the folder's own menu; the replacement is in OnAfterContextMenu
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
        try
        {
            // A pinned Quick access root gets the Quick access item's own menu (with "Unpin from
            // Quick access") instead of the folder's, as in File Explorer; other items keep theirs.
            IntPtr treeItem = ContextMenuTreeItem();
            int pinned = _pinnedRoots.FindIndex(r => r.Item == treeItem);
            if (pinned >= 0 && TryGetQuickAccessMenu(_pinnedRoots[pinned].Path, ref riid, out ppv))
            {
                return 0;
            }
        }
        catch
        {
            ppv = IntPtr.Zero;
        }

        return E_NOTIMPL;
    }

    /// <summary>The tree item the context menu is for: under the mouse, or the caret (Shift+F10).</summary>
    private IntPtr ContextMenuTreeItem()
    {
        if (_sysTreeHwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt) && NativeMethods.ScreenToClient(_sysTreeHwnd, ref pt) &&
            NativeMethods.GetClientRect(_sysTreeHwnd, out NativeMethods.RECT client) &&
            pt.X >= 0 && pt.Y >= 0 && pt.X < client.Width && pt.Y < client.Height)
        {
            TVHITTESTINFO ht = new() { pt = pt };
            IntPtr htPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TVHITTESTINFO>());
            try
            {
                Marshal.StructureToPtr(ht, htPtr, false);
                NativeMethods.SendMessage(_sysTreeHwnd, TVM_HITTEST, IntPtr.Zero, htPtr);
                ht = Marshal.PtrToStructure<TVHITTESTINFO>(htPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(htPtr);
            }

            if (ht.hItem != IntPtr.Zero)
            {
                return ht.hItem;
            }
        }

        return NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETNEXTITEM, new IntPtr(TVGN_CARET), IntPtr.Zero);
    }

    /// <summary>Context menu of the Quick access entry for <paramref name="path"/>.</summary>
    private static bool TryGetQuickAccessMenu(string path, ref Guid riid, out IntPtr ppv)
    {
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
        ppv = IntPtr.Zero;

        Guid iidItem = ShellBrowserInterop.IidIShellItem;
        if (ShellBrowserInterop.SHCreateItemFromParsingName("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}", IntPtr.Zero,
                ref iidItem, out ShellBrowserInterop.IShellItem? quickAccess) != 0 || quickAccess is null)
        {
            return false;
        }

        try
        {
            Guid bhidEnum = ShellBrowserInterop.BhidEnumItems;
            Guid iidEnum = ShellBrowserInterop.IidIEnumShellItems;
            if (quickAccess.BindToHandler(IntPtr.Zero, ref bhidEnum, ref iidEnum, out IntPtr enumPtr) != 0 || enumPtr == IntPtr.Zero)
            {
                return false;
            }

            ShellBrowserInterop.IEnumShellItems items = (ShellBrowserInterop.IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            Marshal.Release(enumPtr);
            try
            {
                while (items.Next(1, out ShellBrowserInterop.IShellItem? child, out uint fetched) == 0 && fetched == 1 && child is not null)
                {
                    try
                    {
                        bool match =
                            (child.GetDisplayName(SIGDN_FILESYSPATH, out string? fsPath) == 0 && fsPath is not null && SameFolder(fsPath, path)) ||
                            (child.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out string? parsing) == 0 && parsing is not null && SameFolder(parsing, path));
                        if (match)
                        {
                            Guid bhidUi = ShellBrowserInterop.BhidSFUIObject;
                            return child.BindToHandler(IntPtr.Zero, ref bhidUi, ref riid, out ppv) == 0 && ppv != IntPtr.Zero;
                        }
                    }
                    catch
                    {
                        // Skip items without a path
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(child);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(quickAccess);
        }

        return false;
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

        QuickAccessPins.Changed -= OnQuickAccessPinsChanged;
        UnhookReorderDropTarget();

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

        _pinFont?.Dispose();
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

    private Font? _pinFont;

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>The pin icon File Explorer shows at the right of pinned Quick access folders.</summary>
    private void DrawPinIcons()
    {
        if (_sysTreeHwnd == IntPtr.Zero || _pinnedRoots.Count == 0 ||
            !NativeMethods.GetClientRect(_sysTreeHwnd, out NativeMethods.RECT clientRect) || clientRect.Width <= 60)
        {
            return;
        }

        if (_pinFont is null)
        {
            using Font probe = new("Segoe Fluent Icons", 8f);
            _pinFont = probe.Name == "Segoe Fluent Icons" ? new Font("Segoe Fluent Icons", 8f) : new Font("Segoe MDL2 Assets", 8f);
        }

        // Light: sampled from File Explorer's navigation pane
        Color color = _isDarkMode ? Color.FromArgb(150, 160, 166) : Color.FromArgb(149, 160, 166);
        using Graphics g = Graphics.FromHwnd(_sysTreeHwnd);
        IntPtr buffer = Marshal.AllocHGlobal(Math.Max(Marshal.SizeOf<NativeMethods.RECT>(), IntPtr.Size * 2));
        try
        {
            foreach ((_, _, IntPtr item) in _pinnedRoots)
            {
                Marshal.WriteIntPtr(buffer, item);
                if (item == IntPtr.Zero || NativeMethods.SendMessage(_sysTreeHwnd, TVM_GETITEMRECT, IntPtr.Zero, buffer) == IntPtr.Zero)
                {
                    continue; // scrolled out of view
                }

                NativeMethods.RECT rect = Marshal.PtrToStructure<NativeMethods.RECT>(buffer);
                Rectangle glyph = new(clientRect.Width - 30, rect.Top, 20, rect.Bottom - rect.Top);

                // A long name stops before the pin: cover it with the row's own background
                // (sampled at the row's right end, so hover and selection colors are kept).
                IntPtr hdc = g.GetHdc();
                uint pixel;
                try
                {
                    pixel = GetPixel(hdc, clientRect.Width - 8, (rect.Top + rect.Bottom) / 2);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }

                if (pixel != 0xFFFFFFFF) // CLR_INVALID
                {
                    using SolidBrush cover = new(ColorTranslator.FromWin32((int)pixel));
                    g.FillRectangle(cover, new Rectangle(glyph.Left - 6, rect.Top + 3, glyph.Right + 2 - (glyph.Left - 6), rect.Bottom - rect.Top - 6));
                }

                // File Explorer's pin: a filled head (PinFill) with the needle of the outline Pin,
                // smoothed in gray scale (no ClearType color fringes)
                using SolidBrush pinBrush = new(color);
                using StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.DrawString("", _pinFont, pinBrush, glyph, center);
                g.DrawString("", _pinFont, pinBrush, glyph, center);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Hosts the native tree. WinForms would otherwise treat the arrow keys, Home/End, PgUp/PgDn,
    /// Enter and Esc pressed in the tree as dialog navigation between controls and never pass them
    /// to the tree, so the keyboard did nothing there.
    /// </summary>
    private sealed class KeyboardHostPanel : Panel
    {
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End
                       or Keys.PageUp or Keys.PageDown or Keys.Enter or Keys.Escape
                   || base.IsInputKey(keyData);
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
            if (m.Msg is WmKeyDown or WmChar or (int)WM_LBUTTONDOWN or (int)WM_LBUTTONDBLCLK)
            {
                _owner._lastUserInputTick = Environment.TickCount64;
            }

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

            if (m.Msg == WmSetFocus)
            {
                // A selection the tree makes while taking focus is not the user's choice
                _owner._inTreeFocus = true;
                try
                {
                    base.WndProc(ref m);
                }
                finally
                {
                    _owner._inTreeFocus = false;
                }

                return;
            }

            base.WndProc(ref m);

            if (m.Msg == (int)WM_PAINT)
            {
                _owner.DrawGroupDividerAboveItem(_owner._firstPinnedItem);
                _owner.DrawGroupDividerAboveItem(_owner._thisPcItem);
                _owner.DrawPinIcons();
            }
        }
    }
}
