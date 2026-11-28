using System.Drawing.Drawing2D;

namespace Explorer2.UI;

/// <summary>
/// Total Commander style function key buttons (F3 View … F8 Delete), hosted centered inside the
/// status bar in split mode. All buttons share one width: the widest caption plus padding.
/// </summary>
internal sealed class FunctionKeyBar : Control
{
    private static readonly (Keys Key, string Label)[] Buttons =
    {
        (Keys.F3, "View"),
        (Keys.F4, "Edit"),
        (Keys.F5, "Copy"),
        (Keys.F6, "Move"),
        (Keys.F7, "New Folder"),
        (Keys.F8, "Delete")
    };

    private const int HorizontalPadding = 10;
    private const int KeyLabelGap = 4;
    private const int ButtonSpacing = 2;

    private readonly Color _bg;
    private readonly Color _hoverBg;
    private readonly Color _pressedBg;
    private readonly Color _textColor;
    private readonly Color _separatorColor;
    private readonly Font _labelFont;
    private readonly Font _keyFont;
    private int _buttonWidth;
    private int _hoverIndex = -1;
    private int _pressedIndex = -1;

    public event Action<Keys>? FunctionKeyClicked;

    public FunctionKeyBar(bool isDarkMode, Color background)
    {
        _bg = background;
        _hoverBg = isDarkMode ? Color.FromArgb(58, 58, 66) : Color.FromArgb(226, 226, 232);
        _pressedBg = isDarkMode ? Color.FromArgb(70, 70, 80) : Color.FromArgb(214, 214, 222);
        _textColor = isDarkMode ? Color.FromArgb(244, 244, 248) : Color.FromArgb(22, 22, 26);
        _separatorColor = isDarkMode ? Color.FromArgb(58, 58, 66) : Color.FromArgb(212, 212, 218);
        _labelFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        _keyFont = new Font("Segoe UI", 9f, FontStyle.Bold);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);

        TabStop = false;
        BackColor = _bg;
        AccessibleName = "Function keys";
        Text = "Function keys"; // UI Automation names the bar from its window text (not painted)
        _buttonWidth = MeasureButtonWidth();
    }

    /// <summary>Width needed to show all buttons at their uniform size.</summary>
    public int PreferredBarWidth => Buttons.Length * _buttonWidth + (Buttons.Length - 1) * ButtonSpacing;

    private int MeasureButtonWidth()
    {
        int widest = 0;
        foreach ((Keys key, string label) in Buttons)
        {
            int w = TextRenderer.MeasureText(key.ToString(), _keyFont, Size.Empty, TextFormatFlags.NoPadding).Width +
                    KeyLabelGap +
                    TextRenderer.MeasureText(label, _labelFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            widest = Math.Max(widest, w);
        }

        return widest + HorizontalPadding * 2;
    }

    /// <summary>Screen rectangle of a function key's button (the guided tour points at it).</summary>
    internal Rectangle ButtonScreenRect(Keys key) =>
        Array.FindIndex(Buttons, b => b.Key == key) is >= 0 and var i ? RectangleToScreen(GetButtonRect(i)) : Rectangle.Empty;

    private Rectangle GetButtonRect(int index) =>
        new(index * (_buttonWidth + ButtonSpacing), 0, _buttonWidth, Height);

    private int HitTest(Point p)
    {
        for (int i = 0; i < Buttons.Length; i++)
        {
            if (GetButtonRect(i).Contains(p))
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit != _hoverIndex)
        {
            _hoverIndex = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _pressedIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressedIndex = HitTest(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int hit = HitTest(e.Location);
        int pressed = _pressedIndex;
        _pressedIndex = -1;
        Invalidate();

        if (e.Button == MouseButtons.Left && hit >= 0 && hit == pressed)
        {
            FunctionKeyClicked?.Invoke(Buttons[hit].Key);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(_bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using Pen sep = new(_separatorColor, 1f);

        for (int i = 0; i < Buttons.Length; i++)
        {
            Rectangle r = GetButtonRect(i);
            if (r.Left >= Width)
            {
                break;
            }

            if (i == _pressedIndex || i == _hoverIndex)
            {
                using GraphicsPath path = IconFactory.CreateRoundedRect(new Rectangle(r.X, r.Y + 1, r.Width - 1, r.Height - 3), 4);
                using SolidBrush fill = new(i == _pressedIndex ? _pressedBg : _hoverBg);
                g.FillPath(fill, path);
            }
            else if (i > 0 && i - 1 != _hoverIndex && i - 1 != _pressedIndex)
            {
                int sx = r.X - ButtonSpacing / 2 - 1;
                g.DrawLine(sep, sx, r.Y + 6, sx, r.Bottom - 6);
            }

            string key = Buttons[i].Key.ToString();
            string label = Buttons[i].Label;
            Size keySize = TextRenderer.MeasureText(g, key, _keyFont, Size.Empty, TextFormatFlags.NoPadding);
            Size labelSize = TextRenderer.MeasureText(g, label, _labelFont, Size.Empty, TextFormatFlags.NoPadding);
            int contentWidth = keySize.Width + KeyLabelGap + labelSize.Width;
            int x = r.X + (r.Width - contentWidth) / 2;

            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, key, _keyFont, new Rectangle(x, r.Y, keySize.Width + 1, r.Height), _textColor, flags);
            TextRenderer.DrawText(g, label, _labelFont, new Rectangle(x + keySize.Width + KeyLabelGap, r.Y, labelSize.Width + 1, r.Height), _textColor, flags);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont.Dispose();
            _keyFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
