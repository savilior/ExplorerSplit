using System.Drawing.Drawing2D;

namespace Explorer2.UI;

/// <summary>
/// Total Commander style function key bar (F3 View … F8 Delete) shown at the bottom in split mode.
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

    private readonly Color _bg;
    private readonly Color _hoverBg;
    private readonly Color _pressedBg;
    private readonly Color _textColor;
    private readonly Color _keyColor;
    private readonly Color _separatorColor;
    private readonly Font _labelFont;
    private readonly Font _keyFont;
    private int _hoverIndex = -1;
    private int _pressedIndex = -1;

    public event Action<Keys>? FunctionKeyClicked;

    public FunctionKeyBar(bool isDarkMode)
    {
        _bg = isDarkMode ? Color.FromArgb(32, 32, 36) : Color.FromArgb(248, 248, 251);
        _hoverBg = isDarkMode ? Color.FromArgb(54, 54, 62) : Color.FromArgb(230, 230, 236);
        _pressedBg = isDarkMode ? Color.FromArgb(66, 66, 76) : Color.FromArgb(218, 218, 226);
        _textColor = isDarkMode ? Color.FromArgb(244, 244, 248) : Color.FromArgb(22, 22, 26);
        _keyColor = Color.FromArgb(0, 120, 212);
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

        Dock = DockStyle.Bottom;
        Height = 30;
        TabStop = false;
    }

    private Rectangle GetButtonRect(int index)
    {
        int count = Buttons.Length;
        int width = Width / count;
        int x = index * width;
        int w = index == count - 1 ? Width - x : width;
        return new Rectangle(x, 0, w, Height);
    }

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

        using Pen sep = new(_separatorColor, 1f);
        g.DrawLine(sep, 0, 0, Width, 0);

        for (int i = 0; i < Buttons.Length; i++)
        {
            Rectangle r = GetButtonRect(i);
            Rectangle inner = new(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);

            if (i == _pressedIndex || i == _hoverIndex)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using GraphicsPath path = IconFactory.CreateRoundedRect(inner, 4);
                using SolidBrush fill = new(i == _pressedIndex ? _pressedBg : _hoverBg);
                g.FillPath(fill, path);
            }

            if (i > 0)
            {
                g.DrawLine(sep, r.X, r.Y + 7, r.X, r.Bottom - 7);
            }

            string key = Buttons[i].Key.ToString();
            string label = Buttons[i].Label;
            Size keySize = TextRenderer.MeasureText(g, key, _keyFont, Size.Empty, TextFormatFlags.NoPadding);
            Size labelSize = TextRenderer.MeasureText(g, label, _labelFont, Size.Empty, TextFormatFlags.NoPadding);
            int gap = 5;
            int total = keySize.Width + gap + labelSize.Width;
            int x = r.X + Math.Max(4, (r.Width - total) / 2);

            TextRenderer.DrawText(g, key, _keyFont, new Rectangle(x, r.Y, keySize.Width + 2, r.Height), _keyColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, label, _labelFont, new Rectangle(x + keySize.Width + gap, r.Y, r.Right - x - keySize.Width - gap, r.Height), _textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
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
