using System.Diagnostics;
using System.Media;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// Total Commander style pane behaviour, active in split mode:
/// Space / Insert toggle the focused item's selection and move down, Num * inverts the selection,
/// a "[..]" row at the top of each pane goes up one folder (reachable with Up from the first item,
/// Home, or the mouse), and F3 View, F4 Edit, F5 Copy, F6 Move, F7 New Folder, F8 Delete
/// (also on a function key bar above the status bar).
/// </summary>
internal sealed partial class DualPaneExplorerForm
{
    private void ApplyCommanderMode()
    {
        _functionKeyBar.Visible = _isSplitEnabled;
        LayoutFunctionKeyBar();
        _leftFilesPane.SetCommanderMode(_isSplitEnabled);
        _rightFilesPane.SetCommanderMode(_isSplitEnabled);
    }

    /// <summary>
    /// In split mode, a folder you just entered gets the cursor on its first item, so Enter / Space /
    /// arrows work immediately (going up, back or forward keeps the shell's own selection).
    /// </summary>
    private void OnActiveTabNavigated(TabbedExplorerPane pane, ExplorerTabInstance tab)
    {
        if (_isSplitEnabled && !tab.LastNavigationRestoresSelection)
        {
            tab.SelectFirstItemWhenReady();
        }
    }

    private void OnParentRowClicked(TabbedExplorerPane pane)
    {
        SetSelectedPane(pane);
        pane.ActiveTab?.FocusFileListView();
        pane.ParentRow.IsCursorOn = true;
    }

    /// <summary>Called for mouse clicks: a click anywhere other than a [..] row takes its cursor off.</summary>
    private void ClearParentRowCursorsForClick(IntPtr clickedHwnd)
    {
        foreach (TabbedExplorerPane pane in new[] { _leftFilesPane, _rightFilesPane })
        {
            ParentFolderRow row = pane.ParentRow;
            if (row.IsCursorOn && (!row.IsHandleCreated || clickedHwnd != row.Handle))
            {
                row.IsCursorOn = false;
            }
        }
    }


    /// <summary>
    /// Centers the function key buttons in the status bar, keeping clear of the item counts on the
    /// left and the view buttons on the right.
    /// </summary>
    private void LayoutFunctionKeyBar()
    {
        if (!_functionKeyBar.Visible || _statusBarPanel.Width <= 0)
        {
            return;
        }

        int height = Math.Max(16, _statusBarPanel.Height - 4);
        int width = _functionKeyBar.PreferredBarWidth;
        int minLeft = _statusLeftContentRight + 12;
        int maxRight = (_statusDetailsBtnBounds.Width > 0 ? _statusDetailsBtnBounds.Left : _statusBarPanel.Width - 70) - 12;

        int x = Math.Max(minLeft, (_statusBarPanel.Width - width) / 2);
        int available = Math.Max(0, maxRight - x);
        Rectangle bounds = new(x, (_statusBarPanel.Height - height) / 2 + 1, Math.Min(width, available), height);

        if (_functionKeyBar.Bounds != bounds)
        {
            _functionKeyBar.Bounds = bounds;
        }
    }

    /// <summary>Called by <see cref="CommanderKeyClaim"/>: should this window take a plain F6 press?</summary>
    internal bool WantsClaimedF6() =>
        _isSplitEnabled && !IsTextEditControlFocused(NativeMethods.GetFocus());

    /// <summary>F6 delivered by <see cref="CommanderKeyClaim"/> (it never reaches the message loop).</summary>
    internal void OnClaimedF6()
    {
        if (!IsDisposed && _isSplitEnabled)
        {
            RunCommanderFunctionKey(Keys.F6, Keys.None);
        }
    }

    private TabbedExplorerPane OtherPane(TabbedExplorerPane pane) =>
        pane == _leftFilesPane ? _rightFilesPane : _leftFilesPane;

