using System.Runtime.InteropServices;
using System.Text;
using Explorer2.Core;

namespace Explorer2.UI;

/// <summary>
/// Adds "Pin to Favorites" (or "Unpin from Favorites") to the right-click menu of a hosted file view.
/// The view builds and shows that menu itself, so the line is put in as the menu opens (the view
/// gets WM_INITMENUPOPUP for it). It has command id 0, which the view treats as "nothing chosen";
/// a message filter on the menu notices when the user picks it, and it runs once the menu is closed.
/// </summary>
internal sealed class FileViewFavoritesMenuHook : NativeWindow
{
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_UNINITMENUPOPUP = 0x0125;
    private const int WM_EXITMENULOOP = 0x0212;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WH_MSGFILTER = -1;
    private const int MSGF_MENU = 2;
    private const uint MF_BYPOSITION = 0x0400;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_HILITE = 0x0080;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_DRAWITEM = 0x002B;
    private const uint MIIM_ID = 0x0002;
    private const uint MIIM_DATA = 0x0020;
    private const uint MIIM_STRING = 0x0040;
    private const uint MIIM_FTYPE = 0x0100;
    private const uint MFT_OWNERDRAW = 0x0100;

    /// <summary>Marks our line in the owner-draw messages ("E2FV").</summary>
    private static readonly IntPtr OurItemData = new(0x45324656);

    private string _label = string.Empty;

    private readonly Func<List<string>> _getSelection;
    private readonly HookProc _hookProc;
    private IntPtr _menu;
    private int _itemPosition = -1;
    private List<string> _paths = new();
    private bool _remove;
    private bool _chosen;
    private IntPtr _hook;

    public FileViewFavoritesMenuHook(IntPtr viewHwnd, Func<List<string>> getSelection)
    {
        _getSelection = getSelection;
        _hookProc = MessageFilter;
        AssignHandle(viewHwnd);
    }

    protected override void WndProc(ref Message m)
    {
        // Our line is drawn here, like the view draws its own lines (it owner-draws the whole menu)
        if (m.Msg == WM_MEASUREITEM && m.LParam != IntPtr.Zero && Marshal.ReadIntPtr(m.LParam, 24) == OurItemData)
        {
            MeasureItem(m.LParam);
            m.Result = new IntPtr(1);
            return;
        }

        if (m.Msg == WM_DRAWITEM && m.LParam != IntPtr.Zero && Marshal.ReadIntPtr(m.LParam, 56) == OurItemData)
        {
            DrawItem(m.LParam);
            m.Result = new IntPtr(1);
            return;
        }

        switch (m.Msg)
        {
            case WM_INITMENUPOPUP:
                base.WndProc(ref m);
                // The first popup (not a submenu like "Send to", not the window menu) is the item menu
                if (_menu == IntPtr.Zero && ((long)m.LParam >> 16 & 0xFFFF) == 0 && AppSettingsFavorites.Enabled)
                {
                    AddItem(m.WParam);
                }

                return;
            case WM_UNINITMENUPOPUP when m.WParam == _menu && _menu != IntPtr.Zero:
            case WM_EXITMENULOOP when _menu != IntPtr.Zero:
                base.WndProc(ref m);
                Finish();
                return;
        }

        base.WndProc(ref m);
    }

