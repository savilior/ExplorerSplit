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
        _browser?.BrowseToIDList(IntPtr.Zero, sbspFlag);
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
        if (_browser is not null && !_disposed)
        {
            Guid iid = ShellBrowserInterop.IidIFolderView;
            if (_browser.GetCurrentView(ref iid, out object? fvObj) == 0 &&
                fvObj is ShellBrowserInterop.IFolderView folderView)
            {
                try
                {
                    Guid iidArray = ShellBrowserInterop.IidIShellItemArray;
                    if (folderView.Items(ShellBrowserInterop.SVGIO_SELECTION, ref iidArray, out object? arrayObj) == 0 &&
                        arrayObj is ShellBrowserInterop.IShellItemArray itemArray)
                    {
                        try
                        {
                            if (itemArray.GetCount(out uint count) == 0 && count > 0 &&
                                itemArray.GetItemAt(0, out ShellBrowserInterop.IShellItem? firstItem) == 0 &&
                                firstItem is not null)
                            {
                                try
                                {
                                    if (firstItem.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? selPath) == 0 &&
                                        !string.IsNullOrWhiteSpace(selPath))
                                    {
                                        return selPath;
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(firstItem);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(itemArray);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(folderView);
                }
            }
        }

        return null;
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

    public bool InvokeModernShare()
    {
        if (_browser is null || _disposed)
        {
            return false;
        }

        Guid iid2 = ShellBrowserInterop.IidIFolderView2;
        if (_browser.GetCurrentView(ref iid2, out object? fv2Obj) != 0 ||
            fv2Obj is not ShellBrowserInterop.IFolderView2 folderView2)
        {
            return false;
        }

        IntPtr pSelection = IntPtr.Zero;
        object? shareCmdObj = null;
        try
        {
            if (folderView2.GetSelection(false, out pSelection) != 0 || pSelection == IntPtr.Zero)
            {
                return false;
            }

            Type? shareType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidModernShare);
            if (shareType is null)
            {
                return false;
            }

            shareCmdObj = Activator.CreateInstance(shareType);
            if (shareCmdObj is not ShellBrowserInterop.IExplorerCommand explorerCommand)
            {
                return false;
            }

            if (shareCmdObj is ShellBrowserInterop.IObjectWithSite withSite)
            {
                withSite.SetSite(_browser);
            }

            return explorerCommand.Invoke(pSelection, IntPtr.Zero) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shareCmdObj is not null && Marshal.IsComObject(shareCmdObj))
            {
                Marshal.ReleaseComObject(shareCmdObj);
            }

            if (pSelection != IntPtr.Zero)
            {
                Marshal.Release(pSelection);
            }

            Marshal.ReleaseComObject(folderView2);
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
                    hwnd = HostPanel.Handle,
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

    public void SelectAllItems() => SendShellViewCommand(0x7021); // FCIDM_SHVIEW_SELECTALL

    public void InvertSelection() => SendShellViewCommand(0x7022); // FCIDM_SHVIEW_INVERTSELECTION

    public void UndoLastAction() => SendShellViewCommand(0x701C); // FCIDM_SHVIEW_UNDO

    public void SelectNoneItems()
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
                const uint SVSI_DESELECTOTHERS = 0x0004;
                folderView2.SelectItem(-1, SVSI_DESELECTOTHERS);
            }
            catch
            {
                // Ignore
            }
            finally
            {
                Marshal.ReleaseComObject(folderView2);
            }
        }
    }

    private void SendShellViewCommand(int commandId)
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
            if (shellView.GetWindow(out IntPtr viewHwnd) == 0 && viewHwnd != IntPtr.Zero)
            {
                NativeMethods.SendMessage(viewHwnd, NativeMethods.WM_COMMAND, new IntPtr(commandId), IntPtr.Zero);
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            Marshal.ReleaseComObject(shellView);
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
