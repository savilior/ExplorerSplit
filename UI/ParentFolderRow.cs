using System.Drawing.Drawing2D;

namespace Explorer2.UI;

/// <summary>
/// Total Commander style "[..]" row shown at the top of a pane in split mode. The row never takes
/// Windows keyboard focus (that stays in the file list, so window re-activation cannot strand focus
/// here); instead it has a virtual cursor (<see cref="IsCursorOn"/>) that the form moves onto it with
/// Up from the first item or Home, and keys are handled by the form while the cursor is on it.
/// Enter or a double-click goes up one folder.
/// </summary>
internal sealed class ParentFolderRow : Control
{
    private readonly Color _bg;
    private readonly Color _hoverBg;
    private readonly Color _focusBg;
    private readonly Color _focusBorder;
    private readonly Color _textColor;
    private readonly Color _iconColor;
    private bool _hovered;
    private bool _isCursorOn;

    public event Action? ActivateRequested;
    public event Action? Clicked;

    /// <summary>True while the pane's keyboard cursor is on the [..] row.</summary>
    public bool IsCursorOn
    {
        get => _isCursorOn;
        set
        {
            if (_isCursorOn != value)
            {
                _isCursorOn = value;
                Invalidate();
            }
        }
    }

    public ParentFolderRow(bool isDarkMode)
    {
        _bg = isDarkMode ? Color.FromArgb(25, 25, 25) : Color.White;
        _hoverBg = isDarkMode ? Color.FromArgb(45, 45, 50) : Color.FromArgb(229, 243, 255);
        _focusBg = isDarkMode ? Color.FromArgb(70, 70, 78) : Color.FromArgb(204, 232, 255);
        _focusBorder = isDarkMode ? Color.FromArgb(110, 110, 120) : Color.FromArgb(153, 209, 255);
        _textColor = isDarkMode ? Color.FromArgb(244, 244, 248) : Color.FromArgb(24, 24, 28);
        _iconColor = isDarkMode ? Color.FromArgb(240, 200, 90) : Color.FromArgb(220, 170, 50);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);

        Dock = DockStyle.Top;
        Height = 24;
        TabStop = false;
        Cursor = Cursors.Default;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        AccessibleName = "Up one folder";
        AccessibleRole = AccessibleRole.ListItem;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Clicked?.Invoke();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left)
        {
            ActivateRequested?.Invoke();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(_bg);

        Rectangle rowRect = new(4, 1, Width - 8, Height - 2);
        if (_isCursorOn || _hovered)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = IconFactory.CreateRoundedRect(rowRect, 3);
            using SolidBrush fill = new(_isCursorOn ? _focusBg : _hoverBg);
            g.FillPath(fill, path);
            if (_isCursorOn)
            {
                using Pen border = new(_focusBorder, 1f);
                g.DrawPath(border, path);
            }
        }

        // Folder glyph with an up arrow
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int iconX = 14;
        int iconY = (Height - 14) / 2;
        using (SolidBrush folder = new(_iconColor))
        {
            g.FillRectangle(folder, iconX, iconY + 2, 7, 3);
            g.FillRectangle(folder, iconX, iconY + 4, 16, 10);
        }

        using (Pen arrow = new(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            int ax = iconX + 8;
            g.DrawLine(arrow, ax, iconY + 12, ax, iconY + 6);
            g.DrawLine(arrow, ax - 3, iconY + 9, ax, iconY + 6);
            g.DrawLine(arrow, ax + 3, iconY + 9, ax, iconY + 6);
        }

        Rectangle textRect = new(iconX + 24, 0, Width - iconX - 28, Height);
        TextRenderer.DrawText(
            g,
            "[..]",
            Font,
            textRect,
            _textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font.Dispose();
        }

        base.Dispose(disposing);
    }
}
