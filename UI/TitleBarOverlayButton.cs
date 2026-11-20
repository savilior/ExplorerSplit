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
        _toolTip.SetToolTip(this, "Toggle Side-by-Side Split View");
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color backgroundColor;
        Color borderColor;
        Color glyphColor;

        if (_isActiveSplit)
        {
            backgroundColor = _isHovered
                ? Color.FromArgb(0, 96, 180)
                : Color.FromArgb(0, 103, 192);
            borderColor = Color.FromArgb(72, 160, 238);
            glyphColor = Color.White;
        }
        else
        {
            backgroundColor = _isHovered
                ? Color.FromArgb(62, 62, 68)
                : Color.FromArgb(44, 44, 48);
            borderColor = _isHovered
                ? Color.FromArgb(115, 115, 125)
                : Color.FromArgb(78, 78, 86);
            glyphColor = Color.FromArgb(242, 242, 245);
        }

        using SolidBrush bgBrush = new(backgroundColor);
        g.FillRectangle(bgBrush, ClientRectangle);

        Rectangle borderRect = new(0, 0, Width - 1, Height - 1);
        using GraphicsPath borderPath = IconFactory.CreateRoundedRect(borderRect, Math.Max(3, Width / 8));
        using Pen borderPen = new(borderColor, 1f);
        g.DrawPath(borderPen, borderPath);

        // Draw crisp split-pane glyph [|]
        int padX = Math.Max(6, Width / 4);
        int padY = Math.Max(5, Height / 4);
        Rectangle iconRect = new(padX, padY, Width - padX * 2, Height - padY * 2);

        using Pen iconPen = new(glyphColor, Math.Max(1.4f, Width / 20f));
        g.DrawRectangle(iconPen, iconRect);

        int midX = iconRect.Left + iconRect.Width / 2;
        g.DrawLine(iconPen, midX, iconRect.Top, midX, iconRect.Bottom);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
