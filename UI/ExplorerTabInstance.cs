using System.Runtime.InteropServices;
using Explorer2.Configuration;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

internal readonly record struct FolderStatusInfo(
    int TotalItems,
    int SelectedItems,
    long SelectedSizeBytes,
    bool HasSelectedFileSize);

[ComVisible(true)]
internal sealed class ExplorerTabInstance : ShellBrowserInterop.IExplorerBrowserEvents, IDisposable
{
    private ShellBrowserInterop.IExplorerBrowser? _browser;
    private ShellBrowserInterop.FOLDERSETTINGS _folderSettings;
    private uint _eventsCookie;
    private string _pendingPath;
    private string _lastNonSearchPath;
    private bool _initialized;
    private bool _disposed;

    // Shell property-bag names keep the pre-rename identifiers so per-folder view state
    // saved by earlier versions (when the app was named ExplorerSplit) is preserved.
    private const string LegacyPropertyBag = "ExplorerSplit";
    private const string LegacyPropertyBagGlobal = "ExplorerSplit_Global";

    private int _cachedSelectedCount = -1;
    private string? _cachedFirstSelectedPath;
    private long _cachedSelectedBytes;
    private bool _cachedHasSelectedFileSize;

    public event Action<ExplorerTabInstance>? NavigationCompleted;

    public ExplorerTabInstance(string initialPath, Color backColor, uint initialViewMode = ShellBrowserInterop.FVM_DETAILS)
    {
        _pendingPath = string.IsNullOrWhiteSpace(initialPath) ? ExplorerDetector.HomeShellGuid : initialPath;
        _lastNonSearchPath = _pendingPath;
        CurrentViewMode = initialViewMode;
        Title = GetShortFolderTitle(_pendingPath);
        DisplayPath = _pendingPath;
        ParsingPath = _pendingPath;
        UpdateTabIcon(_pendingPath);

        HostPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = backColor,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        HostPanel.HandleCreated += (_, _) =>
        {
            if (HostPanel.IsHandleCreated && !_disposed)
            {
                HostPanel.BeginInvoke(EnsureInitialized);
            }
        };
        HostPanel.Resize += (_, _) => SyncRect();
    }

    public Panel HostPanel { get; }
    public string Title { get; private set; }
    public string DisplayPath { get; private set; }
    public string ParsingPath { get; private set; }
    public uint CurrentViewMode { get; private set; }
    public int CurrentIconSize { get; private set; } = 16;
    public Bitmap? TabIcon { get; private set; }

