using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// What the guided tour (UI\Tour) drives: it calls these instead of sending real keys or mouse
/// input, so nothing it does can reach another program.
/// </summary>
internal sealed partial class DualPaneExplorerForm
{
    // Set while the tour presses a key: the key is handled as if typed in this window with these modifiers
    private Keys? _tourKeyModifiers;

    internal ExplorerTabInstance? TourActiveTab => _selectedPane.ActiveTab;

    internal bool TourIsSplit => _isSplitEnabled;

    internal bool TourRightPaneActive => _selectedPane == _rightFilesPane;

    /// <summary>
    /// Presses a key in the window, as typed: Explorer2's own keys run their command, other keys go
    /// to the focused file list (or the active pane's file list).
    /// </summary>
    internal void TourKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        IntPtr focus = NativeMethods.GetFocus();
        IntPtr target = focus != IntPtr.Zero && NativeMethods.IsChild(Handle, focus)
            ? focus
            : _selectedPane.ActiveTab?.FileListHandle ?? IntPtr.Zero;
        if (target == IntPtr.Zero)
        {
            return;
        }

        bool handled;
        _tourKeyModifiers = keyData & Keys.Modifiers;
        try
        {
            Message m = Message.Create(target, NativeMethods.WM_KEYDOWN, (IntPtr)(int)key, (IntPtr)1);
            handled = PreFilterMessage(ref m);
        }
        finally
        {
            _tourKeyModifiers = null;
        }

        if (!handled && (keyData & Keys.Modifiers) == Keys.None)
        {
            // Not one of Explorer2's keys (an arrow, Home…): the file list moves its cursor itself
            NativeMethods.PostMessage(target, NativeMethods.WM_KEYDOWN, (IntPtr)(int)key, (IntPtr)1);
            NativeMethods.PostMessage(target, NativeMethods.WM_KEYUP, (IntPtr)(int)key, unchecked((IntPtr)(int)0xC0000001));
        }
    }

    /// <summary>Screen rectangle of the left or right files pane.</summary>
    internal Rectangle TourPaneRect(bool right)
    {
        TabbedExplorerPane pane = right ? _rightFilesPane : _leftFilesPane;
        return pane.Visible && pane.IsHandleCreated ? pane.RectangleToScreen(pane.ClientRectangle) : Rectangle.Empty;
    }

    internal void TourSelectPane(bool right)
    {
        TabbedExplorerPane pane = right && _isSplitEnabled ? _rightFilesPane : _leftFilesPane;
        SetSelectedPane(pane, moveTreeHighlight: false);
        pane.ActiveTab?.FocusFileListView();
    }

    internal void TourSetSplit(bool on)
    {
        if (_isSplitEnabled != on)
        {
            ToggleDualPaneSplit();
        }
    }

    /// <summary>The control the accessibility name names (the command bar's icon buttons), or null.</summary>
    internal Control? TourControl(string accessibleName) => FindControl(this, accessibleName);

    private static Control? FindControl(Control parent, string accessibleName)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Visible && child.AccessibleName == accessibleName)
            {
                return child;
            }

            if (FindControl(child, accessibleName) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Opens the command bar's "..." menu, as a click on it does.</summary>
    internal void TourShowMoreMenu()
    {
        if (TourControl("See more") is { } button)
        {
            ShowMoreMenu(button);
        }
    }

    /// <summary>The Favorites list in the navigation pane, or null when it isn't shown.</summary>
    internal FavoritesView? TourFavoritesView => FindFavoritesView(this);

    private static FavoritesView? FindFavoritesView(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is FavoritesView { Flyout: null } view && view.Visible)
            {
                return view;
            }

            if (FindFavoritesView(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    internal Rectangle TourFunctionKeyRect(Keys key) =>
        _functionKeyBar.Visible ? _functionKeyBar.ButtonScreenRect(key) : Rectangle.Empty;

    internal Rectangle TourFunctionKeyBarRect =>
        _functionKeyBar.Visible ? _functionKeyBar.RectangleToScreen(_functionKeyBar.ClientRectangle) : Rectangle.Empty;

    /// <summary>Runs a function key's button, as a click on it does. Posted, since F5 opens a dialog.</summary>
    internal void TourFunctionKey(Keys key) => BeginInvoke(() =>
    {
        RunCommanderFunctionKey(key, Keys.None);
        _selectedPane?.ActiveTab?.FocusFileListView();
    });
}