    private void GoUpFromParentRow(TabbedExplorerPane pane)
    {
        SetSelectedPane(pane);
        ExplorerTabInstance? tab = pane.ActiveTab;
        if (tab is null)
        {
            return;
        }

        // Return keyboard focus to the file list once the parent folder has loaded (the shell
        // places the cursor on the folder we came from, as Total Commander does).
        void OnArrived(ExplorerTabInstance arrived)
        {
            arrived.NavigationCompleted -= OnArrived;
            if (!IsDisposed)
            {
                BeginInvoke(arrived.FocusFileListView);
            }
        }

        tab.NavigationCompleted += OnArrived;
        tab.NavigateRelative(ShellBrowserInterop.SBSP_PARENT);
    }

    /// <summary>Handles a key press in split mode. Returns true when the key was consumed.</summary>
    private bool HandleCommanderKey(IntPtr hwnd, Keys keyCode, Keys modifiers)
    {
        TabbedExplorerPane pane = _selectedPane;
        ExplorerTabInstance? tab = pane.ActiveTab;
        if (tab is null)
        {
            return false;
        }

        ParentFolderRow parentRow = pane.ParentRow;
        bool onParentRow = parentRow.Visible && parentRow.IsCursorOn;

        if (onParentRow)
        {
            if (modifiers == Keys.None)
            {
                switch (keyCode)
                {
                    case Keys.Return:
                    case Keys.Back:
                        parentRow.IsCursorOn = false;
                        GoUpFromParentRow(pane);
                        return true;
                    case Keys.Down:
                    case Keys.PageDown:
                    case Keys.End:
                        parentRow.IsCursorOn = false;
                        FocusFirstListItem(tab, keyCode == Keys.End);
                        return true;
                    case Keys.Up:
                    case Keys.Home:
                    case Keys.PageUp:
                    case Keys.Space:
                    case Keys.Insert:
                        return true;
                }
            }

            // Any other key (type-to-find letters, Tab, function keys, ...) takes the cursor off [..]
            if (keyCode is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu))
            {
                parentRow.IsCursorOn = false;
            }
        }

        // Function keys work wherever focus is in the window (except text fields).
        if (modifiers == Keys.None && keyCode is >= Keys.F3 and <= Keys.F8)
        {
            RunCommanderFunctionKey(keyCode, modifiers);
            return true;
        }

        if (modifiers == Keys.Shift && keyCode == Keys.F6)
        {
            tab.InvokeShellVerb("rename");
            return true;
        }

        bool inFileList = pane.IsHandleCreated && NativeMethods.IsChild(pane.Handle, hwnd);
        if (!inFileList)
        {
            return false;
        }

        if (modifiers == Keys.None && keyCode is Keys.Space or Keys.Insert)
        {
            ToggleSelectionAndAdvance(tab);
            return true;
        }

        if (modifiers == Keys.None && keyCode == Keys.Multiply)
        {
            tab.InvertSelection();
            return true;
        }

        if (parentRow.Visible && modifiers == Keys.None)
        {
            if (keyCode == Keys.Home)
            {
                parentRow.IsCursorOn = true;
                return true;
            }

            if (keyCode == Keys.Up)
            {
                int before = tab.GetFocusedItemIndex();
                if (before < 0)
                {
                    parentRow.IsCursorOn = true;
                    return true;
                }

                // Let the view move the cursor; if it could not move (already on the first item),
                // step onto the [..] row like Total Commander.
                BeginInvoke(() =>
                {
                    if (!IsDisposed && pane.ActiveTab == tab && tab.GetFocusedItemIndex() == before)
                    {
                        parentRow.IsCursorOn = true;
                    }
                });
                return false;
            }
        }