    public string TabDisplayHeader =>
        !string.IsNullOrWhiteSpace(DisplayPath) &&
        !DisplayPath.StartsWith("::", StringComparison.Ordinal) &&
        !DisplayPath.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase)
            ? DisplayPath
            : Title;

    public void EnsureInitialized()
    {
        if (_disposed || _initialized || !HostPanel.IsHandleCreated)
        {
            return;
        }

        _initialized = true;

        Type? browserType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidExplorerBrowser);
        if (browserType is null)
        {
            return;
        }

        if (Activator.CreateInstance(browserType) is not ShellBrowserInterop.IExplorerBrowser browser)
        {
            return;
        }

        _browser = browser;
        _browser.SetOptions(ShellBrowserInterop.EBO_NOBORDER | ShellBrowserInterop.EBO_ALWAYSNAVIGATE);
        _browser.SetPropertyBag(AppSettings.GlobalDetailsAndNoGroupEnabled ? LegacyPropertyBagGlobal : LegacyPropertyBag);

        _folderSettings = new ShellBrowserInterop.FOLDERSETTINGS
        {
            ViewMode = CurrentViewMode,
            fFlags = ShellBrowserInterop.FWF_AUTOARRANGE |
                     ShellBrowserInterop.FWF_SHOWSELALWAYS |
                     ShellBrowserInterop.FWF_NOWEBVIEW
        };

        NativeMethods.RECT rc = new(
            0,
            0,
            Math.Max(1, HostPanel.Width),
            Math.Max(1, HostPanel.Height));

        int hr = _browser.Initialize(HostPanel.Handle, ref rc, ref _folderSettings);
        if (hr != 0)
        {
            DestroyBrowser();
            return;
        }

        _browser.Advise(this, out _eventsCookie);

        if (!NavigateToPath(_pendingPath))
        {
            NavigateToPath(ExplorerDetector.HomeShellGuid);
        }

        SyncRect();
        if (HostPanel.IsHandleCreated)
        {
            HostPanel.BeginInvoke(SyncRect);
        }
    }

    public void SyncRect() => SyncRectCore(forceNudge: false);

    public void SyncRectForced() => SyncRectCore(forceNudge: true);

    private void SyncRectCore(bool forceNudge)
    {
        if (_disposed || _browser is null || HostPanel.IsDisposed || !HostPanel.IsHandleCreated)
        {
            return;
        }

        int width = Math.Max(2, HostPanel.ClientSize.Width);
        int height = Math.Max(2, HostPanel.ClientSize.Height);

        if (forceNudge)
        {
            // Force CExplorerBrowser::SetRect past its internal EqualRect cache (same effect as dragging the splitter)
            NativeMethods.RECT nudgeRc = new(0, 0, width - 1, height);
            _browser.SetRect(IntPtr.Zero, nudgeRc);
        }

        NativeMethods.RECT rc = new(0, 0, width, height);
        _browser.SetRect(IntPtr.Zero, rc);

        Guid iidView = ShellBrowserInterop.IidIShellView;
        if (_browser.GetCurrentView(ref iidView, out object? viewObj) == 0 &&
            viewObj is ShellBrowserInterop.IShellView shellView)
        {
            try
            {
                if (shellView.GetWindow(out IntPtr viewHwnd) == 0 && viewHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(
                        viewHwnd,
                        IntPtr.Zero,
                        0,
                        0,
                        width,
                        height,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

                    IntPtr directUi = NativeMethods.FindWindowEx(viewHwnd, IntPtr.Zero, "DirectUIHWND", null);
                    if (directUi != IntPtr.Zero)
                    {
                        NativeMethods.SetWindowPos(
                            directUi,
                            IntPtr.Zero,
                            0,
                            0,
                            width,
                            height,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shellView);
            }
        }
    }

    public bool NavigateToPath(string? path)
    {
        if (_disposed)
        {
            return false;
        }

        string target = NormalizeUserInputPath(path);
        _pendingPath = target;
        if (!target.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
        {
            _lastNonSearchPath = target;
        }

        if (!_initialized)
        {
            if (HostPanel.IsHandleCreated)
            {
                EnsureInitialized();
                return true;
            }

            Title = GetShortFolderTitle(target);
            DisplayPath = target;
            ParsingPath = target;
            UpdateTabIcon(target);
            return true;
        }

        if (_browser is null)
        {
            return false;
        }

        Guid iid = ShellBrowserInterop.IidIShellItem;
        int hr = ShellBrowserInterop.SHCreateItemFromParsingName(
            target,
            IntPtr.Zero,
            ref iid,
            out ShellBrowserInterop.IShellItem? shellItem);

        if (hr != 0 || shellItem is null)
        {
            return false;
        }

        try
        {
            int navHr = _browser.BrowseToObject(shellItem, ShellBrowserInterop.SBSP_ABSOLUTE);
            if (navHr == 0)
            {
                ParsingPath = target;
                return true;
            }

            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(shellItem);
        }
    }

    public bool ExecuteSearch(string? query)
    {
        if (_disposed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return NavigateToPath(_lastNonSearchPath);
        }

        string baseFolder = !string.IsNullOrWhiteSpace(_lastNonSearchPath)
            ? _lastNonSearchPath
            : DisplayPath;

        string folderName = GetShortFolderTitle(baseFolder);
        string searchUri =
            $"search-ms:displayname={Uri.EscapeDataString($"Search Results in {folderName}")}" +
            $"&crumb=System.Generic.String%3A{Uri.EscapeDataString(query.Trim())}" +
            $"&crumb=location:{Uri.EscapeDataString(baseFolder)}";

        return NavigateToPath(searchUri);
    }

    public void NavigateRelative(uint sbspFlag)
    {
        // Going up / back / forward: the shell re-selects the item we came from, so keep that.
        _nextNavigationRestoresSelection =
            (sbspFlag & (ShellBrowserInterop.SBSP_PARENT | ShellBrowserInterop.SBSP_NAVIGATEBACK | ShellBrowserInterop.SBSP_NAVIGATEFORWARD)) != 0;
        _browser?.BrowseToIDList(IntPtr.Zero, sbspFlag);
    }

    private bool _nextNavigationRestoresSelection;

    /// <summary>
    /// True when the last completed navigation was up/back/forward, where the shell restores the
    /// selection itself. False for entering a folder (double-click, Enter, F3, address bar, ...).
    /// </summary>
    public bool LastNavigationRestoresSelection { get; private set; }

    /// <summary>
    /// Puts the cursor (and selection) on the first item once the folder has enumerated, unless
    /// something is already selected. Used in split mode so a freshly entered folder is keyboard-ready.
    /// </summary>
    public async void SelectFirstItemWhenReady()
    {
        int[] delays = { 0, 60, 120, 250, 500, 900 };
        foreach (int delay in delays)
        {
            if (delay > 0)
            {
                await Task.Delay(delay);
            }

            if (_disposed || _browser is null)
            {
                return;
            }

            if (GetSelectedItemCount() > 0)
            {
                return;
            }

            bool hasItems = false;
            WithFolderView2(folderView2 =>
                hasItems = folderView2.ItemCount(0x0002 /* SVGIO_ALLVIEW */, out int count) == 0 && count > 0);

            if (hasItems)
            {
                FocusEdgeItem(last: false);
                return;
            }
        }
    }

    private const uint SvsiDeselect = 0x0000;
    private const uint SvsiSelect = 0x0001;
    private const uint SvsiDeselectOthers = 0x0004;
    private const uint SvsiEnsureVisible = 0x0008;
    private const uint SvsiFocused = 0x0010;

    /// <summary>
    /// Total Commander Space / Insert: toggles the selection of the item under the cursor and
    /// moves the cursor to the next item (in the view's display order) without changing other marks.
    /// </summary>
    public void ToggleFocusedSelectionAndAdvance()
    {
        WithFolderView2(folderView2 =>
        {
            if (folderView2.GetFocusedItem(out int focused) != 0 || focused < 0)
            {
                // No cursor yet: put it on the first item
                if (folderView2.GetVisibleItem(-1, false, out int first) == 0 && first >= 0)
                {
                    folderView2.SelectItem(first, SvsiFocused | SvsiEnsureVisible | (IsItemSelected(folderView2, first) ? SvsiSelect : 0));
                }

                return;
            }

            folderView2.SelectItem(focused, IsItemSelected(folderView2, focused) ? SvsiDeselect : SvsiSelect);

            if (folderView2.GetVisibleItem(focused, false, out int next) == 0 && next >= 0 && next != focused)
            {
                uint keep = IsItemSelected(folderView2, next) ? SvsiSelect : 0;
                folderView2.SelectItem(next, keep | SvsiFocused | SvsiEnsureVisible);
            }
            else
            {
                // Last item: keep the cursor on it (re-assert focus after the toggle)
                uint keep = IsItemSelected(folderView2, focused) ? SvsiSelect : 0;
                folderView2.SelectItem(focused, keep | SvsiFocused | SvsiEnsureVisible);
            }

            _cachedSelectedCount = -1;
        });
    }

    /// <summary>
    /// Moves the cursor to the first or last item. With nothing marked the item is also selected
    /// (like pressing Home/End); with items marked only the cursor moves so the marks are kept.
    /// </summary>
    public void FocusEdgeItem(bool last)
    {
        WithFolderView2(folderView2 =>
        {
            if (folderView2.GetVisibleItem(-1, last, out int target) != 0 || target < 0)
            {
                return;
            }

            bool hasMarks = folderView2.ItemCount(ShellBrowserInterop.SVGIO_SELECTION, out int selCount) == 0 && selCount > 0;
            uint flags = hasMarks
                ? (IsItemSelected(folderView2, target) ? SvsiSelect : 0) | SvsiFocused | SvsiEnsureVisible
                : SvsiSelect | SvsiDeselectOthers | SvsiFocused | SvsiEnsureVisible;
            folderView2.SelectItem(target, flags);
            _cachedSelectedCount = -1;
        });
    }

    private void WithFolderView2(Action<ShellBrowserInterop.IFolderView2> action)
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        Guid iid = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) != 0 ||
            fvObj is not ShellBrowserInterop.IFolderView2 folderView2)
        {
            return;
        }

        try
        {
            action(folderView2);
        }
        catch
        {
            // Some virtual views do not support programmatic selection
        }
        finally
        {
            Marshal.ReleaseComObject(folderView2);
        }
    }

    /// <summary>Number of selected items in the view.</summary>
    public int GetSelectedItemCount()
    {
        if (_browser is null || _disposed)
        {
            return 0;
        }

        Guid iid = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) != 0 ||
            fvObj is not ShellBrowserInterop.IFolderView2 folderView2)
        {
            return 0;
        }

        try
        {
            return folderView2.ItemCount(ShellBrowserInterop.SVGIO_SELECTION, out int count) == 0 ? count : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(folderView2);
        }
    }

    public void RefreshView()
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        Guid iid = ShellBrowserInterop.IidIShellView;
        if (_browser.GetCurrentView(ref iid, out object? viewObj) == 0 &&
            viewObj is ShellBrowserInterop.IShellView shellView)
        {
            try
            {
                shellView.Refresh();
                return;
            }
            finally
            {
                Marshal.ReleaseComObject(shellView);
            }
        }

        NavigateToPath(ParsingPath);
    }

    public void SetViewMode(uint viewMode, int iconSize = 0)
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        CurrentViewMode = viewMode;
        if (iconSize > 0)
        {
            CurrentIconSize = iconSize;
        }

        _folderSettings.ViewMode = viewMode;
        _browser.SetFolderSettings(ref _folderSettings);

        Guid iid2 = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid2, out object? fv2Obj) == 0 &&
            fv2Obj is ShellBrowserInterop.IFolderView2 folderView2)
        {
            try
            {
                int targetSize = iconSize > 0 ? iconSize : -1;
                if (folderView2.SetViewModeAndIconSize(viewMode, targetSize) == 0)
                {
                    if (folderView2.GetViewModeAndIconSize(out uint actualMode, out int actualSize) == 0)
                    {
                        CurrentViewMode = actualMode;
                        CurrentIconSize = actualSize;
                    }
                    return;
                }
            }
            catch
            {
                // Fall back to IFolderView.SetCurrentViewMode
            }
            finally
            {
                Marshal.ReleaseComObject(folderView2);
            }
        }

        Guid iid = ShellBrowserInterop.IidIFolderView;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) == 0 &&
            fvObj is ShellBrowserInterop.IFolderView folderView)
        {
            try
            {
                folderView.SetCurrentViewMode(viewMode);
            }
            finally
            {
                Marshal.ReleaseComObject(folderView);
            }
        }
    }

    public void ApplySort(ShellBrowserInterop.PROPERTYKEY propKey, bool ascending)
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        Guid iid = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) == 0 &&
            fvObj is ShellBrowserInterop.IFolderView2 folderView2)
        {
            try
            {
                ShellBrowserInterop.SORTCOLUMN[] cols =
                [
                    new ShellBrowserInterop.SORTCOLUMN
                    {
                        propkey = propKey,
                        direction = ascending ? ShellBrowserInterop.SORT_ASCENDING : ShellBrowserInterop.SORT_DESCENDING
                    }
                ];
                folderView2.SetSortColumns(cols, 1);
            }
            catch
            {
                // Some virtual views do not support custom column sorting
            }
            finally
            {
                Marshal.ReleaseComObject(folderView2);
            }
        }
    }

    public void ApplyGroupBy(ShellBrowserInterop.PROPERTYKEY? propKey, bool ascending)
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        Guid iid = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) == 0 &&
            fvObj is ShellBrowserInterop.IFolderView2 folderView2)
        {
            try
            {
                ShellBrowserInterop.PROPERTYKEY key = propKey ?? default;
                folderView2.SetGroupBy(ref key, ascending);
            }
            catch
            {
                // Some virtual views do not support grouping
            }
            finally
            {
                Marshal.ReleaseComObject(folderView2);
            }
        }
    }

    public void EnforceDetailsAndNoGroup()
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        SetViewMode(ShellBrowserInterop.FVM_DETAILS, 16);
        ApplyGroupBy(null, true);
    }

    public void ReinitializeAtCurrentPath()
    {
        if (_disposed || !HostPanel.IsHandleCreated)
        {
            return;
        }

        _pendingPath = !string.IsNullOrWhiteSpace(ParsingPath)
            ? ParsingPath
            : !string.IsNullOrWhiteSpace(DisplayPath)
                ? DisplayPath
                : ExplorerDetector.HomeShellGuid;

        DestroyBrowser();
        _initialized = false;
        EnsureInitialized();
    }

    private void SyncViewModeFromBrowser()
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        Guid iid2 = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid2, out object? fv2Obj) == 0 &&
            fv2Obj is ShellBrowserInterop.IFolderView2 folderView2)
        {
            try
            {
                if (folderView2.GetViewModeAndIconSize(out uint actualMode, out int actualSize) == 0)
                {
                    CurrentViewMode = actualMode;
                    CurrentIconSize = actualSize;
                }
            }
            catch
            {
                // Ignore views that do not report mode/size
            }
            finally
            {
                Marshal.ReleaseComObject(folderView2);
            }
        }
    }

    private async Task EnforceGlobalViewAndGroupAsync()
    {
        try
        {
            await Task.Delay(80);
            if (!_disposed && AppSettings.GlobalDetailsAndNoGroupEnabled && HostPanel.IsHandleCreated)
            {
                HostPanel.BeginInvoke(() =>
                {
                    if (!_disposed && AppSettings.GlobalDetailsAndNoGroupEnabled)
                    {
                        EnforceDetailsAndNoGroup();
                    }
                });
            }
        }
        catch
        {
            // Ignore shutdown races
        }
    }

    public FolderStatusInfo GetStatusInfo()
    {
        if (_browser is null || _disposed)
        {
            return default;
        }

        Guid iid = ShellBrowserInterop.IidIFolderView;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) != 0 ||
            fvObj is not ShellBrowserInterop.IFolderView folderView)
        {
            return default;
        }

        try
        {
            folderView.ItemCount(ShellBrowserInterop.SVGIO_ALLVIEW, out int totalCount);
            folderView.ItemCount(ShellBrowserInterop.SVGIO_SELECTION, out int selectedCount);

            totalCount = Math.Max(0, totalCount);
            selectedCount = Math.Max(0, selectedCount);

            if (selectedCount == 0)
            {
                _cachedSelectedCount = 0;
                _cachedFirstSelectedPath = null;
                _cachedSelectedBytes = 0;
                _cachedHasSelectedFileSize = false;
                return new FolderStatusInfo(totalCount, 0, 0, false);
            }

            Guid iidArray = ShellBrowserInterop.IidIShellItemArray;
            if (folderView.Items(ShellBrowserInterop.SVGIO_SELECTION, ref iidArray, out object? arrayObj) == 0 &&
                arrayObj is ShellBrowserInterop.IShellItemArray itemArray)
            {
                try
                {
                    itemArray.GetCount(out uint count);
                    string? firstPath = null;
                    if (count > 0 && itemArray.GetItemAt(0, out ShellBrowserInterop.IShellItem? firstItem) == 0 && firstItem is not null)
                    {
                        try
                        {
                            firstItem.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out firstPath);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(firstItem);
                        }
                    }

                    if (selectedCount == _cachedSelectedCount &&
                        string.Equals(firstPath, _cachedFirstSelectedPath, StringComparison.Ordinal))
                    {
                        return new FolderStatusInfo(totalCount, selectedCount, _cachedSelectedBytes, _cachedHasSelectedFileSize);
                    }

                    long totalBytes = 0;
                    bool hasFileSize = false;
                    uint maxInspect = Math.Min(count, 100u);

                    for (uint i = 0; i < maxInspect; i++)
                    {
                        if (itemArray.GetItemAt(i, out ShellBrowserInterop.IShellItem? item) == 0 && item is not null)
                        {
                            try
                            {
                                if (item.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? path) == 0 &&
                                    !string.IsNullOrWhiteSpace(path) &&
                                    File.Exists(path))
                                {
                                    totalBytes += new FileInfo(path).Length;
                                    hasFileSize = true;
                                }
                            }
                            catch
                            {
                                // Ignore inaccessible files
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(item);
                            }
                        }
                    }

                    _cachedSelectedCount = selectedCount;
                    _cachedFirstSelectedPath = firstPath;
                    _cachedSelectedBytes = totalBytes;
                    _cachedHasSelectedFileSize = hasFileSize;

                    return new FolderStatusInfo(totalCount, selectedCount, totalBytes, hasFileSize);
                }
                finally
                {
                    Marshal.ReleaseComObject(itemArray);
                }
            }

            return new FolderStatusInfo(totalCount, selectedCount, 0, false);
        }
        catch
        {
            return default;
        }
        finally
        {
            Marshal.ReleaseComObject(folderView);
        }
    }

    public string? GetFirstSelectedOrCurrentPath() => GetFirstSelectedItemPath() ?? DisplayPath;

    public string? GetFirstSelectedItemPath()
    {
        List<string> paths = GetSelectedItemPaths(maxItems: 1);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>Returns the file-system paths of the selected items (non-file-system items are skipped).</summary>
    public List<string> GetSelectedItemPaths(int maxItems = int.MaxValue) =>
        GetItemPaths(ShellBrowserInterop.SVGIO_SELECTION, maxItems);

    /// <summary>File-system paths of every item in the view, in the view's display (sort) order.</summary>
    public List<string> GetAllItemPathsInViewOrder()
    {
        const uint SVGIO_ALLVIEW = 0x00000002;
        const uint SVGIO_FLAG_VIEWORDER = 0x80000000;
        return GetItemPaths(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER, int.MaxValue);
    }

    private List<string> GetItemPaths(uint svgio, int maxItems)
    {
        List<string> result = new();
        if (_browser is null || _disposed)
        {
            return result;
        }

        Guid iid = ShellBrowserInterop.IidIFolderView;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) != 0 ||
            fvObj is not ShellBrowserInterop.IFolderView folderView)
        {
            return result;
        }

        try
        {
            Guid iidArray = ShellBrowserInterop.IidIShellItemArray;
            if (folderView.Items(svgio, ref iidArray, out object? arrayObj) != 0 ||
                arrayObj is not ShellBrowserInterop.IShellItemArray itemArray)
            {
                return result;
            }

            try
            {
                if (itemArray.GetCount(out uint count) != 0)
                {
                    return result;
                }

                for (uint i = 0; i < count && result.Count < maxItems; i++)
                {
                    if (itemArray.GetItemAt(i, out ShellBrowserInterop.IShellItem? item) != 0 || item is null)
                    {
                        continue;
                    }

                    try
                    {
                        if (item.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? path) == 0 &&
                            !string.IsNullOrWhiteSpace(path))
                        {
                            result.Add(path);
                        }
                    }
                    catch
                    {
                        // Item has no file-system path
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(itemArray);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folderView);
        }

        return result;
    }

    public bool RenameSelectedItem()
    {
        if (_browser is null || _disposed)
        {
            return false;
        }

        FocusFileListView();

        Guid iid = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) == 0 &&
            fvObj is ShellBrowserInterop.IFolderView2 folderView2)
        {
            try
            {
                if (folderView2.GetFocusedItem(out int idx) != 0 || idx < 0)
                {
                    folderView2.GetSelectionMarkedItem(out idx);
                }

                if (idx >= 0)
                {
                    const uint SVSI_SELECT = 0x0001;
                    const uint SVSI_EDIT = 0x0003;
                    if (folderView2.SelectItem(idx, SVSI_SELECT | SVSI_EDIT) == 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Fall back to F2 key message
            }
            finally
            {
                Marshal.ReleaseComObject(folderView2);
            }
        }

        return InvokeShellVerb("rename");
    }

    public bool CreateNewFolder()
    {
        if (_disposed || string.IsNullOrWhiteSpace(DisplayPath) || !Directory.Exists(DisplayPath))
        {
            return false;
        }

        try
        {
            string baseName = "New folder";
            string candidate = Path.Combine(DisplayPath, baseName);
            int counter = 2;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = Path.Combine(DisplayPath, $"{baseName} ({counter})");
                counter++;
            }

            Directory.CreateDirectory(candidate);
            RefreshView();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool CreateNewTextDocument() =>
        CreateNewFileWithExtension("New Text Document", ".txt");

    public bool CreateNewBitmapImage()
    {
        using Bitmap bmp = new(1, 1);
        using MemoryStream ms = new();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        return CreateNewFileWithExtension("New Bitmap image", ".bmp", ms.ToArray());
    }

    public bool CreateNewRichTextDocument() =>
        CreateNewFileWithExtension(
            "New Rich Text Document",
            ".rtf",
            System.Text.Encoding.ASCII.GetBytes(@"{\rtf1\ansi\deff0}"));

    public bool CreateNewZipArchive()
    {
        // Standard empty 22-byte End-of-Central-Directory ZIP header
        byte[] emptyZip =
        [
            0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        return CreateNewFileWithExtension("New Compressed (zipped) Folder", ".zip", emptyZip);
    }

    public void CreateNewShortcut()
    {
        if (_disposed || string.IsNullOrWhiteSpace(DisplayPath) || !Directory.Exists(DisplayPath))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "rundll32.exe",
                $"appwiz.cpl,NewLinkHere {DisplayPath}")
            {
                UseShellExecute = false
            });
        }
        catch
        {
            // Ignore
        }
    }

    public bool CreateNewFileWithExtension(string baseName, string extension, byte[]? initialBytes = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(DisplayPath) || !Directory.Exists(DisplayPath))
        {
            return false;
        }

        try
        {
            string candidate = Path.Combine(DisplayPath, $"{baseName}{extension}");
            int counter = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate))
            {
                candidate = Path.Combine(DisplayPath, $"{baseName} ({counter}){extension}");
                counter++;
            }

            if (initialBytes is { Length: > 0 })
            {
                File.WriteAllBytes(candidate, initialBytes);
            }
            else
            {
                File.WriteAllText(candidate, string.Empty);
            }

            RefreshView();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool InvokeShellVerb(string verb, bool onBackground = false)
    {
        if (_browser is null || _disposed)
        {
            return false;
        }

        Guid iidView = ShellBrowserInterop.IidIShellView;
        if (_browser.GetCurrentView(ref iidView, out object? viewObj) != 0 ||
            viewObj is not ShellBrowserInterop.IShellView shellView)
        {
            return false;
        }

        try
        {
            uint scope = onBackground ? ShellBrowserInterop.SVGIO_BACKGROUND : ShellBrowserInterop.SVGIO_SELECTION;
            Guid iidMenu = ShellBrowserInterop.IidIContextMenu;
            if (shellView.GetItemObject(scope, ref iidMenu, out object? menuObj) != 0 ||
                menuObj is not ShellBrowserInterop.IContextMenu contextMenu)
            {
                return false;
            }

            IntPtr hMenu = NativeMethods.CreatePopupMenu();
            try
            {
                contextMenu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, ShellBrowserInterop.CMF_NORMAL);

                ShellBrowserInterop.CMINVOKECOMMANDINFO invokeInfo = new()
                {
                    cbSize = Marshal.SizeOf<ShellBrowserInterop.CMINVOKECOMMANDINFO>(),
                    // Use the top-level window: modern verbs such as Windows.ModernShare
                    // anchor their UI to a top-level HWND and fail for child windows.
                    hwnd = HostPanel.FindForm()?.Handle ?? HostPanel.Handle,
                    lpVerb = verb,
                    nShow = 1
                };

                return contextMenu.InvokeCommand(ref invokeInfo) == 0;
            }
            finally
            {
                if (hMenu != IntPtr.Zero)
                {
                    NativeMethods.DestroyMenu(hMenu);
                }

                Marshal.ReleaseComObject(contextMenu);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(shellView);
        }
    }

    public bool TranslateAccelerator(ref NativeMethods.MSG msg)
    {
        if (_browser is null || _disposed)
        {
            return false;
        }

        Guid iidView = ShellBrowserInterop.IidIShellView;
        if (_browser.GetCurrentView(ref iidView, out object? viewObj) != 0 ||
            viewObj is not ShellBrowserInterop.IShellView shellView)
        {
            return false;
        }

        try
        {
            return shellView.TranslateAccelerator(ref msg) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(shellView);
        }
    }

    public void SelectAllItems() => ModifySelection(SelectionChange.All);

    public void InvertSelection() => ModifySelection(SelectionChange.Invert);

    public void SelectNoneItems() => ModifySelection(SelectionChange.None);

    /// <summary>
    /// Undo the last shell file operation. The shell view inside IExplorerBrowser does not act on
    /// WM_COMMAND FCIDM_SHVIEW_UNDO, so focus the file list and send the native Ctrl+Z accelerator
    /// once the invoking menu has closed.
    /// </summary>
    public void UndoLastAction()
    {
        if (_browser is null || _disposed || !HostPanel.IsHandleCreated)
        {
            return;
        }

        HostPanel.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            FocusFileListView();
            SendKeys.SendWait("^z");
        });
    }

    private enum SelectionChange
    {
        All,
        None,
        Invert
    }

    // The shell view hosted by IExplorerBrowser ignores WM_COMMAND select-all/invert, so
    // selection is driven explicitly through IFolderView2.
    private void ModifySelection(SelectionChange change)
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        const uint SVSI_DESELECT = 0x0000;
        const uint SVSI_SELECT = 0x0001;
        const uint SVGIO_ALLVIEW = 0x0002;

        Guid iid = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) != 0 ||
            fvObj is not ShellBrowserInterop.IFolderView2 folderView2)
        {
            return;
        }

        try
        {
            if (folderView2.ItemCount(SVGIO_ALLVIEW, out int count) != 0 || count <= 0)
            {
                return;
            }

            folderView2.SetRedraw(false);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    uint flags = change switch
                    {
                        SelectionChange.All => SVSI_SELECT,
                        SelectionChange.None => SVSI_DESELECT,
                        _ => IsItemSelected(folderView2, i) ? SVSI_DESELECT : SVSI_SELECT
                    };

                    folderView2.SelectItem(i, flags);
                }
            }
            finally
            {
                folderView2.SetRedraw(true);
            }

            _cachedSelectedCount = -1;
        }
        catch
        {
            // Some virtual views do not support programmatic selection
        }
        finally
        {
            Marshal.ReleaseComObject(folderView2);
        }
    }

    private static bool IsItemSelected(ShellBrowserInterop.IFolderView2 folderView2, int index)
    {
        const uint SVSI_SELECT = 0x0001;
        if (folderView2.Item(index, out IntPtr pidl) != 0 || pidl == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return folderView2.GetSelectionState(pidl, out uint state) == 0 && (state & SVSI_SELECT) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    public void FocusFileListView()
    {
        if (_browser is null || _disposed)
        {
            return;
        }

        Guid iidView = ShellBrowserInterop.IidIShellView;
        if (_browser.GetCurrentView(ref iidView, out object? viewObj) != 0 ||
            viewObj is not ShellBrowserInterop.IShellView shellView)
        {
            return;
        }

        try
        {
            const uint SVUIA_ACTIVATE_FOCUS = 2;
            shellView.UIActivate(SVUIA_ACTIVATE_FOCUS);

            if (shellView.GetWindow(out IntPtr viewHwnd) == 0 && viewHwnd != IntPtr.Zero)
            {
                IntPtr directUi = NativeMethods.FindWindowEx(viewHwnd, IntPtr.Zero, "DirectUIHWND", null);
                if (directUi == IntPtr.Zero)
                {
                    directUi = NativeMethods.FindWindowEx(viewHwnd, IntPtr.Zero, "SysListView32", null);
                }

                NativeMethods.SetFocus(directUi != IntPtr.Zero ? directUi : viewHwnd);
            }
        }
        catch
        {
            // Ignore focus errors
        }
        finally
        {
            Marshal.ReleaseComObject(shellView);
        }
    }

    public static string NormalizeUserInputPath(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return ExplorerDetector.HomeShellGuid;
        }

        string trimmed = rawInput.Trim().Trim('"');
        if (trimmed.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        string expanded = Environment.ExpandEnvironmentVariables(trimmed);

        return expanded.ToLowerInvariant() switch
        {
            "home" or "quick access" => ExplorerDetector.HomeShellGuid,
            "this pc" or "computer" or "my computer" => "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            "recycle bin" => "::{645FF040-5081-101B-9F08-00AA002F954E}",
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" or "my documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => expanded
        };
    }

    private void UpdateTabIcon(string path)
    {
        NativeMethods.SHFILEINFO shfi = new();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        uint attrs = 0;

        string iconTarget = path;
        if (iconTarget.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
        {
            iconTarget = _lastNonSearchPath;
        }

        if (!iconTarget.StartsWith("::", StringComparison.Ordinal) && !Directory.Exists(iconTarget))
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            attrs = NativeMethods.FILE_ATTRIBUTE_DIRECTORY;
        }

        IntPtr res = NativeMethods.SHGetFileInfo(
            iconTarget,
            attrs,
            ref shfi,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            flags);

        if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
        {
            try
            {
                using Icon icon = Icon.FromHandle(shfi.hIcon);
                Bitmap newBmp = icon.ToBitmap();
                Bitmap? oldBmp = TabIcon;
                TabIcon = newBmp;
                oldBmp?.Dispose();
            }
            finally
            {
                NativeMethods.DestroyIcon(shfi.hIcon);
            }
        }
    }

    private static string GetShortFolderTitle(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Equals(ExplorerDetector.HomeShellGuid, StringComparison.OrdinalIgnoreCase))
        {
            return "Home";
        }

        if (path.Equals("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase))
        {
            return "This PC";
        }

        if (path.Equals("::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase))
        {
            return "Recycle Bin";
        }

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    // IExplorerBrowserEvents
    int ShellBrowserInterop.IExplorerBrowserEvents.OnNavigationPending(IntPtr pidlFolder) => 0;

    int ShellBrowserInterop.IExplorerBrowserEvents.OnViewCreated(object psv)
    {
        if (!_disposed && HostPanel.IsHandleCreated)
        {
            if (HostPanel.InvokeRequired)
            {
                HostPanel.BeginInvoke(SyncRect);
            }
            else
            {
                SyncRect();
            }
        }

        return 0;
    }

    int ShellBrowserInterop.IExplorerBrowserEvents.OnNavigationComplete(IntPtr pidlFolder)
    {
        if (_disposed || pidlFolder == IntPtr.Zero)
        {
            return 0;
        }

        Guid iid = ShellBrowserInterop.IidIShellItem;
        if (ShellBrowserInterop.SHCreateItemFromIDList(pidlFolder, ref iid, out ShellBrowserInterop.IShellItem? item) == 0 &&
            item is not null)
        {
            try
            {
                item.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? fileSystemPath);
                item.GetDisplayName(ShellBrowserInterop.SIGDN_DESKTOPABSOLUTEPARSING, out string? parsingPath);
                item.GetDisplayName(ShellBrowserInterop.SIGDN_NORMALDISPLAY, out string? normalName);

                DisplayPath = !string.IsNullOrWhiteSpace(fileSystemPath)
                    ? fileSystemPath
                    : !string.IsNullOrWhiteSpace(normalName)
                        ? normalName
                        : parsingPath ?? ExplorerDetector.HomeShellGuid;

                ParsingPath = !string.IsNullOrWhiteSpace(parsingPath)
                    ? parsingPath
                    : !string.IsNullOrWhiteSpace(fileSystemPath)
                        ? fileSystemPath
                        : ExplorerDetector.HomeShellGuid;

                if (!ParsingPath.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
                {
                    _lastNonSearchPath = !string.IsNullOrWhiteSpace(fileSystemPath)
                        ? fileSystemPath
                        : ParsingPath;
                }

                Title = !string.IsNullOrWhiteSpace(normalName)
                    ? normalName
                    : GetShortFolderTitle(DisplayPath);

                _cachedSelectedCount = -1;
                UpdateTabIcon(!string.IsNullOrWhiteSpace(fileSystemPath) ? fileSystemPath : ParsingPath);

                void Notify()
                {
                    if (!_disposed)
                    {
                        if (AppSettings.GlobalDetailsAndNoGroupEnabled)
                        {
                            EnforceDetailsAndNoGroup();
                            _ = EnforceGlobalViewAndGroupAsync();
                        }
                        else
                        {
                            SyncViewModeFromBrowser();
                        }

                        SyncRectForced();
                        LastNavigationRestoresSelection = _nextNavigationRestoresSelection;
                        _nextNavigationRestoresSelection = false;
                        NavigationCompleted?.Invoke(this);
                        _ = DelayedSyncRectAsync();
                    }
                }

                if (HostPanel.IsHandleCreated && HostPanel.InvokeRequired)
                {
                    HostPanel.BeginInvoke(Notify);
                }
                else
                {
                    Notify();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        return 0;
    }

    int ShellBrowserInterop.IExplorerBrowserEvents.OnNavigationFailed(IntPtr pidlFolder) => 0;

    private async Task DelayedSyncRectAsync()
    {
        await Task.Delay(120);
        if (!_disposed && HostPanel.IsHandleCreated && !HostPanel.IsDisposed)
        {
            SyncRectForced();
        }
    }

    private void DestroyBrowser()
    {
        if (_browser is null)
        {
            return;
        }

        try
        {
            if (_eventsCookie != 0)
            {
                _browser.Unadvise(_eventsCookie);
                _eventsCookie = 0;
            }

            _browser.Destroy();
        }
        catch
        {
            // Ignore teardown errors
        }
        finally
        {
            try
            {
                Marshal.ReleaseComObject(_browser);
            }
            catch
            {
                // Ignore
            }

            _browser = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyBrowser();
        TabIcon?.Dispose();
        TabIcon = null;
        HostPanel.Dispose();
    }
}
