using ExplorerSplit.Configuration;
using ExplorerSplit.Interop;
using ExplorerSplit.UI;

namespace ExplorerSplit.Core;

internal sealed class SplitManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly List<IntPtr> _hookHandles = new();
    private readonly HashSet<IntPtr> _interceptedExplorers = new();
    private readonly NativeMethods.WinEventDelegate _winEventDelegate;

    private DualPaneExplorerForm? _dualPaneForm;
    private bool _disposed;

    public event Action? StateChanged;
    public event Action<IntPtr>? ForegroundWindowChanged;
    public event Action<IntPtr>? TrackedExplorerMoved;

    public SplitManager(AppSettings settings)
    {
        _settings = settings;
        _winEventDelegate = OnWinEvent;
        InstallHooks();
    }

    public bool IsPaired(IntPtr explorerHwnd) =>
        _dualPaneForm is not null &&
        !_dualPaneForm.IsDisposed &&
        _dualPaneForm.Visible &&
        _dualPaneForm.IsSplitEnabled &&
        explorerHwnd == _dualPaneForm.Handle;

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

        if (_dualPaneForm is not null &&
            !_dualPaneForm.IsDisposed &&
            _dualPaneForm.IsHandleCreated &&
            (hwnd == _dualPaneForm.Handle || NativeMethods.IsChild(_dualPaneForm.Handle, hwnd)))
        {
            return _dualPaneForm.Handle;
        }

        return IntPtr.Zero;
    }

    public int ActivePairCount =>
        _dualPaneForm is not null && !_dualPaneForm.IsDisposed && _dualPaneForm.Visible ? 1 : 0;

    public Task ToggleSplitAsync(IntPtr explorerHwnd)
    {
        ToggleSplit(explorerHwnd);
        return Task.CompletedTask;
    }

    public void OpenDualPaneWindow(string? leftPath = null, string? rightPath = null, Rectangle? bounds = null)
    {
        if (_disposed)
        {
            return;
        }

        string effectiveLeft = !string.IsNullOrWhiteSpace(leftPath)
            ? leftPath
            : ExplorerDetector.HomeShellGuid;
        string effectiveRight = !string.IsNullOrWhiteSpace(rightPath)
            ? rightPath
            : ResolveRightPanePath(effectiveLeft);

        if (_dualPaneForm is null || _dualPaneForm.IsDisposed)
        {
            _dualPaneForm = new DualPaneExplorerForm(effectiveLeft, effectiveRight, bounds);
            _dualPaneForm.FormClosed += (_, _) => StateChanged?.Invoke();
            _dualPaneForm.Show();
            _dualPaneForm.Activate();
        }
        else
        {
            if (bounds is { Width: > 400, Height: > 300 } b)
            {
                _dualPaneForm.WindowState = FormWindowState.Normal;
                _dualPaneForm.Bounds = b;
            }

            _dualPaneForm.OpenOrFocusFolder(effectiveLeft, effectiveRight, ensureSplitOn: true);
        }

        StateChanged?.Invoke();
    }

    public void ToggleSplit(IntPtr hwnd)
    {
        if (_disposed)
        {
            return;
        }

        // If already focused inside our single-window DualPaneExplorerForm, toggle its right pane
        if (_dualPaneForm is not null &&
            !_dualPaneForm.IsDisposed &&
            _dualPaneForm.IsHandleCreated &&
            (hwnd == _dualPaneForm.Handle || NativeMethods.IsChild(_dualPaneForm.Handle, hwnd)))
        {
            _dualPaneForm.ToggleDualPaneSplit();
            StateChanged?.Invoke();
            return;
        }

        // Triggered from a native Windows File Explorer (CabinetWClass) window
        if (ExplorerDetector.IsExplorerWindow(hwnd))
        {
            string leftPath = ExplorerDetector.GetActiveFolderPath(hwnd) ?? ExplorerDetector.HomeShellGuid;
            string rightPath = ResolveRightPanePath(leftPath);

            NativeMethods.RECT vis = DwmHelper.GetVisualRect(hwnd);
            Rectangle? targetBounds = vis.Width > 400 && vis.Height > 300
                ? new Rectangle(vis.Left, vis.Top, Math.Max(1100, vis.Width), Math.Max(680, vis.Height))
                : null;

            OpenDualPaneWindow(leftPath, rightPath, targetBounds);
            return;
        }

        OpenDualPaneWindow();
    }

    public void UnpairAll(bool closeSecondary = false)
    {
        if (_dualPaneForm is not null && !_dualPaneForm.IsDisposed)
        {
            _dualPaneForm.Close();
            _dualPaneForm.Dispose();
            _dualPaneForm = null;
            StateChanged?.Invoke();
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
            int[] retryDelays = [60, 120, 200];

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

            OpenDualPaneWindow(capturedPath ?? ExplorerDetector.HomeShellGuid);
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
