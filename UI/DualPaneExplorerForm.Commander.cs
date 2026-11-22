using System.Diagnostics;
using System.Media;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// Total Commander style pane behaviour. In both modes Space / Insert toggle the focused item's
/// selection and move down, Num * inverts the selection, and cursor keys keep the marks. In split
/// mode also F3 View, F4 Edit, F5 Copy, F6 Move, F7 New Folder, F8 Delete (also as buttons in the
/// status bar). Backspace goes up one folder in both modes.
/// </summary>
internal sealed partial class DualPaneExplorerForm
{
    private void ApplyCommanderMode()
    {
        _functionKeyBar.Visible = _isSplitEnabled;
        LayoutFunctionKeyBar();
    }

    /// <summary>
    /// A folder you just entered (from the file list, the navigation pane or the address bar) gets
    /// the cursor on its first item, so Enter / Space / arrows work immediately, in both modes
    /// (going up, back or forward keeps the shell's own selection).
    /// </summary>
    private void OnActiveTabNavigated(TabbedExplorerPane pane, ExplorerTabInstance tab)
    {
        // Not for search results, and not while the user is typing in the search / address box:
        // putting the cursor on an item would pull keyboard focus out of the text box.
        if (!tab.LastNavigationRestoresSelection && !tab.IsShowingSearch &&
            !IsTextEditControlFocused(NativeMethods.GetFocus()))
        {
            tab.SelectFirstItemWhenReady();
        }
    }

    // Native window that had keyboard focus when this window was deactivated.
    private IntPtr _focusBeforeDeactivate;

    protected override void OnDeactivate(EventArgs e)
    {
        // WM_ACTIVATE(inactive) arrives before WM_KILLFOCUS, so focus is still ours here.
        IntPtr focused = NativeMethods.GetFocus();
        _focusBeforeDeactivate = focused != IntPtr.Zero && IsHandleCreated && NativeMethods.IsChild(Handle, focused)
            ? focused
            : IntPtr.Zero;
        base.OnDeactivate(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // WinForms restores focus to its own last ActiveControl, which for the native file list is
        // the pane divider (SplitContainer). Put focus back where it really was — normally the
        // active tab's file list — after WinForms has finished.
        IntPtr previous = _focusBeforeDeactivate;
        BeginInvoke(() =>
        {
            if (IsDisposed || Form.ActiveForm != this)
            {
                return;
            }

            if (previous != IntPtr.Zero && NativeMethods.IsWindow(previous) && NativeMethods.IsChild(Handle, previous))
            {
                NativeMethods.SetFocus(previous);
            }
            else if (ActiveControl is not TextBoxBase)
            {
                _selectedPane?.ActiveTab?.FocusFileListView();
            }
        });
    }

    /// <summary>
    /// The split toggle hotkey (configurable from the tray menu, Ctrl+Shift+S by default) handled
    /// inside the window, so it also works for windows not managed by the tray process.
    /// </summary>
    private static bool IsSplitToggleHotkey(Keys keyCode, Keys modifiers)
    {
        if (modifiers == Keys.None || keyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu)
        {
            return false;
        }

        Configuration.AppSettings settings = Configuration.AppSettings.Load();
        uint mods = 0;
        if ((modifiers & Keys.Control) != 0) mods |= NativeMethods.MOD_CONTROL;
        if ((modifiers & Keys.Alt) != 0) mods |= NativeMethods.MOD_ALT;
        if ((modifiers & Keys.Shift) != 0) mods |= NativeMethods.MOD_SHIFT;

        uint wanted = settings.HotkeyModifiers & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT);
        return keyCode == settings.HotkeyKey && mods == wanted && (settings.HotkeyModifiers & NativeMethods.MOD_WIN) == 0;
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

    /// <summary>Handles a key press in split mode. Returns true when the key was consumed.</summary>
    private bool HandleCommanderKey(IntPtr hwnd, Keys keyCode, Keys modifiers)
    {
        TabbedExplorerPane pane = _selectedPane;
        ExplorerTabInstance? tab = pane.ActiveTab;
        if (tab is null)
        {
            return false;
        }

        // Function keys (split mode only) work wherever focus is in the window (except text fields).
        if (_isSplitEnabled && modifiers == Keys.None && keyCode is >= Keys.F3 and <= Keys.F8)
        {
            RunCommanderFunctionKey(keyCode, modifiers);
            return true;
        }

        if (_isSplitEnabled && modifiers == Keys.Shift && keyCode == Keys.F3)
        {
            CommanderOpenWithDefaultApp(tab);
            return true;
        }

        if (_isSplitEnabled && modifiers == Keys.Shift && keyCode == Keys.F6)
        {
            tab.InvokeShellVerb("rename");
            return true;
        }

        // Marking keys below work in both modes, in the file list only.

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

        // Total Commander: once items are marked, cursor keys move only the cursor and keep the
        // marks (the shell view would otherwise select the item under the cursor and drop the rest).
        if (modifiers == Keys.None &&
            keyCode is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End &&
            tab.HasCommanderMarks)
        {
            int steps = keyCode switch
            {
                Keys.Up => -1,
                Keys.Down => 1,
                Keys.PageUp => -tab.EstimatedPageRows,
                Keys.PageDown => tab.EstimatedPageRows,
                Keys.Home => int.MinValue,
                _ => int.MaxValue
            };
            tab.MoveCursorKeepingMarks(steps);
            return true;
        }

        return false;
    }

    private static void ToggleSelectionAndAdvance(ExplorerTabInstance tab) =>
        tab.ToggleFocusedSelectionAndAdvance();

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

    /// <summary>
    /// F3: open the quick viewer (Total Commander's Lister) on the selected file; N / P in the viewer
    /// step through the folder's files in display order. A single selected folder is entered instead.
    /// </summary>
    private void CommanderView(ExplorerTabInstance tab)
    {
        List<string> selected = tab.GetSelectedItemPaths();
        if (selected.Count == 0)
        {
            SystemSounds.Beep.Play();
            return;
        }

        if (selected.Count == 1 && Directory.Exists(selected[0]))
        {
            tab.NavigateToPath(selected[0]);
            return;
        }

        string? first = selected.FirstOrDefault(File.Exists);
        if (first is null)
        {
            SystemSounds.Beep.Play();
            return;
        }

        List<string> files = tab.GetAllItemPathsInViewOrder().Where(File.Exists).ToList();
        int index = files.FindIndex(p => string.Equals(p, first, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            files = selected.Where(File.Exists).ToList();
            index = 0;
        }

        QuickViewerForm viewer = new(files, index, _isDarkMode, _windowIcon, this);
        viewer.FormClosed += (_, _) =>
        {
            if (!IsDisposed)
            {
                Activate();
                tab.FocusFileListView();
            }
        };
        viewer.Show(this);
    }

    /// <summary>Shift+F3: open the selection with its default app (a single folder is entered).</summary>
    private void CommanderOpenWithDefaultApp(ExplorerTabInstance tab)
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
