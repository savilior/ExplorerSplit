using System.Diagnostics;
using System.Runtime.InteropServices;
using ExplorerSplit.Configuration;
using ExplorerSplit.Interop;
using ExplorerSplit.UI;

namespace ExplorerSplit.Core;

internal sealed class SplitManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly List<DualPaneExplorerForm> _windows = new();
    private readonly List<IntPtr> _hookHandles = new();
    private readonly HashSet<IntPtr> _interceptedExplorers = new();
    private readonly NativeMethods.WinEventDelegate _winEventDelegate;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardHookDelegate;
    private readonly SynchronizationContext? _uiSyncContext;

    private IntPtr _keyboardHookHandle;
    private bool _disposed;

    public event Action? StateChanged;
    public event Action<IntPtr>? ForegroundWindowChanged;
    public event Action<IntPtr>? TrackedExplorerMoved;

    public SplitManager(AppSettings settings)
    {
        _settings = settings;
        _uiSyncContext = SynchronizationContext.Current;
        _winEventDelegate = OnWinEvent;
        _keyboardHookDelegate = OnLowLevelKeyboard;

        InstallHooks();
        UpdateWinEHook();
    }

    public bool IsPaired(IntPtr explorerHwnd)
    {
        DualPaneExplorerForm? owned = FindOwnedWindow(explorerHwnd);
        return owned is not null && owned.Visible && owned.IsSplitEnabled;
    }

    public IntPtr ResolveExplorerWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (ExplorerDetector.IsExplorerWindow(hwnd))
        {
            return hwnd;
        }

        DualPaneExplorerForm? owned = FindOwnedWindow(hwnd);
        return owned is not null ? owned.Handle : IntPtr.Zero;
    }

    public int ActivePairCount
    {
        get
        {
            CleanupClosedWindows();
            return _windows.Count;
        }
    }

    public void UpdateWinEHook()
    {
        if (_disposed)
        {
            return;
        }

        if (_settings.ReplaceDefaultExplorer && _keyboardHookHandle == IntPtr.Zero)
        {
            using Process currentProcess = Process.GetCurrentProcess();
            using ProcessModule? mainModule = currentProcess.MainModule;
            IntPtr moduleHandle = NativeMethods.GetModuleHandle(mainModule?.ModuleName);
            _keyboardHookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _keyboardHookDelegate,
                moduleHandle,
                0);
        }
        else if (!_settings.ReplaceDefaultExplorer && _keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }
    }

    public Task ToggleSplitAsync(IntPtr explorerHwnd)
    {
        ToggleSplit(explorerHwnd);
        return Task.CompletedTask;
    }

    public void OpenNewWindow(
        string? leftPath = null,
        string? rightPath = null,
        Rectangle? bounds = null,
        bool? startInSplit = null)
    {
        if (_disposed)
        {
            return;
        }

        CleanupClosedWindows();

        string effectiveLeft = !string.IsNullOrWhiteSpace(leftPath)
            ? leftPath
            : ExplorerDetector.HomeShellGuid;
        string effectiveRight = !string.IsNullOrWhiteSpace(rightPath)
            ? rightPath
            : ResolveRightPanePath(effectiveLeft);
        bool splitMode = startInSplit ?? _settings.DefaultStartInSplitMode;

        // Cascade position slightly when multiple windows are open
        if (bounds is null && _windows.Count > 0)
        {
            DualPaneExplorerForm last = _windows[^1];
            if (!last.IsDisposed && last.WindowState == FormWindowState.Normal)
            {
                bounds = new Rectangle(
                    last.Left + 32,
                    last.Top + 32,
                    last.Width,
                    last.Height);
            }
        }

        DualPaneExplorerForm form = new(effectiveLeft, effectiveRight, bounds, splitMode);
        form.FormClosed += (_, _) =>
        {
            _windows.Remove(form);
            StateChanged?.Invoke();
        };

        _windows.Add(form);
        form.Show();
        form.Activate();
        NativeMethods.SetForegroundWindow(form.Handle);
        StateChanged?.Invoke();
    }

    public void ToggleSplit(IntPtr hwnd)
    {
        if (_disposed)
        {
            return;
        }

        DualPaneExplorerForm? owned = FindOwnedWindow(hwnd);
        if (owned is not null)
        {
            owned.ToggleDualPaneSplit();
            StateChanged?.Invoke();
            return;
        }

        if (ExplorerDetector.IsExplorerWindow(hwnd))
        {
            string leftPath = ExplorerDetector.GetActiveFolderPath(hwnd) ?? ExplorerDetector.HomeShellGuid;
            string rightPath = ResolveRightPanePath(leftPath);

            NativeMethods.RECT vis = DwmHelper.GetVisualRect(hwnd);
            Rectangle? targetBounds = vis.Width > 400 && vis.Height > 300
                ? new Rectangle(vis.Left, vis.Top, Math.Max(1100, vis.Width), Math.Max(680, vis.Height))
                : null;

            OpenNewWindow(leftPath, rightPath, targetBounds, startInSplit: true);
            return;
        }

        OpenNewWindow();
    }

    public void UnpairAll(bool closeSecondary = false)
    {
        foreach (DualPaneExplorerForm form in _windows.ToArray())
        {
            if (!form.IsDisposed)
            {
                form.Close();
                form.Dispose();
            }
        }

        _windows.Clear();
        StateChanged?.Invoke();
    }

    private DualPaneExplorerForm? FindOwnedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        CleanupClosedWindows();

        for (int i = 0; i < _windows.Count; i++)
        {
            DualPaneExplorerForm form = _windows[i];
            if (form.IsHandleCreated && (hwnd == form.Handle || NativeMethods.IsChild(form.Handle, hwnd)))
            {
                return form;
            }
        }

        return null;
    }

    private void CleanupClosedWindows()
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            if (_windows[i].IsDisposed)
            {
                _windows.RemoveAt(i);
            }
        }
    }

    private string ResolveRightPanePath(string leftPath)
    {
        return _settings.DefaultRightPaneMode switch
        {
            RightPanePathMode.Home => ExplorerDetector.HomeShellGuid,
            RightPanePathMode.Custom when !string.IsNullOrWhiteSpace(_settings.CustomRightPanePath) =>
                _settings.CustomRightPanePath,
            _ => !string.IsNullOrWhiteSpace(leftPath) ? leftPath : ExplorerDetector.HomeShellGuid
        };
    }

    private void InstallHooks()
    {
        uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

        AddHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, flags);
        AddHook(NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW, flags);
        AddHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, flags);
    }

    private void AddHook(uint eventMin, uint eventMax, uint flags)
    {
        IntPtr handle = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            flags);

        if (handle != IntPtr.Zero)
        {
            _hookHandles.Add(handle);
        }
    }

    private IntPtr OnLowLevelKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 &&
            _settings.ReplaceDefaultExplorer &&
            !_disposed &&
            (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
        {
            NativeMethods.KBDLLHOOKSTRUCT hookInfo = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (hookInfo.vkCode == NativeMethods.VK_E)
            {
                bool winPressed = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                                  (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

                if (winPressed)
                {
                    if (_uiSyncContext is not null)
                    {
                        _uiSyncContext.Post(_ => OpenNewWindow(), null);
                    }
                    else
                    {
                        OpenNewWindow();
                    }

                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return;
        }

        switch (eventType)
        {
            case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                if (_settings.ReplaceDefaultExplorer && ExplorerDetector.IsExplorerWindow(hwnd))
                {
                    _ = InterceptNativeExplorerAsync(hwnd);
                    return;
                }

                ForegroundWindowChanged?.Invoke(hwnd);
                StateChanged?.Invoke();
                break;

            case NativeMethods.EVENT_OBJECT_SHOW:
                if (idObject == NativeMethods.OBJID_WINDOW &&
                    idChild == NativeMethods.CHILDID_SELF &&
                    ExplorerDetector.IsExplorerWindow(hwnd))
                {
                    if (_settings.ReplaceDefaultExplorer)
                    {
                        _ = InterceptNativeExplorerAsync(hwnd);
                        return;
                    }

                    ForegroundWindowChanged?.Invoke(NativeMethods.GetForegroundWindow());
                    StateChanged?.Invoke();
                }
                break;

            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject == NativeMethods.OBJID_WINDOW &&
                    idChild == NativeMethods.CHILDID_SELF &&
                    hwnd == NativeMethods.GetForegroundWindow() &&
                    ExplorerDetector.IsExplorerWindow(hwnd))
                {
                    TrackedExplorerMoved?.Invoke(hwnd);
                }
                break;
        }
    }

    public void InterceptExistingExplorerWindowsIfEnabled()
    {
        if (!_settings.ReplaceDefaultExplorer || _disposed)
        {
            return;
        }

        foreach (IntPtr hwnd in ExplorerDetector.GetAllExplorerWindows())
        {
            _ = InterceptNativeExplorerAsync(hwnd);
        }
    }

    private async Task InterceptNativeExplorerAsync(IntPtr explorerHwnd)
    {
        if (_disposed || !_settings.ReplaceDefaultExplorer || !ExplorerDetector.IsExplorerWindow(explorerHwnd))
        {
            return;
        }

        if (!_interceptedExplorers.Add(explorerHwnd))
        {
            return;
        }

        try
        {
            string? capturedPath = ExplorerDetector.GetActiveFolderPath(explorerHwnd);
            int[] retryDelays = [50, 100, 180];

            for (int i = 0; i < retryDelays.Length && string.IsNullOrWhiteSpace(capturedPath); i++)
            {
                await Task.Delay(retryDelays[i]);
                if (!NativeMethods.IsWindow(explorerHwnd))
                {
                    break;
                }

                capturedPath = ExplorerDetector.GetActiveFolderPath(explorerHwnd);
            }

            if (NativeMethods.IsWindow(explorerHwnd))
            {
                NativeMethods.PostMessage(explorerHwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            OpenNewWindow(capturedPath ?? ExplorerDetector.HomeShellGuid);
        }
        finally
        {
            _interceptedExplorers.Remove(explorerHwnd);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }

        UnpairAll();

        foreach (IntPtr hook in _hookHandles)
        {
            if (hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(hook);
            }
        }

        _hookHandles.Clear();
    }
}
