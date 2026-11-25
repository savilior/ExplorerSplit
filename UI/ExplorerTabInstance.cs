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

/// <summary>
/// Site for the hosted ExplorerBrowser: hands it an ICommDlgBrowser so the host can handle the
/// view's default command (double-click / Enter on items).
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ExplorerBrowserSite : ShellBrowserInterop.IServiceProvider, ShellBrowserInterop.ICommDlgBrowser
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    private readonly Func<bool> _onDefaultCommand;

    public ExplorerBrowserSite(Func<bool> onDefaultCommand) => _onDefaultCommand = onDefaultCommand;

    public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
    {
        // The view asks with the ICommDlgBrowser IID as the service id; the docs name SID_SExplorerBrowserFrame
        if ((guidService == ShellBrowserInterop.IidICommDlgBrowser || guidService == ShellBrowserInterop.SidSExplorerBrowserFrame) &&
            riid == ShellBrowserInterop.IidICommDlgBrowser)
        {
            ppvObject = Marshal.GetComInterfaceForObject(this, typeof(ShellBrowserInterop.ICommDlgBrowser));
            return S_OK;
        }

        ppvObject = IntPtr.Zero;
        return E_NOINTERFACE;
    }

    // S_OK = handled here; S_FALSE = let the view run its default action
    public int OnDefaultCommand(IntPtr ppshv)
    {
        try
        {
            return _onDefaultCommand() ? S_OK : S_FALSE;
        }
        catch
        {
            return S_FALSE;
        }
    }

    public int OnStateChange(IntPtr ppshv, uint uChange) => S_OK;

    public int IncludeObject(IntPtr ppshv, IntPtr pidl) => S_OK; // show every item
}

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
    private FileViewFavoritesMenuHook? _favoritesMenuHook;

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
        _pendingPath = string.IsNullOrWhiteSpace(initialPath) ? ExplorerDetector.StartLocation : initialPath;
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

        // The site must be set before Initialize: the browser looks up its services then.
        if (_browser is ShellBrowserInterop.IObjectWithSite withSite)
        {
            withSite.SetSite(new ExplorerBrowserSite(HandleDefaultCommand));
        }

        int hr = _browser.Initialize(HostPanel.Handle, ref rc, ref _folderSettings);
        if (hr != 0)
        {
            DestroyBrowser();
            return;
        }

        _browser.Advise(this, out _eventsCookie);

        if (!NavigateToPath(_pendingPath))
        {
            NavigateToPath(ExplorerDetector.StartLocation);
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

    /// <summary>
    /// Search scope, as File Explorer's Search options: true = the folder and all subfolders
    /// (default), false = only the current folder.
    /// </summary>
    public bool SearchSubfolders { get; set; } = true;

    /// <summary>Runs the current search again (after a search option changed).</summary>
    public void RerunSearch()
    {
        if (IsShowingSearch && !string.IsNullOrWhiteSpace(SearchQuery))
        {
            ExecuteSearch(SearchQuery, force: true);
        }
    }

    public bool ExecuteSearch(string? query, bool force = false)
    {
        if (_disposed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchQuery = null;
            if (!IsShowingSearch)
            {
                return true;
            }

            // Like Explorer: clearing the box returns to the folder that was searched.
            return NavigateToPath(_lastNonSearchPath);
        }

        if (!force && IsShowingSearch && string.Equals(SearchQuery, query.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        if (_browser is null)
        {
            return false;
        }

        string baseFolder = !string.IsNullOrWhiteSpace(_lastNonSearchPath)
            ? _lastNonSearchPath
            : DisplayPath;

        ShellBrowserInterop.IShellItem? searchItem = CreateSearchFolderItem(baseFolder, query.Trim());
        if (searchItem is null)
        {
            return false;
        }

        try
        {
            SearchQuery = query.Trim();
            _searchNavigationPending = true;
            uint flags = IsShowingSearch
                ? ShellBrowserInterop.SBSP_ABSOLUTE | ShellBrowserInterop.SBSP_WRITENOHISTORY
                : ShellBrowserInterop.SBSP_ABSOLUTE;
            if (_browser.BrowseToObject(searchItem, flags) == 0)
            {
                return true;
            }

            _searchNavigationPending = false;
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(searchItem);
        }
    }

    /// <summary>
    /// True while the tab shows search results rather than a folder.
    /// </summary>
    public bool IsShowingSearch { get; private set; }

    /// <summary>The last query searched in this tab (shown in the search box while results are up).</summary>
    public string? SearchQuery { get; private set; }

    /// <summary>
    /// The folder the tab is in: its current location, or while showing search results the folder
    /// that was searched. Use this to open the "same place" in another tab or pane.
    /// </summary>
    public string FolderPath => IsShowingSearch && !string.IsNullOrWhiteSpace(_lastNonSearchPath)
        ? _lastNonSearchPath
        : ParsingPath;

    private bool _searchNavigationPending;
    private static uint s_searchRevision;
    private readonly HashSet<string> _searchResultParsingNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "File contents" (Search options > Advanced options): the shell reads that setting only once
    /// per process, so it is applied here instead: the free words of the query also match file
    /// contents ("words" becomes "(words OR content:(words))"), while property filters such as
    /// kind:=picture keep applying to every result.
    /// </summary>
    private static string WithContentMatch(string query)
    {
        List<string> filters = new();
        List<string> words = new();
        foreach (System.Text.RegularExpressions.Match token in
                 System.Text.RegularExpressions.Regex.Matches(query, @"[^\s:""]+:(""[^""]*""|\S*)|""[^""]*""|\S+"))
        {
            bool isFilter = token.Value.Contains(':') && !token.Value.StartsWith('"');
            (isFilter ? filters : words).Add(token.Value);
        }

        if (words.Count == 0)
        {
            return query;
        }

        string freeText = string.Join(' ', words);
        string contentPart = $"({freeText} OR content:({freeText}))";
        return filters.Count == 0 ? contentPart : $"{string.Join(' ', filters)} {contentPart}";
    }

    /// <summary>Creates a "property op string" condition leaf; returns IntPtr.Zero on failure.</summary>
    private static IntPtr MakeStringLeaf(ShellBrowserInterop.IConditionFactory factory, string property, int cop, string value)
    {
        // PROPVARIANT { VARTYPE vt = VT_LPWSTR; ...; LPWSTR pwszVal at offset 8 }
        IntPtr propVariant = Marshal.AllocCoTaskMem(24);
        IntPtr text = Marshal.StringToCoTaskMemUni(value);
        try
        {
            for (int i = 0; i < 24; i += 4)
            {
                Marshal.WriteInt32(propVariant, i, 0);
            }

            Marshal.WriteInt16(propVariant, 0, 31 /* VT_LPWSTR */);
            Marshal.WriteIntPtr(propVariant, 8, text);
            return factory.MakeLeaf(property, cop, null, propVariant, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, false,
                out IntPtr leaf) == 0 ? leaf : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
            Marshal.FreeCoTaskMem(propVariant);
        }
    }

    // One query parser per UI thread: creating it loads the schema and is slow.
    [ThreadStatic] private static ShellBrowserInterop.IQueryParser? t_queryParser;
    [ThreadStatic] private static bool t_parserNatural;
    [ThreadStatic] private static bool t_parserWildcard;

    /// <summary>
    /// Parses <paramref name="query"/> the way File Explorer's search box does (Advanced Query
    /// Syntax: words match names, contents and properties; "kind:", "date:", "size:", ... work; words
    /// are prefix-matched) and resolves it against the current time. Returns an ICondition pointer
    /// the caller releases, or IntPtr.Zero when the parser is unavailable.
    /// </summary>
    private static IntPtr ParseNativeQuery(string query)
    {
        ShellBrowserInterop.IQuerySolution? solution = null;
        IntPtr parsed = IntPtr.Zero;
        IntPtr mainType = IntPtr.Zero;
        IntPtr referenceTime = IntPtr.Zero;

        try
        {
            // Same parser preferences File Explorer uses (Folder Options > Search)
            bool naturalSyntax = SearchPreferences.NaturalQuerySyntax;
            bool autoWildcard = SearchPreferences.AutoWildcard;
            if (t_queryParser is not null && (t_parserNatural != naturalSyntax || t_parserWildcard != autoWildcard))
            {
                Marshal.ReleaseComObject(t_queryParser);
                t_queryParser = null;
            }

            if (t_queryParser is null)
            {
                Type? managerType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidQueryParserManager);
                if (managerType is null ||
                    Activator.CreateInstance(managerType) is not ShellBrowserInterop.IQueryParserManager manager)
                {
                    return IntPtr.Zero;
                }

                try
                {
                    Guid iidParser = ShellBrowserInterop.IidIQueryParser;
                    ushort langId = (ushort)(System.Globalization.CultureInfo.CurrentUICulture.LCID & 0xFFFF);
                    if (manager.CreateLoadedParser("SystemIndex", langId, ref iidParser, out ShellBrowserInterop.IQueryParser? parser) != 0 ||
                        parser is null)
                    {
                        return IntPtr.Zero;
                    }

                    manager.InitializeOptions(naturalSyntax, autoWildcard, parser);
                    t_queryParser = parser;
                    t_parserNatural = naturalSyntax;
                    t_parserWildcard = autoWildcard;
                }
                finally
                {
                    Marshal.ReleaseComObject(manager);
                }
            }

            if (t_queryParser.Parse(query, IntPtr.Zero, out solution) != 0 || solution is null ||
                solution.GetQuery(out parsed, out mainType) != 0 || parsed == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // SYSTEMTIME (8 WORDs) for relative terms like "date:today"
            DateTime now = DateTime.Now;
            referenceTime = Marshal.AllocCoTaskMem(16);
            short[] st =
            {
                (short)now.Year, (short)now.Month, (short)now.DayOfWeek, (short)now.Day,
                (short)now.Hour, (short)now.Minute, (short)now.Second, (short)now.Millisecond
            };
            Marshal.Copy(st, 0, referenceTime, st.Length);

            if (solution.Resolve(parsed, 0, referenceTime, out IntPtr resolved) == 0 && resolved != IntPtr.Zero)
            {
                return resolved;
            }

            // Resolution failed: use the unresolved condition (ownership passes to the caller)
            IntPtr result = parsed;
            parsed = IntPtr.Zero;
            return result;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (parsed != IntPtr.Zero) Marshal.Release(parsed);
            if (mainType != IntPtr.Zero) Marshal.Release(mainType);
            if (referenceTime != IntPtr.Zero) Marshal.FreeCoTaskMem(referenceTime);
            if (solution is not null) Marshal.ReleaseComObject(solution);
        }
    }

    /// <summary>
    /// Builds a search folder (the native query, searched recursively under
    /// <paramref name="baseFolder"/>) that the hosted browser shows in place.
    /// </summary>
    private ShellBrowserInterop.IShellItem? CreateSearchFolderItem(string baseFolder, string query)
    {
        ShellBrowserInterop.ISearchFolderItemFactory? factory = null;
        ShellBrowserInterop.IConditionFactory? conditionFactory = null;
        ShellBrowserInterop.IShellItem? scopeItem = null;
        ShellBrowserInterop.IShellItemArray? scope = null;
        IntPtr condition = IntPtr.Zero;

        try
        {
            Guid iidItem = ShellBrowserInterop.IidIShellItem;
            if (ShellBrowserInterop.SHCreateItemFromParsingName(baseFolder, IntPtr.Zero, ref iidItem, out scopeItem) != 0 ||
                scopeItem is null)
            {
                return null;
            }

            Guid iidArray = ShellBrowserInterop.IidIShellItemArray;
            if (ShellBrowserInterop.SHCreateShellItemArrayFromShellItem(scopeItem, ref iidArray, out scope) != 0 ||
                scope is null)
            {
                return null;
            }

            Type? conditionFactoryType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidConditionFactory);
            Type? factoryType = Type.GetTypeFromCLSID(ShellBrowserInterop.ClsidSearchFolderItemFactory);
            if (conditionFactoryType is null || factoryType is null)
            {
                return null;
            }

            conditionFactory = Activator.CreateInstance(conditionFactoryType) as ShellBrowserInterop.IConditionFactory;
            factory = Activator.CreateInstance(factoryType) as ShellBrowserInterop.ISearchFolderItemFactory;
            if (conditionFactory is null || factory is null)
            {
                return null;
            }

            string parseText = SearchPreferences.FileContents ? WithContentMatch(query) : query;

            // "Current folder" scope: the query AND "folder path = the searched folder", in AQS
            if (!SearchSubfolders && Directory.Exists(baseFolder))
            {
                parseText = $"({parseText}) System.ItemFolderPathDisplay:=\"{baseFolder.TrimEnd('\\')}\"";
            }
            condition = ParseNativeQuery(parseText);

            if (condition == IntPtr.Zero)
            {
                // Fallback when the query parser is unavailable: file name contains the text.
                condition = MakeStringLeaf(conditionFactory, "System.ItemNameDisplay", ShellBrowserInterop.COP_VALUE_CONTAINS, query);
                if (condition == IntPtr.Zero)
                {
                    return null;
                }
            }

            // Same title format as File Explorer ("query - Search Results in Folder"). The shell reuses
            // the results of an earlier search folder with the same name (stale after an option or file
            // change), so every search gets a unique invisible suffix: a counter written in zero-width
            // characters.
            uint revision = ++s_searchRevision;
            System.Text.StringBuilder marker = new();
            for (; revision > 0; revision >>= 1)
            {
                marker.Append((revision & 1) == 0 ? '​' : '‌');
            }

            factory.SetDisplayName($"{query} - Search Results in {GetShortFolderTitle(baseFolder)}{marker}");
            factory.SetScope(scope);
            factory.SetCondition(condition);

            if (factory.GetShellItem(ref iidItem, out ShellBrowserInterop.IShellItem? searchItem) != 0 || searchItem is null)
            {
                return null;
            }

            if (searchItem.GetDisplayName(ShellBrowserInterop.SIGDN_DESKTOPABSOLUTEPARSING, out string? parsingName) == 0 &&
                !string.IsNullOrWhiteSpace(parsingName))
            {
                _searchResultParsingNames.Add(parsingName);
            }

            return searchItem;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (condition != IntPtr.Zero) Marshal.Release(condition);
            if (scope is not null) Marshal.ReleaseComObject(scope);
            if (scopeItem is not null) Marshal.ReleaseComObject(scopeItem);
            if (factory is not null) Marshal.ReleaseComObject(factory);
            if (conditionFactory is not null) Marshal.ReleaseComObject(conditionFactory);
        }
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

    /// <summary>
    /// After a navigation, selects (and puts the cursor on) the item named <paramref name="fileName"/>
    /// once the new folder has enumerated. Used by "Open file location" from search results.
    /// </summary>
    public async void SelectItemByNameWhenReady(string fileName)
    {
        int[] delays = { 100, 150, 250, 400, 600, 900, 1200 };
        foreach (int delay in delays)
        {
            await Task.Delay(delay);
            if (_disposed || _browser is null)
            {
                return;
            }

            if (IsShowingSearch)
            {
                continue; // the folder has not replaced the search results yet
            }

            bool done = false;
            WithFolderView2(folderView2 =>
            {
                if (folderView2.ItemCount(0x0002 /* SVGIO_ALLVIEW */, out int count) != 0)
                {
                    return;
                }

                Guid iid = ShellBrowserInterop.IidIShellItem;
                for (int i = 0; i < count && !done; i++)
                {
                    if (folderView2.GetItem(i, ref iid, out object? obj) != 0 || obj is not ShellBrowserInterop.IShellItem item)
                    {
                        continue;
                    }

                    try
                    {
                        if (item.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? path) == 0 &&
                            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            folderView2.SelectItem(i, SvsiSelect | SvsiDeselectOthers | SvsiFocused | SvsiEnsureVisible);
                            _cachedSelectedCount = -1;
                            done = true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            });

            if (done)
            {
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
        bool wasMarking = _isMarking;
        _isMarking = true;
        ShowKeyboardCues();
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

            // Before marking starts, a lone selected item under the cursor is just the shell's
            // "selection follows cursor", not a mark: the first Space marks it instead of unmarking it.
            bool focusedSelected = IsItemSelected(folderView2, focused);
            bool cursorOnlySelection = !wasMarking && focusedSelected &&
                                       folderView2.ItemCount(ShellBrowserInterop.SVGIO_SELECTION, out int selCount) == 0 && selCount == 1;
            folderView2.SelectItem(focused, focusedSelected && !cursorOnlySelection ? SvsiDeselect : SvsiSelect);

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

    // Set once items are marked with Space / Insert (Total Commander style); cleared on navigation.
    private bool _isMarking;

    /// <summary>
    /// True while Total Commander style marks are active: items were marked with Space / Insert
    /// and some are still selected. Cursor keys then move the cursor without touching the marks.
    /// </summary>
    public bool HasCommanderMarks => _isMarking && GetSelectedItemCount() > 0;

    /// <summary>
    /// A plain click makes a new selection, so marking starts over: the next Space marks the
    /// clicked item instead of unmarking it.
    /// </summary>
    public void ResetMarking() => _isMarking = false;

    /// <summary>
    /// Moves the cursor (focused item) by <paramref name="steps"/> items in display order without
    /// changing which items are selected. int.MinValue / int.MaxValue go to the first / last item.
    /// </summary>
    public void MoveCursorKeepingMarks(int steps)
    {
        ShowKeyboardCues();
        WithFolderView2(folderView2 =>
        {
            int target;
            if (steps is int.MinValue or int.MaxValue)
            {
                if (folderView2.GetVisibleItem(-1, steps == int.MaxValue, out target) != 0 || target < 0)
                {
                    return;
                }
            }
            else
            {
                if (folderView2.GetFocusedItem(out int focused) != 0 || focused < 0)
                {
                    return;
                }

                target = focused;
                bool previous = steps < 0;
                for (int i = 0; i < Math.Abs(steps); i++)
                {
                    if (folderView2.GetVisibleItem(target, previous, out int next) != 0 || next < 0 || next == target)
                    {
                        break;
                    }

                    target = next;
                }

                if (target == focused)
                {
                    return;
                }
            }

            uint keep = IsItemSelected(folderView2, target) ? SvsiSelect : 0;
            folderView2.SelectItem(target, keep | SvsiFocused | SvsiEnsureVisible);
            _cachedSelectedCount = -1;
        });
    }

    /// <summary>Rough number of rows visible in the view, for Page Up / Page Down.</summary>
    public int EstimatedPageRows
    {
        get
        {
            float scale = HostPanel.DeviceDpi / 96f;
            int rowHeight = (int)Math.Max(16, (CurrentViewMode == ShellBrowserInterop.FVM_DETAILS ? 22 : 30) * scale);
            return Math.Max(1, HostPanel.ClientSize.Height / rowHeight - 2);
        }
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

    /// <summary>Default command (double-click / Enter) on the selection.</summary>
    private bool HandleDefaultCommand() => BrowseSelectedFolderInPlace() || OpenSelectedFilesLikeExplorer();

    /// <summary>
    /// A single selected folder (or a shortcut to one) is opened in this tab. Left to the view, the
    /// folder's "open" verb runs, and while Explorer2 replaces File Explorer that verb is
    /// Explorer2 itself, so every double-click opened a new window.
    /// </summary>
    private bool BrowseSelectedFolderInPlace()
    {
        const uint SFGAO_LINK = 0x00010000;
        const uint SFGAO_STREAM = 0x00400000;
        const uint SFGAO_FOLDER = 0x20000000;
        Guid bhidLinkTargetItem = new("3981E228-F559-11D3-8E3A-00C04F6837D5");

        if (_browser is null || _disposed)
        {
            return false;
        }

        Guid iid = ShellBrowserInterop.IidIFolderView;
        if (_browser.GetCurrentView(ref iid, out object? fvObj) != 0 ||
            fvObj is not ShellBrowserInterop.IFolderView folderView)
        {
            return false;
        }

        ShellBrowserInterop.IShellItemArray? itemArray = null;
        ShellBrowserInterop.IShellItem? item = null;
        object? target = null;
        try
        {
            Guid iidArray = ShellBrowserInterop.IidIShellItemArray;
            if (folderView.Items(ShellBrowserInterop.SVGIO_SELECTION, ref iidArray, out object? arrayObj) != 0)
            {
                return false;
            }

            itemArray = arrayObj as ShellBrowserInterop.IShellItemArray;
            if (itemArray is null || itemArray.GetCount(out uint count) != 0 || count != 1 ||
                itemArray.GetItemAt(0, out item) != 0 || item is null ||
                item.GetAttributes(SFGAO_LINK | SFGAO_STREAM | SFGAO_FOLDER, out uint attributes) < 0)
            {
                return false;
            }

            if ((attributes & SFGAO_LINK) != 0)
            {
                Guid iidItem = ShellBrowserInterop.IidIShellItem;
                if (item.BindToHandler(IntPtr.Zero, ref bhidLinkTargetItem, ref iidItem, out IntPtr ppv) != 0 || ppv == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    target = Marshal.GetObjectForIUnknown(ppv);
                }
                finally
                {
                    Marshal.Release(ppv);
                }

                if (target is not ShellBrowserInterop.IShellItem targetItem ||
                    targetItem.GetAttributes(SFGAO_STREAM | SFGAO_FOLDER, out attributes) < 0)
                {
                    return false;
                }
            }
            else
            {
                target = item;
            }

            // Folders only; zip and other stream-backed folders keep the view's own behavior
            if ((attributes & SFGAO_FOLDER) == 0 || (attributes & SFGAO_STREAM) != 0)
            {
                return false;
            }

            return _browser.BrowseToObject(target, ShellBrowserInterop.SBSP_ABSOLUTE) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (target is not null && !ReferenceEquals(target, item))
            {
                Marshal.ReleaseComObject(target);
            }

            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }

            if (itemArray is not null)
            {
                Marshal.ReleaseComObject(itemArray);
            }

            Marshal.ReleaseComObject(folderView);
        }
    }

    /// <summary>
    /// Default command (double-click / Enter) on selected files: open them through explorer.exe,
    /// as File Explorer does. Apps that check who launched them (e.g. Foxit's "opened by an app
    /// without a valid signature" warning) then see the signed Windows shell, not Explorer2.exe.
    /// Folders, shortcuts and non-file items return false so the view handles them as before.
    /// </summary>
    private bool OpenSelectedFilesLikeExplorer()
    {
        List<string> paths = GetSelectedItemPaths();
        if (paths.Count == 0 || paths.Count != GetSelectedItemCount())
        {
            return false; // nothing selected, or items without a file-system path
        }

        foreach (string path in paths)
        {
            string ext = Path.GetExtension(path);
            if (!File.Exists(path) ||
                ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) // zip folders browse in place
            {
                return false;
            }
        }

        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        foreach (string path in paths)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(explorer, $"\"{path}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
            });
        }

        return true;
    }

    /// <summary>Returns the file-system paths of the selected items (non-file-system items are skipped).</summary>
    public List<string> GetSelectedItemPaths(int maxItems = int.MaxValue) =>
        GetItemPaths(ShellBrowserInterop.SVGIO_SELECTION, maxItems);

    /// <summary>Selected items' paths; with <paramref name="includeShellItems"/>, items without one (a drive, This PC…) by their shell parsing name.</summary>
    public List<string> GetSelectedItemPaths(bool includeShellItems) =>
        GetItemPaths(ShellBrowserInterop.SVGIO_SELECTION, int.MaxValue, includeShellItems);

    /// <summary>File-system paths of every item in the view, in the view's display (sort) order.</summary>
    public List<string> GetAllItemPathsInViewOrder()
    {
        const uint SVGIO_ALLVIEW = 0x00000002;
        const uint SVGIO_FLAG_VIEWORDER = 0x80000000;
        return GetItemPaths(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER, int.MaxValue);
    }

    private List<string> GetItemPaths(uint svgio, int maxItems, bool includeShellItems = false)
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
                        if (includeShellItems)
                        {
                            if (FavoritesDropTarget.PathOf(item) is { } anyPath)
                            {
                                result.Add(anyPath);
                            }
                        }
                        else if (item.GetDisplayName(ShellBrowserInterop.SIGDN_FILESYSPATH, out string? path) == 0 &&
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
    public void UndoLastAction() => SendViewAccelerator(Keys.Z);

    /// <summary>Redo the last undone shell file operation (Ctrl+Y in the view).</summary>
    public void RedoLastAction() => SendViewAccelerator(Keys.Y);

    /// <summary>
    /// Cut the selection the way Ctrl+X does. Invoking the "cut" verb on the selection's context
    /// menu puts the files on the clipboard but bypasses the view, so the items are not dimmed;
    /// the view's own Ctrl+X handler does both.
    /// </summary>
    public void CutSelection() => SendViewAccelerator(Keys.X);

    // Sends Ctrl+<key> to the file list once the invoking button or menu is done. Keys go by
    // virtual-key code so the accelerator works under any keyboard layout.
    private void SendViewAccelerator(Keys key)
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
            const byte VK_CONTROL = 0x11;
            NativeMethods.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event((byte)key, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event((byte)key, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        });
    }

    /// <summary>
    /// "Compress to ZIP file": zips the selected files and folders into a new .zip next to them,
    /// named after the first item (as File Explorer does).
    /// </summary>
    public async void CompressSelectionToZip()
    {
        List<string> paths = GetSelectedItemPaths();
        if (paths.Count == 0)
        {
            return;
        }

        string folder = Path.GetDirectoryName(paths[0]) ?? string.Empty;
        string baseName = Directory.Exists(paths[0])
            ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))
            : Path.GetFileNameWithoutExtension(paths[0]);
        string zipPath = Path.Combine(folder, baseName + ".zip");
        for (int n = 2; File.Exists(zipPath) || Directory.Exists(zipPath); n++)
        {
            zipPath = Path.Combine(folder, $"{baseName} ({n}).zip");
        }

        try
        {
            await Task.Run(() => WriteZip(zipPath, paths));
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(zipPath);
            }
            catch
            {
                // Leave a partial file rather than hide the original error
            }

            MessageBox.Show(HostPanel.FindForm(), $"Could not create the ZIP file.\n\n{ex.Message}", "Compress to ZIP file",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_disposed)
        {
            RefreshView();
        }
    }

    private static void WriteZip(string zipPath, List<string> paths)
    {
        using FileStream stream = new(zipPath, FileMode.CreateNew);
        using System.IO.Compression.ZipArchive zip = new(stream, System.IO.Compression.ZipArchiveMode.Create);
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                string root = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                zip.CreateEntry(root + "/");
                foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    string relative = root + "/" + Path.GetRelativePath(path, entry).Replace('\\', '/');
                    if (Directory.Exists(entry))
                    {
                        zip.CreateEntry(relative + "/");
                    }
                    else
                    {
                        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, entry, relative);
                    }
                }
            }
            else if (File.Exists(path))
            {
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, path, Path.GetFileName(path));
            }
        }
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

    /// <summary>
    /// Turns on the view's keyboard cues so the cursor (focused item) gets its focus rectangle.
    /// Windows hides it until the view itself sees keyboard input, and the marking keys are
    /// handled before they reach the view, so without this the cursor was invisible.
    /// </summary>
    private void ShowKeyboardCues()
    {
        const uint WM_UPDATEUISTATE = 0x0128;
        const int UIS_CLEAR = 2;
        const int UISF_HIDEFOCUS = 0x1;

        if (_browser is null || _disposed || _keyboardCuesShown)
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
                // WM_UPDATEUISTATE passes itself on to every child window
                NativeMethods.SendMessage(viewHwnd, WM_UPDATEUISTATE, (IntPtr)(UIS_CLEAR | (UISF_HIDEFOCUS << 16)), IntPtr.Zero);
                _keyboardCuesShown = true;
            }
        }
        catch
        {
            // Cosmetic only
        }
        finally
        {
            Marshal.ReleaseComObject(shellView);
        }
    }

    // Reset on navigation: each folder gets a new view window.
    private bool _keyboardCuesShown;

    public static string NormalizeUserInputPath(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return ExplorerDetector.StartLocation;
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
        if (IsShowingSearch || iconTarget.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
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
        // "Pin to Favorites" in the new view's right-click menu
        if (!_disposed && AppSettingsFavorites.Enabled && psv is ShellBrowserInterop.IShellView view)
        {
            try
            {
                if (view.GetWindow(out IntPtr viewHwnd) == 0 && viewHwnd != IntPtr.Zero)
                {
                    _favoritesMenuHook?.Detach();
                    _favoritesMenuHook = new FileViewFavoritesMenuHook(viewHwnd, () => GetSelectedItemPaths(includeShellItems: true));
                }
            }
            catch
            {
                // The view works without it
            }
        }

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

                IsShowingSearch = _searchNavigationPending ||
                                  _searchResultParsingNames.Contains(ParsingPath) ||
                                  ParsingPath.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase);
                _searchNavigationPending = false;
                _isMarking = false;
                _keyboardCuesShown = false;

                if (!IsShowingSearch)
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

    int ShellBrowserInterop.IExplorerBrowserEvents.OnNavigationFailed(IntPtr pidlFolder)
    {
        _searchNavigationPending = false;
        return 0;
    }

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

            if (_browser is ShellBrowserInterop.IObjectWithSite withSite)
            {
                withSite.SetSite(null);
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
        _favoritesMenuHook?.Detach();
        _favoritesMenuHook = null;
        DestroyBrowser();
        TabIcon?.Dispose();
        TabIcon = null;
        HostPanel.Dispose();
    }
}
