using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Explorer2.Configuration;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// Draws a clear border around the file list's current item (the focused item, Total Commander's
/// cursor). Windows' own focus rectangle is a thin line that is hard to see on a multiple selection
/// and in dark mode. The border shows when Windows shows the focus rectangle (after keyboard use):
/// an accent-colored outline over the view's own focus line.
/// The item's rectangle comes from UI Automation on a background thread, because the item view
/// (DirectUIHWND) has no message that returns it.
/// </summary>
internal sealed class FileViewFocusBorder : NativeWindow
{
    private const int WM_SETFOCUS = 0x0007;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_PAINT = 0x000F;
    private const int WM_SIZE = 0x0005;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_HSCROLL = 0x0114;
    private const int WM_VSCROLL = 0x0115;
    private const int WM_UPDATEUISTATE = 0x0128;
    private const int WM_QUERYUISTATE = 0x0129;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_NCDESTROY = 0x0082;
    private const int UISF_HIDEFOCUS = 0x1;

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private Rectangle _rect = Rectangle.Empty; // client coordinates of the current item
    private int _burst;
    private int _tick;
    private bool _queryRunning;
    private bool _detached;

    private FileViewFocusBorder(IntPtr itemsView)
    {
        AssignHandle(itemsView);
        _timer.Tick += (_, _) => OnTimer();
        if (NativeMethods.GetFocus() == itemsView)
        {
            Update();
        }
    }

    /// <summary>Attaches to the item view of the shell view <paramref name="viewHwnd"/>, once it exists.</summary>
    public static FileViewFocusBorder? TryAttach(IntPtr viewHwnd)
    {
        IntPtr itemsView = NativeMethods.FindWindowEx(viewHwnd, IntPtr.Zero, "DirectUIHWND", null);
        return itemsView == IntPtr.Zero ? null : new FileViewFocusBorder(itemsView);
    }

    /// <summary>The current item may have moved (keys handled outside the view, scrolling…).</summary>
    public void Update()
    {
        if (_detached || Handle == IntPtr.Zero)
        {
            return;
        }

        _burst = 4; // look a few times: the view moves its focus a moment after the key
        _timer.Start();
    }

    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _timer.Stop();
        _timer.Dispose();
        if (Handle != IntPtr.Zero)
        {
            InvalidateBorder(_rect);
            ReleaseHandle();
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_PAINT:
                base.WndProc(ref m);
                DrawBorder();
                return;
            case WM_KILLFOCUS:
                base.WndProc(ref m);
                SetRect(Rectangle.Empty);
                return;
            case WM_NCDESTROY:
                base.WndProc(ref m);
                Detach();
                return;
            case WM_SETFOCUS:
            case WM_SIZE:
            case WM_KEYDOWN:
            case WM_KEYUP:
            case WM_HSCROLL:
            case WM_VSCROLL:
            case WM_MOUSEWHEEL:
            case WM_LBUTTONUP:
            case WM_RBUTTONUP:
            case WM_UPDATEUISTATE:
                base.WndProc(ref m);
                Update();
                return;
        }

