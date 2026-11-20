using System.Runtime.InteropServices;

namespace Explorer2.Interop;

internal readonly record struct BorderMargins(int Left, int Top, int Right, int Bottom);

internal static class DwmHelper
{
    public static NativeMethods.RECT GetVisualRect(IntPtr hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT visualRect,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0 &&
            visualRect.Width > 0 &&
            visualRect.Height > 0)
        {
            return visualRect;
        }

        if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
        {
            return windowRect;
        }

        return default;
    }

    public static BorderMargins GetBorderMargins(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT winRect))
        {
            return default;
        }

        NativeMethods.RECT visRect = GetVisualRect(hwnd);
        return new BorderMargins(
            Left: Math.Max(0, visRect.Left - winRect.Left),
            Top: Math.Max(0, visRect.Top - winRect.Top),
            Right: Math.Max(0, winRect.Right - visRect.Right),
            Bottom: Math.Max(0, winRect.Bottom - visRect.Bottom));
    }

    public static NativeMethods.RECT VisualToWindowRect(IntPtr hwnd, NativeMethods.RECT targetVisual)
    {
        BorderMargins margins = GetBorderMargins(hwnd);
        return new NativeMethods.RECT(
            left: targetVisual.Left - margins.Left,
            top: targetVisual.Top - margins.Top,
            right: targetVisual.Right + margins.Right,
            bottom: targetVisual.Bottom + margins.Bottom);
    }

    public static bool SetVisualWindowPos(IntPtr hwnd, NativeMethods.RECT targetVisual, uint extraFlags = 0)
    {
        NativeMethods.RECT winRect = VisualToWindowRect(hwnd, targetVisual);
        return NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            winRect.Left,
            winRect.Top,
            winRect.Width,
            winRect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | extraFlags);
    }

    public static NativeMethods.RECT GetMonitorWorkArea(IntPtr hwnd)
    {
        IntPtr hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero)
        {
            NativeMethods.MONITORINFO monitorInfo = new()
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                return monitorInfo.rcWork;
            }
        }

        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        return new NativeMethods.RECT(area.Left, area.Top, area.Right, area.Bottom);
    }

    public static double GetWindowDpiScale(IntPtr hwnd)
    {
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }
}
