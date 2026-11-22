using System.Drawing.Drawing2D;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

internal sealed class TitleBarOverlayButton : Form
{
    private readonly ToolTip _toolTip;
    private IntPtr _targetExplorerHwnd;
    private Rectangle _lastPlacedBounds;
    private bool _isActiveSplit;
    private bool _isHovered;

    public event Action<IntPtr>? SplitToggleRequested;

    public TitleBarOverlayButton()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(32, 26);

        _toolTip = new ToolTip
        {
            InitialDelay = 350,
            ReshowDelay = 200
        };
        _toolTip.SetToolTip(this, "Open in Explorer2 (split view)");
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW |
                          NativeMethods.WS_EX_NOACTIVATE |
                          NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)NativeMethods.MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    public void UpdatePosition(IntPtr foregroundExplorerHwnd, bool isSplit, bool enabled)
    {
        if (!enabled ||
            foregroundExplorerHwnd == IntPtr.Zero ||
            !ExplorerDetector.IsExplorerWindow(foregroundExplorerHwnd) ||
            !NativeMethods.IsWindowVisible(foregroundExplorerHwnd) ||
            NativeMethods.IsIconic(foregroundExplorerHwnd))
        {
            HideOverlay();
            return;
        }

        NativeMethods.RECT visualRect = DwmHelper.GetVisualRect(foregroundExplorerHwnd);
        if (visualRect.Width <= 0 || visualRect.Height <= 0)
        {
            HideOverlay();
            return;
        }

        double dpiScale = DwmHelper.GetWindowDpiScale(foregroundExplorerHwnd);
        int width = Math.Max(24, (int)Math.Round(32 * dpiScale));
        int height = Math.Max(20, (int)Math.Round(26 * dpiScale));
        int x = Math.Max(visualRect.Left + 8, visualRect.Right - (int)Math.Round(185 * dpiScale));
        int y = visualRect.Top + (int)Math.Round(6 * dpiScale);

        bool needsInvalidate = _isActiveSplit != isSplit || _targetExplorerHwnd != foregroundExplorerHwnd;
        _targetExplorerHwnd = foregroundExplorerHwnd;
        _isActiveSplit = isSplit;

        bool wasHidden = !Visible;
        if (wasHidden)
        {
            base.Show();
        }

        Rectangle targetBounds = new(x, y, width, height);
        bool sizeChanged = _lastPlacedBounds.Width != width || _lastPlacedBounds.Height != height;

        if (wasHidden || _lastPlacedBounds != targetBounds)
        {
            _lastPlacedBounds = targetBounds;
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE);
        }

        if (sizeChanged)
        {
            UpdateRoundedRegion(dpiScale);
            needsInvalidate = true;
        }

        if (needsInvalidate)
        {
            Invalidate();
        }
    }

    public void HideOverlay()
    {
        _targetExplorerHwnd = IntPtr.Zero;
        _lastPlacedBounds = Rectangle.Empty;
        _isHovered = false;
        if (Visible)
        {
            Hide();
        }
    }

    private void UpdateRoundedRegion(double dpiScale)
    {
        int radius = Math.Max(3, (int)Math.Round(4 * dpiScale));
        using GraphicsPath path = IconFactory.CreateRoundedRect(new Rectangle(0, 0, Width, Height), radius);
        Region? previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && _targetExplorerHwnd != IntPtr.Zero)
        {
            SplitToggleRequested?.Invoke(_targetExplorerHwnd);
        }
    }

    // The button shows the Explorer2 app icon on File Explorer's title bar. Its background is made
    // transparent (TransparencyKey) so only the icon, and a hover / split-active highlight, show.
    private static bool IsLightTheme()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private Bitmap? _appIcon;
    private int _appIconSize;

    private Bitmap GetAppIcon(int size)
    {
        if (_appIcon is null || _appIconSize != size)
        {
            _appIcon?.Dispose();
            _appIconSize = size;
            string? exe = Environment.ProcessPath;
            using Icon? icon = exe is not null ? Icon.ExtractIcon(exe, 0, size) : null;
            _appIcon = icon?.ToBitmap() ?? IconFactory.CreateTrayIcon().ToBitmap();
        }

        return _appIcon;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        bool light = IsLightTheme();
        // Close to the title bar color, so the icon's anti-aliased edges blend in.
        Color key = light ? Color.FromArgb(240, 240, 241) : Color.FromArgb(31, 31, 32);
        if (BackColor != key)
        {
            BackColor = key;
            TransparencyKey = key;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(key);

        Rectangle box = new(0, 0, Width - 1, Height - 1);
        int radius = Math.Max(3, Height / 6);
        if (_isActiveSplit || _isHovered)
        {
            Color fill = _isActiveSplit
                ? (light ? Color.FromArgb(204, 228, 247) : Color.FromArgb(0, 74, 135))
                : (light ? Color.FromArgb(224, 224, 226) : Color.FromArgb(58, 58, 62));
            using GraphicsPath path = IconFactory.CreateRoundedRect(box, radius);
            using SolidBrush brush = new(fill);
            g.FillPath(brush, path);
        }

        int iconSize = Math.Min(Height - 6, (int)Math.Round(Height * 0.7));
        Bitmap icon = GetAppIcon(Math.Max(16, iconSize));
        Rectangle iconRect = new((Width - iconSize) / 2, (Height - iconSize) / 2, iconSize, iconSize);
        g.DrawImage(icon, iconRect);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _appIcon?.Dispose();
        }

        base.Dispose(disposing);
    }
}
