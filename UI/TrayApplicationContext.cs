using System.IO.Pipes;
using Explorer2.Configuration;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    public static readonly string IpcPipeName = "Explorer2_WindowIPC" + AppPaths.InstanceSuffix;
    private const int HotkeyId = 1;

    private readonly AppSettings _settings;
    private readonly SplitManager _splitManager;
    private readonly TitleBarOverlayButton _overlayButton;
    private readonly HotkeyMessageWindow _hotkeyWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly System.Windows.Forms.Timer _overlayGuardTimer;
    private readonly SynchronizationContext? _uiSyncContext;
    private readonly CancellationTokenSource _ipcCts = new();

    private readonly ToolStripMenuItem _newWindowItem;
    private readonly ToolStripMenuItem _unpairAllItem;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripSeparator _preferencesEnd;
    private List<ToolStripItem> _trayPreferenceItems = new();

    private IntPtr _lastExplorerHwnd;
    private bool _isHotkeyRegistered;
    private bool _disposed;

    /// <summary>The running tray context; windows use it for their Preferences menu.</summary>
    internal static TrayApplicationContext? Current { get; private set; }

    /// <summary>Makes one preferences menu item in the caller's menu style (radio items sit in submenus).</summary>
    internal delegate ToolStripMenuItem PreferenceItemFactory(string text, EventHandler? onClick, bool isChecked, bool isRadio);

    public TrayApplicationContext(string? startupFolder = null)
    {
        Current = this;
        _uiSyncContext = SynchronizationContext.Current;
        _settings = AppSettings.Load();
        _splitManager = new SplitManager(_settings);
        _overlayButton = new TitleBarOverlayButton();
        _hotkeyWindow = new HotkeyMessageWindow();
        _trayIcon = IconFactory.CreateTrayIcon();

        _splitManager.StateChanged += RefreshOverlayAndHotkey;
        _splitManager.ForegroundWindowChanged += OnForegroundWindowChanged;
        _splitManager.TrackedExplorerMoved += OnTrackedExplorerMoved;
        _overlayButton.SplitToggleRequested += OnOverlaySplitRequested;
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;

        _newWindowItem = new ToolStripMenuItem("New Explorer2 Window", null, (_, _) => _splitManager.OpenNewWindow());
        _unpairAllItem = new ToolStripMenuItem("Close All Windows", null, (_, _) => _splitManager.UnpairAll(closeSecondary: false));
        ToolStripMenuItem exitItem = new("Exit", null, (_, _) => ExitApplication());
        _preferencesEnd = new ToolStripSeparator();

        // The preferences between the two separators are rebuilt each time the menu opens, from the
        // same list a window's "..." > Preferences shows
        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([
            _newWindowItem,
            new ToolStripSeparator(),
            _preferencesEnd,
            _unpairAllItem,
            exitItem
        ]);
        if (!AppPaths.IsDemo)
        {
            _menu.Items.Insert(1, new ToolStripMenuItem("Take the Tour", null, (_, _) => TourLauncher.Launch()));
        }
        _menu.Opening += (_, _) =>
        {
            RebuildTrayPreferences();
            SyncMenuState();
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = AppPaths.IsDemo ? "Explorer2 (demo)" : "Explorer2 — File Explorer",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _splitManager.OpenNewWindow();

        _overlayGuardTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _overlayGuardTimer.Tick += (_, _) => RefreshOverlayAndHotkey();
        _overlayGuardTimer.Start();

        ExplorerReplacementManager.SetEnabled(_settings.ReplaceDefaultExplorer);
        StartupManager.MigrateLegacyEntry();

        SyncMenuState();
        RefreshOverlayAndHotkey();
        StartIpcServer();

        string? initialFolder = startupFolder;
        if (string.IsNullOrWhiteSpace(initialFolder))
        {
            IntPtr existingExplorer = ExplorerDetector.GetAllExplorerWindows().FirstOrDefault();
            if (existingExplorer != IntPtr.Zero)
            {
                initialFolder = ExplorerDetector.GetActiveFolderPath(existingExplorer);
            }
        }

        _splitManager.OpenNewWindow(initialFolder);
        if (_settings.ReplaceDefaultExplorer)
        {
            _splitManager.InterceptExistingExplorerWindowsIfEnabled();
        }
    }

    private void StartIpcServer()
    {
        _ = Task.Run(async () =>
        {
            while (!_ipcCts.Token.IsCancellationRequested)
            {
                try
                {
                    using NamedPipeServerStream server = new(
                        IpcPipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_ipcCts.Token);
                    using StreamReader reader = new(server);
                    string? folderArg = await reader.ReadLineAsync(_ipcCts.Token);

                    if (_uiSyncContext is not null)
                    {
                        _uiSyncContext.Post(_ =>
                        {
                            if (!_disposed)
                            {
                                _splitManager.OpenNewWindow(string.IsNullOrWhiteSpace(folderArg) ? null : folderArg);
                            }
                        }, null);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(100, _ipcCts.Token).ConfigureAwait(false);
                }
            }
        }, _ipcCts.Token);
    }

    private void OnForegroundWindowChanged(IntPtr hwnd)
    {
        IntPtr resolvedExplorer = _splitManager.ResolveExplorerWindow(hwnd);
        if (resolvedExplorer != IntPtr.Zero)
        {
            _lastExplorerHwnd = resolvedExplorer;
        }

        UpdateContextualHotkey(resolvedExplorer);
        RefreshOverlayForWindow(resolvedExplorer);
    }

    private void OnTrackedExplorerMoved(IntPtr hwnd)
    {
        IntPtr resolvedExplorer = _splitManager.ResolveExplorerWindow(hwnd);
        if (resolvedExplorer != IntPtr.Zero)
        {
            _lastExplorerHwnd = resolvedExplorer;
        }

        RefreshOverlayForWindow(resolvedExplorer);
    }

    private void RefreshOverlayAndHotkey()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        IntPtr resolvedExplorer = _splitManager.ResolveExplorerWindow(foreground);
        if (resolvedExplorer != IntPtr.Zero)
        {
            _lastExplorerHwnd = resolvedExplorer;
        }

        UpdateContextualHotkey(resolvedExplorer);
        RefreshOverlayForWindow(resolvedExplorer);
    }

    private void RefreshOverlayForWindow(IntPtr explorerHwnd)
    {
        // Demo mode leaves File Explorer's windows to the installed Explorer2
        if (_disposed || AppPaths.IsDemo)
        {
            return;
        }

        bool isExplorer = explorerHwnd != IntPtr.Zero && ExplorerDetector.IsExplorerWindow(explorerHwnd);
        bool isSplit = isExplorer && _splitManager.IsPaired(explorerHwnd);
        _overlayButton.UpdatePosition(explorerHwnd, isSplit, _settings.ShowTitleBarButton);
    }

    private void UpdateContextualHotkey(IntPtr explorerHwnd)
    {
        if (_disposed || AppPaths.IsDemo)
        {
            return;
        }

        bool shouldRegister = explorerHwnd != IntPtr.Zero;
        if (shouldRegister && !_isHotkeyRegistered)
        {
            _isHotkeyRegistered = NativeMethods.RegisterHotKey(
                _hotkeyWindow.Handle,
                HotkeyId,
                _settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                (uint)_settings.HotkeyKey);
        }
        else if (!shouldRegister && _isHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
            _isHotkeyRegistered = false;
        }
    }

    private void ReapplyHotkeyRegistration()
    {
        if (_isHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
            _isHotkeyRegistered = false;
        }

        IntPtr resolvedExplorer = _splitManager.ResolveExplorerWindow(NativeMethods.GetForegroundWindow());
        UpdateContextualHotkey(resolvedExplorer);
    }

    private void OnHotkeyPressed()
    {
        IntPtr resolvedExplorer = _splitManager.ResolveExplorerWindow(NativeMethods.GetForegroundWindow());
        if (resolvedExplorer != IntPtr.Zero)
        {
            _ = _splitManager.ToggleSplitAsync(resolvedExplorer);
        }
    }

    private void OnOverlaySplitRequested(IntPtr explorerHwnd)
    {
        if (ExplorerDetector.IsExplorerWindow(explorerHwnd))
        {
            _ = _splitManager.ToggleSplitAsync(explorerHwnd);
        }
    }

    private void OnToggleReplaceDefaultExplorer(object? sender, EventArgs e)
    {
        if (AppPaths.IsDemo)
        {
            return; // a demo instance must not take over File Explorer
        }

        _settings.ReplaceDefaultExplorer = !_settings.ReplaceDefaultExplorer;
        _settings.Save();

        ExplorerReplacementManager.SetEnabled(_settings.ReplaceDefaultExplorer);
        _splitManager.UpdateWinEHook();

        if (_settings.ReplaceDefaultExplorer)
        {
            _overlayButton.HideOverlay();
            _splitManager.InterceptExistingExplorerWindowsIfEnabled();
        }

        SyncMenuState();
        RefreshOverlayAndHotkey();
    }

    private void OnToggleDefaultSplitMode(object? sender, EventArgs e)
    {
        _settings.DefaultStartInSplitMode = !_settings.DefaultStartInSplitMode;
        _settings.Save();
        SyncMenuState();
    }

    private void OnToggleGlobalDetailsAndNoGroup(object? sender, EventArgs e)
    {
        _settings.EnforceGlobalDetailsAndNoGroup = !AppSettings.GlobalDetailsAndNoGroupEnabled;
        _settings.Save();
        SyncMenuState();
    }

    private void OnToggleTitleBarButton(object? sender, EventArgs e)
    {
        _settings.ShowTitleBarButton = !_settings.ShowTitleBarButton;
        _settings.Save();
        RefreshOverlayAndHotkey();
    }

    /// <summary>F5/F6 ask for the destination (on) or go straight to the other pane (off).</summary>
    private void OnToggleAskCopyMove(object? sender, EventArgs e)
    {
        _settings.AskBeforeCopyMove = !_settings.AskBeforeCopyMove;
        _settings.Save();
        SyncMenuState();
    }

    /// <summary>
    /// Favorites (on) or the native Quick access pins (off) in the navigation pane. Like the theme, open
    /// windows are reopened in place to show it.
    /// </summary>
    private void OnToggleFavorites(object? sender, EventArgs e)
    {
        _settings.FavoritesSection = !_settings.FavoritesSection;
        _settings.Save();
        SyncMenuState();
        ReopenWindowsSoon();
    }

    private void OnConfigureTopSection(object? sender, EventArgs e)
    {
        using TopSectionDialog dialog = new();
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            TopSection.SetHidden(dialog.HiddenIds);
        }
    }

    private void OnConfigureHotkey(object? sender, EventArgs e)
    {
        using HotkeyConfigDialog dialog = new(_settings.HotkeyModifiers, _settings.HotkeyKey);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _settings.HotkeyModifiers = dialog.SelectedModifiers;
            _settings.HotkeyKey = dialog.SelectedKey;
            _settings.Save();
            ReapplyHotkeyRegistration();
            SyncMenuState();
        }
    }

    private void SetRightPaneMode(RightPanePathMode mode)
    {
        _settings.DefaultRightPaneMode = mode;
        _settings.Save();
        SyncMenuState();
    }

    private void OnSelectCustomRightPanePath(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Select the default folder for the Right pane when opening Split View:",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_settings.CustomRightPanePath)
                ? _settings.CustomRightPanePath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            _settings.CustomRightPanePath = dialog.SelectedPath;
            _settings.DefaultRightPaneMode = RightPanePathMode.Custom;
            _settings.Save();
            SyncMenuState();
        }
    }

    /// <summary>
    /// Colors are set when a window is built, so open windows are reopened in place to show the theme.
    /// </summary>
    internal void SetTheme(ThemeMode mode)
    {
        if (_settings.Theme == mode)
        {
            return;
        }

        _settings.Theme = mode;
        _settings.Save();
        AppTheme.Mode = mode;
        AppTheme.ApplyToProcess();
        SyncMenuState();
        ReopenWindowsSoon();
    }

    /// <summary>
    /// Reopens the open windows in place so a change applied when a window is built shows at once.
    /// Posted, because the change may come from a menu of one of those windows.
    /// </summary>
    private void ReopenWindowsSoon()
    {
        if (_uiSyncContext is null)
        {
            _splitManager.ReopenAllWindows();
            return;
        }

        _uiSyncContext.Post(_ =>
        {
            if (!_disposed)
            {
                _splitManager.ReopenAllWindows();
            }
        }, null);
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        bool targetState = !StartupManager.IsEnabled();
        StartupManager.SetEnabled(targetState);
        SyncMenuState();
    }

    /// <summary>
    /// The preferences (the tray menu's settings), made fresh with their current state. The tray menu
    /// and every window's "..." > Preferences use this list, so they always match.
    /// </summary>
    internal List<ToolStripItem> CreatePreferenceItems(PreferenceItemFactory make)
    {
        RightPanePathMode mode = _settings.DefaultRightPaneMode;
        string customText = string.IsNullOrWhiteSpace(_settings.CustomRightPanePath)
            ? "Custom..."
            : $"Custom ({_settings.CustomRightPanePath})...";
        ToolStripMenuItem rightPaneMenu = make("Default Right-Pane Path", null, false, false);
        rightPaneMenu.DropDownItems.AddRange([
            make("Same as Left", (_, _) => SetRightPaneMode(RightPanePathMode.SameAsLeft), mode == RightPanePathMode.SameAsLeft, true),
            make("Home", (_, _) => SetRightPaneMode(RightPanePathMode.Home), mode == RightPanePathMode.Home, true),
            make(customText, OnSelectCustomRightPanePath, mode == RightPanePathMode.Custom, true)
        ]);

        ToolStripMenuItem themeMenu = make("Theme", null, false, false);
        themeMenu.DropDownItems.AddRange([
            make("Use Windows Setting", (_, _) => SetTheme(ThemeMode.System), _settings.Theme == ThemeMode.System, true),
            make("Light", (_, _) => SetTheme(ThemeMode.Light), _settings.Theme == ThemeMode.Light, true),
            make("Dark", (_, _) => SetTheme(ThemeMode.Dark), _settings.Theme == ThemeMode.Dark, true)
        ]);

        return [
            make("Replace Default File Explorer", OnToggleReplaceDefaultExplorer, _settings.ReplaceDefaultExplorer, false),
            make("Start in Split Mode by Default", OnToggleDefaultSplitMode, _settings.DefaultStartInSplitMode, false),
            make("Always Use Details View & Group: None", OnToggleGlobalDetailsAndNoGroup, AppSettings.GlobalDetailsAndNoGroupEnabled, false),
            make("Enable Title Bar Toggle Button", OnToggleTitleBarButton, _settings.ShowTitleBarButton, false),
            make("Favorites in Navigation Pane", OnToggleFavorites, _settings.FavoritesSection, false),
            make("Navigation Pane Top Section...", OnConfigureTopSection, false, false),
            make("Ask Before F5 Copy / F6 Move", OnToggleAskCopyMove, _settings.AskBeforeCopyMove, false),
            make($"Configure Hotkey ({_settings.FormatHotkey()})...", OnConfigureHotkey, false, false),
            rightPaneMenu,
            themeMenu,
            make("Run at Windows Startup", OnToggleStartup, StartupManager.IsEnabled(), false)
        ];
    }

    // Only when the menu opens: the setting handlers run from these items' clicks
    private void RebuildTrayPreferences()
    {
        foreach (ToolStripItem old in _trayPreferenceItems)
        {
            _menu.Items.Remove(old);
            old.Dispose();
        }

        _trayPreferenceItems = CreatePreferenceItems((text, onClick, isChecked, _) =>
            new ToolStripMenuItem(text, null, onClick) { Checked = isChecked });
        int insertAt = _menu.Items.IndexOf(_preferencesEnd);
        for (int i = 0; i < _trayPreferenceItems.Count; i++)
        {
            _menu.Items.Insert(insertAt + i, _trayPreferenceItems[i]);
        }
    }

    private void SyncMenuState()
    {
        int winCount = _splitManager.ActivePairCount;
        _unpairAllItem.Enabled = winCount > 0;
        _unpairAllItem.Text = winCount > 0 ? $"Close All Windows ({winCount})" : "Close All Windows";
    }

    /// <summary>The open Explorer2 windows, oldest first.</summary>
    internal IReadOnlyList<DualPaneExplorerForm> Windows => _splitManager.Windows;

    internal ThemeMode Theme => _settings.Theme;

    internal void ExitApplication()
    {
        Dispose(true);
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;
        if (Current == this)
        {
            Current = null;
        }

        if (disposing)
        {
            _ipcCts.Cancel();
            _ipcCts.Dispose();

            _overlayGuardTimer.Stop();
            _overlayGuardTimer.Dispose();

            if (_isHotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
                _isHotkeyRegistered = false;
            }

            _splitManager.Dispose();
            _overlayButton.Dispose();
            _hotkeyWindow.Dispose();

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class HotkeyMessageWindow : NativeWindow, IDisposable
    {
        public event Action? HotkeyPressed;

        public HotkeyMessageWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                HotkeyPressed?.Invoke();
                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }
}
