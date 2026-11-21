using System.Runtime.InteropServices;

namespace Explorer2.UI;

/// <summary>
/// Native window (system) menu for the custom title bar: opened by clicking the app icon,
/// with an extra "About Explorer2" item (also available via Alt+Space).
/// </summary>
internal sealed partial class DualPaneExplorerForm
{
    private const int WmSysCommand = 0x0112;
    private const int SysMenuAboutId = 0x1010;    // custom SC_ id: must be < 0xF000 with the low 4 bits clear
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;

    private void AddAboutToSystemMenu()
    {
        IntPtr menu = GetSystemMenu(Handle, false);
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
        AppendMenu(menu, MfString, (UIntPtr)SysMenuAboutId, "About Explorer2");
    }

    /// <summary>
    /// Shows the native window menu at a screen point with the same item states Windows would set
    /// (Restore only when maximized, Move/Size/Maximize only when not), then runs the chosen command.
    /// </summary>
    private void ShowSystemMenu(Point screenPoint)
    {
        IntPtr menu = GetSystemMenu(Handle, false);
        if (menu == IntPtr.Zero)
        {
            return;
        }

        bool maximized = WindowState == FormWindowState.Maximized;
        bool minimized = WindowState == FormWindowState.Minimized;
        EnableMenuItem(menu, ScRestore, maximized || minimized ? MfEnabled : MfGrayed);
        EnableMenuItem(menu, ScMove, maximized ? MfGrayed : MfEnabled);
        EnableMenuItem(menu, ScSize, maximized ? MfGrayed : MfEnabled);
        EnableMenuItem(menu, ScMinimize, minimized ? MfGrayed : MfEnabled);
        EnableMenuItem(menu, ScMaximize, maximized ? MfGrayed : MfEnabled);
        EnableMenuItem(menu, ScClose, MfEnabled);
        SetMenuDefaultItem(menu, ScClose, 0);

        int command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmLeftAlign | TpmTopAlign, screenPoint.X, screenPoint.Y, Handle, IntPtr.Zero);
        if (command != 0)
        {
            Interop.NativeMethods.PostMessage(Handle, WmSysCommand, (IntPtr)command, IntPtr.Zero);
        }
    }

    private const uint ScSize = 0xF000;
    private const uint ScMove = 0xF010;
    private const uint ScMinimize = 0xF020;
    private const uint ScMaximize = 0xF030;
    private const uint ScClose = 0xF060;
    private const uint ScRestore = 0xF120;
    private const uint MfEnabled = 0x0000;
    private const uint MfGrayed = 0x0001;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmReturnCmd = 0x0100;

    [DllImport("user32.dll")]
    private static extern int EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    private void ShowAboutDialog()
    {
        using AboutDialog dialog = new(_isDarkMode, _windowIcon);
        dialog.ShowDialog(this);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
}