    private void AddItem(IntPtr menu)
    {
        List<string> paths;
        try
        {
            paths = _getSelection();
        }
        catch
        {
            return;
        }

        if (paths.Count == 0)
        {
            return; // the folder background's menu
        }

        _menu = menu;
        _paths = paths;
        _remove = paths.All(Favorites.ContainsAtTopLevel);
        _chosen = false;

        int count = GetMenuItemCount(menu);
        int position = -1;
        int firstSeparator = -1;
        StringBuilder text = new(256);
        for (int i = 0; i < count; i++)
        {
            if ((GetMenuState(menu, (uint)i, MF_BYPOSITION) & MF_SEPARATOR) != 0)
            {
                if (firstSeparator < 0)
                {
                    firstSeparator = i;
                }

                continue;
            }

            text.Clear();
            GetMenuString(menu, (uint)i, text, text.Capacity, MF_BYPOSITION);
            if (text.ToString().Contains("Quick access", StringComparison.OrdinalIgnoreCase))
            {
                position = i + 1; // next to "Pin to Quick access", as File Explorer has its pin commands
                break;
            }
        }

        if (position < 0)
        {
            position = firstSeparator >= 0 ? firstSeparator : count;
        }

        // Owner-drawn like the view's own lines when they are (Windows 11), else a plain line
        _label = _remove ? "Unpin from Favorites" : "Pin to Favorites";
        MENUITEMINFO neighbor = new() { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_FTYPE };
        bool ownerDrawn = count > 0 && GetMenuItemInfo(menu, 0, true, ref neighbor) && (neighbor.fType & MFT_OWNERDRAW) != 0;
        IntPtr labelText = Marshal.StringToHGlobalUni(_label);
        try
        {
            MENUITEMINFO item = new()
            {
                cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MIIM_FTYPE | MIIM_ID | MIIM_STRING | MIIM_DATA,
                fType = ownerDrawn ? MFT_OWNERDRAW : 0,
                wID = 0,
                dwItemData = ownerDrawn ? OurItemData : IntPtr.Zero,
                dwTypeData = labelText,
                cch = (uint)_label.Length
            };

            if (!InsertMenuItem(menu, (uint)position, true, ref item))
            {
                _menu = IntPtr.Zero;
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(labelText);
        }

        _itemPosition = position;
        _hook = SetWindowsHookEx(WH_MSGFILTER, _hookProc, IntPtr.Zero, GetCurrentThreadId());
    }

    // Measured on the view's own lines at 100%: 24 px high, text 31 px in, rounded hover highlight
    private int Px(int value) => (int)Math.Round(value * (Handle != IntPtr.Zero ? GetDpiForWindow(Handle) : 96) / 96.0);

    private static Font MenuFont => SystemFonts.MenuFont ?? new Font("Segoe UI", 9f);

    private void MeasureItem(IntPtr measure)
    {
        // MEASUREITEMSTRUCT: CtlType, CtlID, itemID, itemWidth (12), itemHeight (16), itemData (24)
        int width = TextRenderer.MeasureText(_label, MenuFont).Width + Px(35 + 40);
        Marshal.WriteInt32(measure, 12, width);
        Marshal.WriteInt32(measure, 16, Px(24));
    }

    private void DrawItem(IntPtr draw)
    {
        // DRAWITEMSTRUCT: itemState (16), hDC (32), rcItem (40)
        int state = Marshal.ReadInt32(draw, 16);
        IntPtr hdc = Marshal.ReadIntPtr(draw, 32);
        Rectangle rc = Rectangle.FromLTRB(Marshal.ReadInt32(draw, 40), Marshal.ReadInt32(draw, 44),
            Marshal.ReadInt32(draw, 48), Marshal.ReadInt32(draw, 52));
        bool hot = (state & 0x0001) != 0; // ODS_SELECTED

        // The menu's own background, read just left of the line (dark or light menu)
        uint pixel = GetPixel(hdc, Math.Max(0, rc.Left - 1), rc.Top + rc.Height / 2);
        Color back = pixel == 0xFFFFFFFF
            ? (Configuration.AppTheme.IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249))
            : ColorTranslator.FromWin32((int)pixel);
        bool dark = back.GetBrightness() < 0.5f;
        Color text = dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(0, 0, 0);
        Color highlight = dark
            ? Color.FromArgb(Math.Min(255, back.R + 20), Math.Min(255, back.G + 20), Math.Min(255, back.B + 20))
            : Color.FromArgb(Math.Max(0, back.R - 9), Math.Max(0, back.G - 9), Math.Max(0, back.B - 9));

        using Graphics g = Graphics.FromHdc(hdc);
        using (SolidBrush fill = new(back))
        {
            g.FillRectangle(fill, rc);
        }

        if (hot)
        {
            Rectangle r = Rectangle.FromLTRB(rc.Left + Px(1), rc.Top, rc.Right - Px(3), rc.Bottom - 1);
            int d = Px(8);
            using System.Drawing.Drawing2D.GraphicsPath path = new();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using SolidBrush fill = new(highlight);
            g.FillPath(fill, path);
        }

        Rectangle textRect = Rectangle.FromLTRB(rc.Left + Px(31), rc.Top, rc.Right - Px(8), rc.Bottom);
        TextRenderer.DrawText(g, _label, MenuFont, textRect, text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private IntPtr MessageFilter(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == MSGF_MENU && _menu != IntPtr.Zero && lParam != IntPtr.Zero)
        {
            MSG msg = Marshal.PtrToStructure<MSG>(lParam);
            bool onItem = (GetMenuState(_menu, (uint)_itemPosition, MF_BYPOSITION) & MF_HILITE) != 0;
            if (onItem && msg.message is WM_LBUTTONUP or WM_RBUTTONUP)
            {
                onItem = GetMenuItemRect(IntPtr.Zero, _menu, (uint)_itemPosition, out RECT rect) &&
                         msg.pt.X >= rect.Left && msg.pt.X < rect.Right && msg.pt.Y >= rect.Top && msg.pt.Y < rect.Bottom;
                _chosen |= onItem;
            }
            else if (onItem && msg.message == WM_KEYDOWN && msg.wParam.ToInt32() == 0x0D /* Enter */)
            {
                _chosen = true;
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Finish()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        bool chosen = _chosen;
        bool remove = _remove;
        List<string> paths = _paths;
        _menu = IntPtr.Zero;
        _itemPosition = -1;
        _chosen = false;

        if (chosen)
        {
            SynchronizationContext.Current?.Post(_ =>
            {
                if (remove)
                {
                    foreach (string path in paths)
                    {
                        Favorites.RemovePath(path);
                    }
                }
                else
                {
                    Favorites.AddPaths(paths);
                }
            }, null);
        }
    }

    public void Detach()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero)
        {
            ReleaseHandle();
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenuItem(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr hMenu, uint uId, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int cchMax, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMenuItemRect(IntPtr hWnd, IntPtr hMenu, uint uItem, out RECT lprcItem);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

/// <summary>The Favorites section setting, read once per process (new windows follow a change).</summary>
internal static class AppSettingsFavorites
{
    public static bool Enabled => Configuration.AppSettings.FavoritesSectionEnabled;
}

/// <summary>IContextMenu with the command info passed as a raw pointer (its verb can be a number).</summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214E4-0000-0000-C000-000000000046")]
internal interface IContextMenuRaw
{
    [PreserveSig]
    int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

    [PreserveSig]
    int InvokeCommand(IntPtr pici);

    [PreserveSig]
    int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
}

/// <summary>The "Pin to Favorites" / "Unpin from Favorites" line the folder tree adds to a folder's menu.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class FavoritesTreeMenu : IContextMenuRaw
{
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint CMF_DEFAULTONLY = 0x0001;
    private const uint MF_BYPOSITION = 0x0400;
    private const uint MF_STRING = 0x0000;

    private readonly string _path;
    private readonly bool _remove;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public FavoritesTreeMenu(string path)
    {
        _path = path;
        _remove = Favorites.ContainsAtTopLevel(path);
    }

    public int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags)
    {
        if ((uFlags & CMF_DEFAULTONLY) != 0 || idCmdFirst > idCmdLast)
        {
            return 0;
        }

        InsertMenu(hmenu, indexMenu, MF_BYPOSITION | MF_STRING, (UIntPtr)idCmdFirst,
            _remove ? "Unpin from Favorites" : "Pin to Favorites");
        return 1; // one command used
    }

    public int InvokeCommand(IntPtr pici)
    {
        if (pici == IntPtr.Zero)
        {
            return E_FAIL;
        }

        // CMINVOKECOMMANDINFO: cbSize, fMask, hwnd, lpVerb (a command offset when its high word is 0)
        IntPtr verb = Marshal.ReadIntPtr(pici, 8 + IntPtr.Size);
        if ((verb.ToInt64() >> 16) != 0 || (verb.ToInt64() & 0xFFFF) != 0)
        {
            return E_FAIL;
        }

        void Run()
        {
            if (_remove)
            {
                Favorites.RemovePath(_path);
            }
            else
            {
                Favorites.AddPaths(new[] { _path });
            }
        }

        if (_uiContext is not null)
        {
            _uiContext.Post(_ => Run(), null);
        }
        else
        {
            Run();
        }

        return 0;
    }

    public int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax) => E_NOTIMPL;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);
}
