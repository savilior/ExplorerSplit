using System.Drawing.Drawing2D;
using Explorer2.Interop;

namespace Explorer2.UI;

internal sealed class Win11MenuRenderer : ToolStripProfessionalRenderer
{
    private readonly Color _bgColor;
    private readonly Color _borderColor;
    private readonly Color _hoverColor;
    private readonly Color _textColor;
    private readonly Color _disabledTextColor;
    private readonly Color _separatorColor;
    private readonly Color _accentColor = Color.FromArgb(0, 120, 212);

    public Win11MenuRenderer(bool isDarkMode) : base(new Win11ColorTable(isDarkMode))
    {
        RoundedEdges = false;

        _bgColor = isDarkMode ? Color.FromArgb(44, 44, 50) : Color.FromArgb(249, 249, 252);
        _borderColor = isDarkMode ? Color.FromArgb(68, 68, 76) : Color.FromArgb(216, 216, 224);
        _hoverColor = isDarkMode ? Color.FromArgb(62, 62, 70) : Color.FromArgb(232, 232, 239);
        _textColor = isDarkMode ? Color.FromArgb(244, 244, 248) : Color.FromArgb(24, 24, 28);
        _disabledTextColor = isDarkMode ? Color.FromArgb(130, 130, 140) : Color.FromArgb(145, 145, 155);
        _separatorColor = isDarkMode ? Color.FromArgb(62, 62, 70) : Color.FromArgb(224, 224, 230);
    }

    public static ContextMenuStrip CreateMenu(bool isDarkMode, Font font)
    {
        Win11MenuRenderer renderer = new(isDarkMode);
        ContextMenuStrip menu = new()
        {
            Renderer = renderer,
            Font = font,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = renderer._bgColor,
            ForeColor = renderer._textColor,
            Padding = new Padding(4, 6, 4, 6)
        };

        ApplyRoundedCornersOnHandle(menu);
        return menu;
    }

    public static ToolStripMenuItem CreateMenuItem(
        string text,
        bool isDarkMode,
        EventHandler? onClick = null,
        bool isChecked = false,
        string? shortcutDisplay = null,
        bool isCheckBox = false)
    {
        ToolStripMenuItem item = new(text)
        {
            Checked = isChecked,
            Tag = isCheckBox ? "checkbox" : null,
            Padding = new Padding(6, 5, 12, 5),
            Margin = new Padding(0, 1, 0, 1),
            ShortcutKeyDisplayString = shortcutDisplay ?? string.Empty
        };

        if (onClick is not null)
        {
            item.Click += onClick;
        }

        item.DropDownOpened += (_, _) =>
        {
            if (item.DropDown is ToolStripDropDownMenu subMenu)
            {
                subMenu.ShowImageMargin = false;
                subMenu.ShowCheckMargin = true;
                subMenu.Padding = new Padding(4, 6, 4, 6);
                subMenu.BackColor = isDarkMode ? Color.FromArgb(44, 44, 50) : Color.FromArgb(249, 249, 252);
                ApplyRoundedCorners(subMenu.Handle);
            }
        };

        return item;
    }

    private static void ApplyRoundedCornersOnHandle(ToolStripDropDown dropDown)
    {
        dropDown.HandleCreated += (_, _) => ApplyRoundedCorners(dropDown.Handle);
        dropDown.Opened += (_, _) => ApplyRoundedCorners(dropDown.Handle);
    }

    private static void ApplyRoundedCorners(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int pref = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                handle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref pref,
                sizeof(int));
        }
        catch
        {
            // Ignore on older OS versions
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using SolidBrush b = new(_bgColor);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Suppress classic vertical gradient gutter
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using Pen p = new(_borderColor, 1f);
        Rectangle r = new(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        e.Graphics.DrawRectangle(p, r);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle hoverRect = new(3, 1, e.Item.Width - 6, e.Item.Height - 2);
        using GraphicsPath path = IconFactory.CreateRoundedRect(hoverRect, 4);
        using SolidBrush b = new(_hoverColor);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _textColor : _disabledTextColor;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int cx = e.ImageRectangle.Left + e.ImageRectangle.Width / 2;
        int cy = e.Item.Height / 2;

        if (e.Item.Tag is string tag && tag == "checkbox")
        {
            using Pen checkPen = new(_textColor, 1.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(checkPen, new Point[]
            {
                new(cx - 4, cy),
                new(cx - 1, cy + 3),
                new(cx + 5, cy - 3)
            });
            return;
        }

        // Draw Windows 11 crisp radio dot bullet '•'
        using SolidBrush dotBrush = new(_accentColor);
        e.Graphics.FillEllipse(dotBrush, cx - 3, cy - 3, 6, 6);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using Pen p = new(_separatorColor, 1f);
        e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = e.ArrowRectangle;
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;

        using Pen p = new(_textColor, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(p, cx - 2, cy - 4, cx + 2, cy);
        e.Graphics.DrawLine(p, cx + 2, cy, cx - 2, cy + 4);
    }

    private sealed class Win11ColorTable : ProfessionalColorTable
    {
        private readonly Color _bg;
        private readonly Color _border;

        public Win11ColorTable(bool isDarkMode)
        {
            UseSystemColors = false;
            _bg = isDarkMode ? Color.FromArgb(44, 44, 50) : Color.FromArgb(249, 249, 252);
            _border = isDarkMode ? Color.FromArgb(68, 68, 76) : Color.FromArgb(216, 216, 224);
        }

        public override Color ToolStripDropDownBackground => _bg;
        public override Color MenuBorder => _border;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
    }
}