        base.WndProc(ref m);
    }

    private void OnTimer()
    {
        if (_detached)
        {
            return;
        }

        bool focused = NativeMethods.GetFocus() == Handle;
        if (!focused)
        {
            _timer.Stop();
            SetRect(Rectangle.Empty);
            return;
        }

        // A burst after input, then a slow check (every 300 ms) for anything else that moves it
        if (_burst > 0)
        {
            _burst--;
            Query();
        }
        else if (++_tick % 6 == 0)
        {
            Query();
        }
    }

    private void Query()
    {
        if (_queryRunning)
        {
            return;
        }

        _queryRunning = true;
        IntPtr hwnd = Handle;
        FocusedItemLocator.Request(screenRect =>
        {
            if (_ui is null)
            {
                return;
            }

            _ui.Post(_ =>
            {
                _queryRunning = false;
                if (_detached || Handle != hwnd || NativeMethods.GetFocus() != hwnd)
                {
                    return;
                }

                Rectangle client = Rectangle.Empty;
                if (screenRect is { } r && r.Width > 0 && r.Height > 0)
                {
                    NativeMethods.POINT topLeft = new() { X = r.Left, Y = r.Top };
                    ScreenToClient(hwnd, ref topLeft);
                    client = new Rectangle(topLeft.X, topLeft.Y, r.Width, r.Height);
                    GetClientRect(hwnd, out RECT bounds);
                    if (!client.IntersectsWith(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)))
                    {
                        client = Rectangle.Empty;
                    }
                }

                SetRect(client);
            }, null);
        });
    }

    private void SetRect(Rectangle rect)
    {
        if (rect == _rect)
        {
            return;
        }

        Rectangle old = _rect;
        _rect = rect;
        InvalidateBorder(old);
        InvalidateBorder(rect);
    }

    private void InvalidateBorder(Rectangle rect)
    {
        if (rect.IsEmpty || Handle == IntPtr.Zero)
        {
            return;
        }

        rect.Inflate(3, 3);
        RECT r = new() { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        InvalidateRect(Handle, ref r, false);
    }

    private void DrawBorder()
    {
        if (_rect.IsEmpty || _detached)
        {
            return;
        }

        // Only while Windows shows focus (after keyboard use), like its own focus rectangle
        int uiState = (int)NativeMethods.SendMessage(Handle, WM_QUERYUISTATE, IntPtr.Zero, IntPtr.Zero);
        if ((uiState & UISF_HIDEFOCUS) != 0)
        {
            return;
        }

        IntPtr hdc = GetDC(Handle);
        if (hdc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            using Graphics g = Graphics.FromHdc(hdc);
            ExcludeChildWindows(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = g.DpiX / 96f;
            bool dark = AppTheme.IsDark;

            // An accent outline (the selection's own blue) drawn over the view's thin black / white
            // focus line, so the current item stands out without a hard frame
            Color accent = dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
            float width = Math.Max(2f, (float)Math.Round(2f * scale));
            RectangleF box = new(_rect.Left, _rect.Top, _rect.Width - 1, _rect.Height - 1);
            box.Inflate(-(width - 1) / 2f, -(width - 1) / 2f);
            using GraphicsPath path = RoundedRectangle(box, 2f * scale);
            using Pen pen = new(accent, width);
            g.DrawPath(pen, path);
        }
        catch
        {
            // Cosmetic only
        }
        finally
        {
            ReleaseDC(Handle, hdc);
        }
    }

    /// <summary>Keeps the border off the column header and other child windows of the view.</summary>
    private void ExcludeChildWindows(Graphics g)
    {
        for (IntPtr child = GetWindow(Handle, GW_CHILD); child != IntPtr.Zero; child = GetWindow(child, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(child) || !GetWindowRect(child, out RECT r))
            {
                continue;
            }

            NativeMethods.POINT topLeft = new() { X = r.Left, Y = r.Top };
            ScreenToClient(Handle, ref topLeft);
            g.ExcludeClip(new Rectangle(topLeft.X, topLeft.Y, r.Right - r.Left, r.Bottom - r.Top));
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        GraphicsPath path = new();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private const uint GW_HWNDNEXT = 2;
    private const uint GW_CHILD = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, ref RECT lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativeMethods.POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}

/// <summary>
/// Finds the screen rectangle of the element that has keyboard focus (the file list's current item)
/// with UI Automation, on its own thread so the window never waits for it.
/// </summary>
internal static class FocusedItemLocator
{
    private const int UIA_BoundingRectanglePropertyId = 30001;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_ListItemControlTypeId = 50007;
    private const int UIA_DataItemControlTypeId = 50029;

    private static readonly BlockingCollection<Action<Rectangle?>> Requests = new();
    private static Thread? _worker;

    public static void Request(Action<Rectangle?> done)
    {
        if (_worker is null)
        {
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "Focused item locator" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }

        Requests.Add(done);
    }

    private static void WorkLoop()
    {
        IUIAutomation? automation = null;
        try
        {
            automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;
        }
        catch
        {
            // No UI Automation: every request answers "nothing"
        }

        foreach (Action<Rectangle?> done in Requests.GetConsumingEnumerable())
        {
            Rectangle? result = null;
            if (automation is not null)
            {
                IUIAutomationElement? element = null;
                try
                {
                    element = automation.GetFocusedElement();
                    object controlType = element.GetCurrentPropertyValue(UIA_ControlTypePropertyId);
                    if (controlType is int type && type is UIA_ListItemControlTypeId or UIA_DataItemControlTypeId &&
                        element.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is double[] { Length: 4 } r)
                    {
                        result = new Rectangle((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]));
                    }
                }
                catch
                {
                    // Focus moved away meanwhile
                }
                finally
                {
                    if (element is not null)
                    {
                        Marshal.ReleaseComObject(element);
                    }
                }
            }

            try
            {
                done(result);
            }
            catch
            {
                // The caller handles its own errors
            }
        }
    }

    // Only the methods used are called; the others hold their places in the interface.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        void ElementFromPoint();
        IUIAutomationElement GetFocusedElement();
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }
}
