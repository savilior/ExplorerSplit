using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Explorer2.Interop;

namespace Explorer2.Core;

internal static class ExplorerDetector
{
    public const string ExplorerClassName = "CabinetWClass";
    public const string HomeShellGuid = "::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    public const string ThisPcShellGuid = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>
    /// Where a new window or tab opens, from File Explorer's Folder Options "Open File Explorer to"
    /// (HKCU\...\Explorer\Advanced\LaunchTo: 1 = This PC, 2 or unset = Home, 3 = Downloads).
    /// Read on every call so a change in Folder Options applies to the next window or tab.
    /// </summary>
    public static string StartLocation
    {
        get
        {
            int launchTo = 2;
            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key?.GetValue("LaunchTo") is int value)
                {
                    launchTo = value;
                }
            }
            catch
            {
                // Fall back to Home, File Explorer's default
            }

            return launchTo switch
            {
                1 => ThisPcShellGuid,
                3 => GetDownloadsPath() ?? HomeShellGuid,
                _ => HomeShellGuid
            };
        }
    }

    internal static string? GetDownloadsPath()
    {
        Guid downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        if (NativeMethods.SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out IntPtr pathPtr) != 0)
        {
            return null;
        }

        try
        {
            string? path = Marshal.PtrToStringUni(pathPtr);
            return Directory.Exists(path) ? path : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(
            ref Guid guidService,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);
    }

    public static bool IsExplorerWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        StringBuilder className = new(64);
        int len = NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return len > 0 && string.Equals(className.ToString(), ExplorerClassName, StringComparison.Ordinal);
    }

    public static List<IntPtr> GetAllExplorerWindows()
    {
        List<IntPtr> windows = new();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && IsExplorerWindow(hwnd))
            {
                windows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static string? GetActiveFolderPath(IntPtr explorerHwnd)
    {
        if (!IsExplorerWindow(explorerHwnd))
        {
            return null;
        }

        IntPtr activeTabHwnd = FindActiveTabHwnd(explorerHwnd);
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return null;
        }

        object? shell = null;
        object? windowsObj = null;
        string? fallbackPath = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            dynamic dynShell = shell;
            windowsObj = dynShell.Windows();
            if (windowsObj is not IEnumerable windows)
            {
                return null;
            }

            foreach (object? item in windows)
            {
                if (item is null)
                {
                    continue;
                }

                try
                {
                    dynamic dynItem = item;
                    long hwndVal = Convert.ToInt64(dynItem.HWND);
                    if (new IntPtr(hwndVal) != explorerHwnd)
                    {
                        continue;
                    }

                    string? candidatePath = ExtractPathFromShellItem(dynItem);
                    if (string.IsNullOrEmpty(candidatePath))
                    {
                        continue;
                    }

                    fallbackPath ??= candidatePath;

                    if (activeTabHwnd != IntPtr.Zero &&
                        TryGetTabHwnd(item, out IntPtr itemTabHwnd) &&
                        itemTabHwnd == activeTabHwnd)
                    {
                        return candidatePath;
                    }
                }
                catch
                {
                    // Ignore non-Explorer or transitional ShellWindows items
                }
                finally
                {
                    ReleaseIfComObject(item);
                }
            }
        }
        catch
        {
            return fallbackPath;
        }
        finally
        {
            ReleaseIfComObject(windowsObj);
            ReleaseIfComObject(shell);
        }

        return fallbackPath;
    }

    public static async Task<IntPtr> LaunchNewExplorerWindowAsync(string? path, CancellationToken cancellationToken = default)
    {
        HashSet<IntPtr> existingWindows = new(GetAllExplorerWindows());

        string arguments = BuildLaunchArguments(path);
        ProcessStartInfo startInfo = new("explorer.exe", arguments)
        {
            UseShellExecute = false
        };

        using Process? process = Process.Start(startInfo);

        const int pollDelayMs = 40;
        const int maxWaitMs = 4000;
        int attempts = maxWaitMs / pollDelayMs;

        for (int i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(pollDelayMs, cancellationToken);

            foreach (IntPtr hwnd in GetAllExplorerWindows())
            {
                if (!existingWindows.Contains(hwnd))
                {
                    NativeMethods.RECT rect = DwmHelper.GetVisualRect(hwnd);
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        return hwnd;
                    }
                }
            }
        }

        return IntPtr.Zero;
    }

    private static string BuildLaunchArguments(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"/n,{StartLocation}";
        }

        string trimmed = path.Trim().Trim('"');
        if (trimmed.StartsWith("::", StringComparison.Ordinal))
        {
            return $"/n,{trimmed}";
        }

        if (trimmed.Length == 3 && trimmed[1] == ':' && trimmed[2] == '\\')
        {
            return $"/n,{trimmed}";
        }

        trimmed = trimmed.TrimEnd('\\');
        return $"/n,\"{trimmed}\"";
    }

    public static IntPtr FindActiveTabHwnd(IntPtr explorerHwnd)
    {
        IntPtr firstTab = IntPtr.Zero;
        IntPtr child = IntPtr.Zero;

        while (true)
        {
            child = NativeMethods.FindWindowEx(explorerHwnd, child, "ShellTabWindowClass", null);
            if (child == IntPtr.Zero)
            {
                break;
            }

            if (firstTab == IntPtr.Zero)
            {
                firstTab = child;
            }

            if (NativeMethods.IsWindowVisible(child))
            {
                return child;
            }
        }

        return firstTab;
    }

    public static List<IntPtr> GetAllTabHwnds(IntPtr explorerHwnd)
    {
        List<IntPtr> tabs = new();
        IntPtr child = IntPtr.Zero;

        while (true)
        {
            child = NativeMethods.FindWindowEx(explorerHwnd, child, "ShellTabWindowClass", null);
            if (child == IntPtr.Zero)
            {
                break;
            }

            tabs.Add(child);
        }

        return tabs;
    }

    public static int GetContentAreaTopOffset(IntPtr explorerHwnd, IntPtr activeTabHwnd)
    {
        int topOffset = 0;

        IntPtr bridge = IntPtr.Zero;
        while (true)
        {
            bridge = NativeMethods.FindWindowEx(
                explorerHwnd,
                bridge,
                "Microsoft.UI.Content.DesktopChildSiteBridge",
                null);

            if (bridge == IntPtr.Zero)
            {
                break;
            }

            if (!NativeMethods.IsWindowVisible(bridge) ||
                !NativeMethods.GetWindowRect(bridge, out NativeMethods.RECT bridgeRect))
            {
                continue;
            }

            NativeMethods.POINT topPt = new() { X = bridgeRect.Left, Y = bridgeRect.Top };
            NativeMethods.POINT bottomPt = new() { X = bridgeRect.Right, Y = bridgeRect.Bottom };
            NativeMethods.ScreenToClient(explorerHwnd, ref topPt);
            NativeMethods.ScreenToClient(explorerHwnd, ref bottomPt);

            int height = bottomPt.Y - topPt.Y;
            if (topPt.Y <= 4 && height is >= 24 and <= 120)
            {
                topOffset = Math.Max(topOffset, bottomPt.Y);
            }
        }

        if (activeTabHwnd != IntPtr.Zero &&
            NativeMethods.GetWindowRect(activeTabHwnd, out NativeMethods.RECT tabRect))
        {
            NativeMethods.POINT tabTop = new() { X = tabRect.Left, Y = tabRect.Top };
            NativeMethods.ScreenToClient(explorerHwnd, ref tabTop);
            if (tabTop.Y is >= 0 and < 150)
            {
                topOffset = Math.Max(topOffset, tabTop.Y);
            }
        }

        NativeMethods.POINT clientOrigin = new() { X = 0, Y = 0 };
        NativeMethods.ClientToScreen(explorerHwnd, ref clientOrigin);
        NativeMethods.RECT visualRect = DwmHelper.GetVisualRect(explorerHwnd);
        if (clientOrigin.Y <= visualRect.Top + 4)
        {
            int minOffset = (int)Math.Round(42 * DwmHelper.GetWindowDpiScale(explorerHwnd));
            topOffset = Math.Max(topOffset, minOffset);
        }

        return topOffset;
    }

    private static bool TryGetTabHwnd(object shellItem, out IntPtr tabHwnd)
    {
        tabHwnd = IntPtr.Zero;
        if (shellItem is not IServiceProvider serviceProvider)
        {
            return false;
        }

        Guid sid = SidTopLevelBrowser;
        Guid iid = IidShellBrowser;
        object? browserObj = null;

        try
        {
            int hr = serviceProvider.QueryService(ref sid, ref iid, out browserObj);
            if (hr == 0 && browserObj is IShellBrowser browser)
            {
                return browser.GetWindow(out tabHwnd) == 0 && tabHwnd != IntPtr.Zero;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseIfComObject(browserObj);
        }

        return false;
    }

    private static string? ExtractPathFromShellItem(dynamic dynItem)
    {
        object? document = null;
        object? folder = null;
        object? self = null;

        try
        {
            document = dynItem.Document;
            if (document is not null)
            {
                dynamic dynDoc = document;
                folder = dynDoc.Folder;
                if (folder is not null)
                {
                    dynamic dynFolder = folder;
                    self = dynFolder.Self;
                    if (self is not null)
                    {
                        dynamic dynSelf = self;
                        string? selfPath = dynSelf.Path as string;
                        if (!string.IsNullOrWhiteSpace(selfPath))
                        {
                            return selfPath;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to LocationURL
        }
        finally
        {
            ReleaseIfComObject(self);
            ReleaseIfComObject(folder);
            ReleaseIfComObject(document);
        }

        try
        {
            string? locationUrl = dynItem.LocationURL as string;
            if (!string.IsNullOrWhiteSpace(locationUrl) &&
                Uri.TryCreate(locationUrl, UriKind.Absolute, out Uri? uri) &&
                uri.IsFile)
            {
                return uri.LocalPath;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private static void ReleaseIfComObject(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try
            {
                Marshal.ReleaseComObject(obj);
            }
            catch
            {
                // Ignore release errors
            }
        }
    }
}
