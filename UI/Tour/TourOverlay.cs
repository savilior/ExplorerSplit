using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Explorer2.UI;

/// <summary>
/// A see-through window the mouse clicks pass through, drawn with per-pixel transparency
/// (UpdateLayeredWindow). The tour's cursor, highlight ring and captions are each one.
/// </summary>
internal class TourLayer : Form
{
    private readonly bool _topMost;

    public TourLayer(bool topMost)
    {
        _topMost = topMost;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x8000000,
                WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | (_topMost ? WS_EX_TOPMOST : 0);
            return cp;
        }
    }

    /// <summary>Shows <paramref name="bitmap"/> (32-bit ARGB) with its top-left corner at <paramref name="location"/>.</summary>
    public void Present(Bitmap bitmap, Point location)
    {
        if (!Visible)
        {
            Show();
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            SIZE size = new() { cx = bitmap.Width, cy = bitmap.Height };
            POINT source = new();
            POINT dest = new() { x = location.X, y = location.Y };
            BLENDFUNCTION blend = new() { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dest, ref size, memDc, ref source, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Puts a top-most layer back above menus that opened after it.</summary>
    public void Raise()
    {
        if (_topMost && Visible)
        {
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// What the tour draws over the demo window: a cursor that glides to what is being shown (with a
/// ripple on clicks and the dragged item's name while dragging), a ring around it, and at the bottom
/// the key being pressed and the caption.
/// </summary>
internal sealed class TourOverlay : IDisposable
{
    private static readonly Color Accent = Color.FromArgb(0, 120, 212);

    private readonly TourLayer _cursor = new(topMost: true);
    private readonly TourLayer _ring = new(topMost: true);
    private readonly TourLayer _caption = new(topMost: true);
    private float _scale = 1f;

    private Rectangle _ringRect;
    private string? _captionText;
    private string? _keyText;
    private Rectangle _window;
    private bool _hidden;

    // Cursor state
    private Point _cursorAt = new(-1, -1);
    private bool _pressed;
    private float _ripple = -1f;
    private string? _dragLabel;

    public Point CursorPosition => _cursorAt;

    public void SetScale(float scale)
    {
        if (Math.Abs(scale - _scale) > 0.01f)
        {
            _scale = scale;
            RedrawCursor();
            RedrawRing();
            RedrawCaption();
        }
    }

    /// <summary>Hides everything while another program is in front (the layers are top-most).</summary>
    public void SetHidden(bool hidden)
    {
        if (hidden == _hidden)
        {
            return;
        }

        _hidden = hidden;
        if (hidden)
        {
            _cursor.Hide();
            _ring.Hide();
            _caption.Hide();
        }
        else
        {
            RedrawCursor();
            RedrawRing();
            RedrawCaption();
        }
    }

    public void RaiseTopMost()
    {
        _caption.Raise();
        _ring.Raise();
        _cursor.Raise();
    }

    // ------------------------------------------------------------------ cursor

    public void SetCursor(Point at, bool pressed = false, float ripple = -1f, string? dragLabel = null)
    {
        _cursorAt = at;
        _pressed = pressed;
        _ripple = ripple;
        _dragLabel = dragLabel;
        RedrawCursor();
    }

    public void HideCursor()
    {
        _cursorAt = new Point(-1, -1);
        _cursor.Hide();
    }

    private int S(float v) => (int)Math.Round(v * _scale);

    private void RedrawCursor()
    {
        if (_hidden || _cursorAt.X < 0)
        {
            return;
        }

        // The hotspot sits at (pad, pad); room around it for the ripple, to the right for the label
        int pad = S(26);
        using Font labelFont = new("Segoe UI", 10f * _scale, FontStyle.Regular, GraphicsUnit.Point);
        Size labelSize = _dragLabel is null ? Size.Empty : TextRenderer.MeasureText(_dragLabel, labelFont);
        int width = pad + S(30) + (labelSize.Width > 0 ? labelSize.Width + S(24) : 0) + pad / 2;
        int height = pad + S(40) + pad / 2;

        using Bitmap bmp = new(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (_ripple >= 0f)
            {
                float r = S(6) + _ripple * S(18);
                int alpha = (int)(200 * (1f - _ripple));
                using Pen ring = new(Color.FromArgb(alpha, Accent), S(3));
                g.DrawEllipse(ring, pad - r, pad - r, r * 2, r * 2);
            }

            if (_pressed)
            {
                using SolidBrush dot = new(Color.FromArgb(110, Accent));
                g.FillEllipse(dot, pad - S(10), pad - S(10), S(20), S(20));
            }

            if (_dragLabel is not null)
            {
                Rectangle chip = new(pad + S(18), pad + S(20), labelSize.Width + S(14), labelSize.Height + S(6));
                using GraphicsPath path = IconFactory.CreateRoundedRect(chip, S(5));
                using SolidBrush bg = new(Color.FromArgb(225, 255, 255, 255));
                using Pen border = new(Color.FromArgb(200, Accent), Math.Max(1, S(1)));
                g.FillPath(bg, path);
                g.DrawPath(border, path);
                using SolidBrush text = new(Color.FromArgb(24, 24, 28));
                g.DrawString(_dragLabel, labelFont, text, chip.X + S(7), chip.Y + S(3));
            }

            // Arrow cursor: white with a dark outline
            float k = 1.25f * _scale;
            PointF[] arrow =
            [
                new(0, 0), new(0, 17), new(4.2f, 13), new(7, 19.5f), new(9.6f, 18.4f), new(6.9f, 12.2f), new(12.4f, 12.2f)
            ];
            for (int i = 0; i < arrow.Length; i++)
            {
                arrow[i] = new PointF(pad + arrow[i].X * k, pad + arrow[i].Y * k);
            }

            using SolidBrush white = new(Color.White);
            using Pen outline = new(Color.FromArgb(20, 20, 20), Math.Max(1f, 1.2f * _scale)) { LineJoin = LineJoin.Round };
            using SolidBrush shadow = new(Color.FromArgb(60, 0, 0, 0));
            g.TranslateTransform(S(1.5f), S(1.5f));
            g.FillPolygon(shadow, arrow);
            g.ResetTransform();
            g.FillPolygon(white, arrow);
            g.DrawPolygon(outline, arrow);
        }

        _cursor.Present(bmp, new Point(_cursorAt.X - pad, _cursorAt.Y - pad));
    }

    // ------------------------------------------------------------------ highlight ring

    public void SetRing(Rectangle rect)
    {
        if (rect != _ringRect)
        {
            _ringRect = rect;
            RedrawRing();
        }
    }

    private void RedrawRing()
    {
        if (_hidden || _ringRect.Width <= 0 || _ringRect.Height <= 0)
        {
            _ring.Hide();
            return;
        }

        int pad = S(10);
        Rectangle outer = Rectangle.Inflate(_ringRect, pad, pad);
        using Bitmap bmp = new(outer.Width, outer.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Rectangle r = new(pad - S(3), pad - S(3), _ringRect.Width + S(6), _ringRect.Height + S(6));
            for (int i = 3; i >= 1; i--)
            {
                using Pen glow = new(Color.FromArgb(28 * (4 - i), Accent), S(3) + i * S(3));
                using GraphicsPath glowPath = IconFactory.CreateRoundedRect(r, S(8));
                g.DrawPath(glow, glowPath);
            }

            using Pen pen = new(Accent, S(2.5f));
            using GraphicsPath path = IconFactory.CreateRoundedRect(r, S(8));
            g.DrawPath(pen, path);
        }

        _ring.Present(bmp, outer.Location);
    }

    // ------------------------------------------------------------------ caption and key

    /// <summary>The demo window's bounds: the caption is centered near its bottom.</summary>
    public void SetWindow(Rectangle window)
    {
        if (window != _window)
        {
            _window = window;
            RedrawCaption();
        }
    }

    public void SetCaption(string? text)
    {
        if (text != _captionText)
        {
            _captionText = text;
            RedrawCaption();
        }
    }

    public void SetKey(string? key)
    {
        if (key != _keyText)
        {
            _keyText = key;
            RedrawCaption();
        }
    }

    private void RedrawCaption()
    {
        if (_hidden || _window.Width <= 0 || (string.IsNullOrEmpty(_captionText) && string.IsNullOrEmpty(_keyText)))
        {
            _caption.Hide();
            return;
        }

        int width = Math.Min(_window.Width - S(80), S(820));
        if (width < S(200))
        {
            _caption.Hide();
            return;
        }

        using Font font = new("Segoe UI", 13f * _scale, FontStyle.Regular, GraphicsUnit.Point);
        using Font keyFont = new("Segoe UI", 12f * _scale, FontStyle.Bold, GraphicsUnit.Point);
        using StringFormat format = new() { Alignment = StringAlignment.Center };
        int padX = S(18), padY = S(10);

        SizeF textSize = SizeF.Empty;
        Size keySize = Size.Empty;
        using (Bitmap probe = new(1, 1))
        using (Graphics pg = Graphics.FromImage(probe))
        {
            pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (!string.IsNullOrEmpty(_captionText))
            {
                textSize = pg.MeasureString(_captionText, font, width - padX * 2, format);
            }

            if (!string.IsNullOrEmpty(_keyText))
            {
                SizeF k = pg.MeasureString(_keyText, keyFont);
                keySize = new Size((int)Math.Ceiling(k.Width) + S(22), (int)Math.Ceiling(k.Height) + S(10));
            }
        }

        int boxHeight = textSize.IsEmpty ? 0 : (int)Math.Ceiling(textSize.Height) + padY * 2;
        int gap = boxHeight > 0 && keySize.Height > 0 ? S(10) : 0;
        int height = keySize.Height + gap + boxHeight + S(4);

        using Bitmap bmp = new(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (keySize.Height > 0)
            {
                Rectangle chip = new((width - keySize.Width) / 2, 0, keySize.Width, keySize.Height);
                using GraphicsPath chipPath = IconFactory.CreateRoundedRect(chip, S(7));
                using SolidBrush chipBg = new(Color.FromArgb(245, 250, 250, 252));
                using Pen chipBorder = new(Color.FromArgb(150, 90, 90, 100), Math.Max(1, S(1)));
                using Pen chipEdge = new(Color.FromArgb(120, 90, 90, 100), S(3));
                g.DrawLine(chipEdge, chip.Left + S(5), chip.Bottom - S(1), chip.Right - S(5), chip.Bottom - S(1));
                g.FillPath(chipBg, chipPath);
                g.DrawPath(chipBorder, chipPath);
                using SolidBrush keyBrush = new(Color.FromArgb(28, 28, 34));
                using StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(_keyText, keyFont, keyBrush, chip, center);
            }

            if (boxHeight > 0)
            {
                Rectangle box = new(0, keySize.Height + gap, width, boxHeight);
                int boxWidth = Math.Min(width, (int)Math.Ceiling(textSize.Width) + padX * 2 + S(8));
                box = new Rectangle((width - boxWidth) / 2, box.Y, boxWidth, box.Height);
                using GraphicsPath boxPath = IconFactory.CreateRoundedRect(box, S(10));
                using SolidBrush boxBg = new(Color.FromArgb(232, 32, 32, 38));
                g.FillPath(boxBg, boxPath);
                // A light edge and accent line keep the caption visible on the dark theme too
                using Pen edge = new(Color.FromArgb(110, 255, 255, 255), Math.Max(1, S(1)));
                g.DrawPath(edge, boxPath);
                using Pen accentLine = new(Accent, S(3));
                g.DrawLine(accentLine, box.Left + S(10), box.Bottom - S(2), box.Right - S(10), box.Bottom - S(2));
                using SolidBrush text = new(Color.White);
                RectangleF textRect = new(box.X + padX, box.Y + padY, box.Width - padX * 2, box.Height - padY * 2 + S(2));
                g.DrawString(_captionText, font, text, textRect, format);
            }
        }

        // Above the status bar and the tour's control bar
        Point location = new(_window.Left + (_window.Width - width) / 2, _window.Bottom - S(92) - height);
        _caption.Present(bmp, location);
    }

    public void Dispose()
    {
        _cursor.Dispose();
        _ring.Dispose();
        _caption.Dispose();
    }
}
