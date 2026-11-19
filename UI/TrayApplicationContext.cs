using ExplorerSplit.Configuration;
using ExplorerSplit.Core;
using ExplorerSplit.Interop;

namespace ExplorerSplit.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyId = 1;

    private readonly AppSettings _settings;
    private readonly SplitManager _splitManager;
    private readonly TitleBarOverlayButton _overlayButton;
    private readonly HotkeyMessageWindow _hotkeyWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly System.Windows.Forms.Timer _overlayGuardTimer;

    private readonly ToolStripMenuItem _toggleSplitItem;
    private readonly ToolStripMenuItem _replaceExplorerItem;
    private readonly ToolStripMenuItem _titleBarButtonItem;
    private readonly ToolStripMenuItem _hotkeyItem;
    private readonly ToolStripMenuItem _sameAsLeftItem;
    private readonly ToolStripMenuItem _homeItem;
    private readonly ToolStripMenuItem _customPathItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _unpairAllItem;

    private IntPtr _lastExplorerHwnd;
    private bool _isHotkeyRegistered;
    private bool _disposed;

    public TrayApplicationContext(string? startupFolder = null)
    {
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

        _toggleSplitItem = new ToolStripMenuItem("Open Dual-Pane Explorer", null, (_, _) => TriggerToggleForActiveOrRecentExplorer());
        _replaceExplorerItem = new ToolStripMenuItem("Replace Default File Explorer", null, OnToggleReplaceDefaultExplorer);
        _titleBarButtonItem = new ToolStripMenuItem("Enable Title Bar Toggle Button", null, OnToggleTitleBarButton);
        _hotkeyItem = new ToolStripMenuItem(string.Empty, null, OnConfigureHotkey);

        _sameAsLeftItem = new ToolStripMenuItem("Same as Left", null, (_, _) => SetRightPaneMode(RightPanePathMode.SameAsLeft));
        _homeItem = new ToolStripMenuItem("Home", null, (_, _) => SetRightPaneMode(RightPanePathMode.Home));
        _customPathItem = new ToolStripMenuItem("Custom...", null, OnSelectCustomRightPanePath);

        ToolStripMenuItem rightPaneMenu = new("Default Right-Pane Path");
        rightPaneMenu.DropDownItems.AddRange([_sameAsLeftItem, _homeItem, _customPathItem]);

        _startupItem = new ToolStripMenuItem("Run at Windows Startup", null, OnToggleStartup);
        _unpairAllItem = new ToolStripMenuItem("Close All Split Views", null, (_, _) => _splitManager.UnpairAll(closeSecondary: false));
        ToolStripMenuItem exitItem = new("Exit", null, (_, _) => ExitApplication());

        ContextMenuStrip menu = new();
        menu.Items.AddRange([
            _toggleSplitItem,
            new ToolStripSeparator(),
            _replaceExplorerItem,
            _titleBarButtonItem,
            _hotkeyItem,
            rightPaneMenu,
            _startupItem,
            new ToolStripSeparator(),
            _unpairAllItem,
            exitItem
        ]);
        menu.Opening += (_, _) => SyncMenuState();

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "ExplorerSplit — Side-by-Side File Explorer",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => TriggerToggleForActiveOrRecentExplorer();

        _overlayGuardTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _overlayGuardTimer.Tick += (_, _) => RefreshOverlayAndHotkey();
        _overlayGuardTimer.Start();

        SyncMenuState();
        RefreshOverlayAndHotkey();

        // Open the single-window Dual-Pane Explorer on startup
        string? initialFolder = startupFolder;
        if (string.IsNullOrWhiteSpace(initialFolder))
        {
            IntPtr existingExplorer = ExplorerDetector.GetAllExplorerWindows().FirstOrDefault();
            if (existingExplorer != IntPtr.Zero)
            {
                initialFolder = ExplorerDetector.GetActiveFolderPath(existingExplorer);
            }
        }

        _splitManager.OpenDualPaneWindow(initialFolder);
        if (_settings.ReplaceDefaultExplorer)
        {
            _splitManager.InterceptExistingExplorerWindowsIfEnabled();
        }
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
        if (_disposed)
        {
            return;
        }

        bool isExplorer = explorerHwnd != IntPtr.Zero && ExplorerDetector.IsExplorerWindow(explorerHwnd);
        bool isSplit = isExplorer && _splitManager.IsPaired(explorerHwnd);
        _overlayButton.UpdatePosition(explorerHwnd, isSplit, _settings.ShowTitleBarButton);
    }

    private void UpdateContextualHotkey(IntPtr explorerHwnd)
    {
        if (_disposed)
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

    private void TriggerToggleForActiveOrRecentExplorer()
    {
        IntPtr target = NativeMethods.GetForegroundWindow();
        if (!ExplorerDetector.IsExplorerWindow(target))
        {
            if (ExplorerDetector.IsExplorerWindow(_lastExplorerHwnd) &&
                NativeMethods.IsWindowVisible(_lastExplorerHwnd))
            {
                target = _lastExplorerHwnd;
            }
            else
            {
                target = ExplorerDetector.GetAllExplorerWindows().FirstOrDefault();
            }
        }

        if (target != IntPtr.Zero)
        {
            _ = _splitManager.ToggleSplitAsync(target);
        }
        else
        {
            _splitManager.OpenDualPaneWindow();
        }
    }

    private void OnToggleReplaceDefaultExplorer(object? sender, EventArgs e)
    {
        _settings.ReplaceDefaultExplorer = !_settings.ReplaceDefaultExplorer;
        _settings.Save();

        if (_settings.ReplaceDefaultExplorer)
        {
            _overlayButton.HideOverlay();
            _splitManager.InterceptExistingExplorerWindowsIfEnabled();
        }

        SyncMenuState();
        RefreshOverlayAndHotkey();
    }

    private void OnToggleTitleBarButton(object? sender, EventArgs e)
    {
        _settings.ShowTitleBarButton = !_settings.ShowTitleBarButton;
        _settings.Save();
        RefreshOverlayAndHotkey();
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

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        bool targetState = !StartupManager.IsEnabled();
        StartupManager.SetEnabled(targetState);
        SyncMenuState();
    }

    private void SyncMenuState()
    {
        _replaceExplorerItem.Checked = _settings.ReplaceDefaultExplorer;
        _titleBarButtonItem.Checked = _settings.ShowTitleBarButton;
        _hotkeyItem.Text = $"Configure Hotkey ({_settings.FormatHotkey()})...";

        _sameAsLeftItem.Checked = _settings.DefaultRightPaneMode == RightPanePathMode.SameAsLeft;
        _homeItem.Checked = _settings.DefaultRightPaneMode == RightPanePathMode.Home;
        _customPathItem.Checked = _settings.DefaultRightPaneMode == RightPanePathMode.Custom;
        _customPathItem.Text = string.IsNullOrWhiteSpace(_settings.CustomRightPanePath)
            ? "Custom..."
            : $"Custom ({_settings.CustomRightPanePath})...";

        _startupItem.Checked = StartupManager.IsEnabled();

        int pairCount = _splitManager.ActivePairCount;
        _unpairAllItem.Enabled = pairCount > 0;
        _unpairAllItem.Text = pairCount > 0 ? $"Close All Split Views ({pairCount})" : "Close All Split Views";

        _toggleSplitItem.Enabled = true;
    }

    private void ExitApplication()
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

        if (disposing)
        {
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
