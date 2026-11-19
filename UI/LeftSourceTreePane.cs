using System.Runtime.InteropServices;
using ExplorerSplit.Core;
using ExplorerSplit.Interop;

namespace ExplorerSplit.UI;

[ComVisible(true)]
internal sealed class LeftSourceTreePane : Panel, ShellBrowserInterop.INameSpaceTreeControlEvents
{
    private const uint TVM_SETITEMHEIGHT = 0x111B;
    private const uint TVM_SETBKCOLOR = 0x111D;
    private const uint TVM_SETTEXTCOLOR = 0x111E;

    private readonly Panel _hostPanel;
    private readonly bool _isDarkMode;
    private readonly Color _bgColor;
    private readonly Color _fgColor;

    private ShellBrowserInterop.INameSpaceTreeControl? _nstc;
    private IntPtr _nstcHwnd;
    private IntPtr _sysTreeHwnd;
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
        Color borderColor = isDarkMode ? Color.FromArgb(56, 56, 62) : Color.FromArgb(218, 218, 224);

        Dock = DockStyle.Fill;
        BackColor = _bgColor;
        Padding = Padding.Empty;
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
        // 1. Home / Quick Access (expanded to show pinned Quick Access folders with pin icons)
        AppendShellRoot(
            ExplorerDetector.HomeShellGuid,
            ShellBrowserInterop.SHCONTF_FOLDERS,
            ShellBrowserInterop.NSTCRS_VISIBLE | ShellBrowserInterop.NSTCRS_EXPANDED);

        // 2. OneDrive (if present)
        string? oneDrivePath = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrivePath) && Directory.Exists(oneDrivePath))
        {
            AppendShellRoot(
                oneDrivePath,
                ShellBrowserInterop.SHCONTF_FOLDERS,
                ShellBrowserInterop.NSTCRS_VISIBLE);
        }

        // 3. This PC (expanded to show Desktop, Downloads, Documents, Pictures, Music, Videos, and Drives)
        AppendShellRoot(
            "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            ShellBrowserInterop.SHCONTF_FOLDERS,
            ShellBrowserInterop.NSTCRS_VISIBLE | ShellBrowserInterop.NSTCRS_EXPANDED);

        // 4. Network
        AppendShellRoot(
            "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}",
            ShellBrowserInterop.SHCONTF_FOLDERS,
            ShellBrowserInterop.NSTCRS_VISIBLE);

        _nstc.TreeAdvise(this, out _eventsCookie);
        SyncTreeBounds();
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
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnGetToolTip(ShellBrowserInterop.IShellItem psi, IntPtr pszTip, int cchTip) => 1;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeforeItemDelete(ShellBrowserInterop.IShellItem psi) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnItemAdded(ShellBrowserInterop.IShellItem psi, bool fIsRoot) => 0;
    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnItemDeleted(ShellBrowserInterop.IShellItem psi, bool fIsRoot) => 0;

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeforeContextMenu(
        ShellBrowserInterop.IShellItem? psi,
        ref Guid riid,
        out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return 1;
    }

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnAfterContextMenu(
        ShellBrowserInterop.IShellItem? psi,
        IntPtr pcmIn,
        ref Guid riid,
        out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return 1;
    }

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnBeforeStateImageChange(ShellBrowserInterop.IShellItem psi) => 0;

    int ShellBrowserInterop.INameSpaceTreeControlEvents.OnGetDefaultIconIndex(
        ShellBrowserInterop.IShellItem psi,
        out int piDefaultIcon,
        out int piOpenIcon)
    {
        piDefaultIcon = -1;
        piOpenIcon = -1;
        return 1;
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
            DestroyTreeControl();
        }

        base.Dispose(disposing);
    }
}