        return false;
    }

    private static void ToggleSelectionAndAdvance(ExplorerTabInstance tab) =>
        tab.ToggleFocusedSelectionAndAdvance();

    private static void FocusFirstListItem(ExplorerTabInstance tab, bool toEnd)
    {
        tab.FocusFileListView();
        tab.FocusEdgeItem(last: toEnd);
    }

    private void RunCommanderFunctionKey(Keys key, Keys modifiers)
    {
        ExplorerTabInstance? tab = _selectedPane.ActiveTab;
        if (tab is null)
        {
            return;
        }

        switch (key)
        {
            case Keys.F3:
                CommanderView(tab);
                break;
            case Keys.F4:
                CommanderEdit(tab);
                break;
            case Keys.F5:
                CommanderTransfer(tab, FileTransfer.Operation.Copy);
                break;
            case Keys.F6:
                CommanderTransfer(tab, FileTransfer.Operation.Move);
                break;
            case Keys.F7:
                CommanderNewFolder(tab);
                break;
            case Keys.F8:
                if (tab.GetSelectedItemCount() > 0)
                {
                    tab.InvokeShellVerb("delete");
                }
                else
                {
                    SystemSounds.Beep.Play();
                }

                break;
        }
    }

    /// <summary>F3: open the selected file with its default app (a single folder is entered).</summary>
    private void CommanderView(ExplorerTabInstance tab)
    {
        List<string> paths = tab.GetSelectedItemPaths();
        if (paths.Count == 0)
        {
            SystemSounds.Beep.Play();
            return;
        }

        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            tab.NavigateToPath(paths[0]);
            return;
        }

        tab.InvokeShellVerb("open");
    }

    /// <summary>F4: open the selected files with their Edit verb, falling back to Notepad.</summary>
    private void CommanderEdit(ExplorerTabInstance tab)
    {
        List<string> files = tab.GetSelectedItemPaths().Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            SystemSounds.Beep.Play();
            return;
        }

        if (tab.InvokeShellVerb("edit"))
        {
            return;
        }

        foreach (string file in files.Take(10))
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
            }
            catch
            {
                // Ignore files Notepad cannot open
            }
        }
    }

    /// <summary>F5 / F6: confirm the destination (other pane's folder by default), then copy or move.</summary>
    private void CommanderTransfer(ExplorerTabInstance tab, FileTransfer.Operation operation)
    {
        List<string> sources = tab.GetSelectedItemPaths();
        if (sources.Count == 0)
        {
            SystemSounds.Beep.Play();
            return;
        }

        bool isCopy = operation == FileTransfer.Operation.Copy;
        string verb = isCopy ? "Copy" : "Move";
        string? otherFolder = OtherPane(_selectedPane).ActiveTab?.DisplayPath;
        string defaultTarget = !string.IsNullOrWhiteSpace(otherFolder) && Directory.Exists(otherFolder) ? otherFolder : string.Empty;
        string what = sources.Count == 1
            ? $"\"{Path.GetFileName(sources[0].TrimEnd('\\'))}\""
            : $"{sources.Count} items";

        using CommanderPromptDialog dialog = new(verb, $"{verb} {what} to:", defaultTarget, verb, _isDarkMode);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string destination = Environment.ExpandEnvironmentVariables(dialog.InputText);
        bool validFolder = Directory.Exists(destination);
        // A single item may also be copied/moved to a new name inside an existing folder.
        bool validNewName = !validFolder && sources.Count == 1 &&
                            Path.GetDirectoryName(destination) is { Length: > 0 } parent && Directory.Exists(parent);

        if (string.IsNullOrWhiteSpace(destination) || (!validFolder && !validNewName))
        {
            MessageBox.Show(this, $"The destination folder was not found:\n{destination}", verb,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        FileTransfer.Start(operation, sources, destination, Handle);
    }

    /// <summary>F7: ask for a name and create the folder in the active pane.</summary>
    private void CommanderNewFolder(ExplorerTabInstance tab)
    {
        string current = tab.DisplayPath;
        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
        {
            SystemSounds.Beep.Play();
            return;
        }

        string folderName = Path.GetFileName(current.TrimEnd('\\'));
        if (string.IsNullOrEmpty(folderName))
        {
            folderName = current;
        }

        using CommanderPromptDialog dialog = new(
            "New Folder",
            $"Create new folder in \"{folderName}\":",
            "New folder",
            "Create",
            _isDarkMode);

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string name = dialog.InputText;
        char[] invalid = Path.GetInvalidFileNameChars().Where(c => c != '\\').ToArray();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(invalid) >= 0)
        {
            MessageBox.Show(this, "That isn't a valid folder name.", "New Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string target = Path.Combine(current, name);
        if (Directory.Exists(target) || File.Exists(target))
        {
            MessageBox.Show(this, $"\"{name}\" already exists.", "New Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "New Folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
