using System.Runtime.InteropServices;
using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>What the Favorites lists need from the navigation pane that hosts them.</summary>
internal interface IFavoritesHost
{
    bool IsDarkMode { get; }

    /// <summary>The window the lists belong to (owner of flyouts, menus and dialogs).</summary>
    Form? OwnerForm { get; }

    FavoritesFlyouts Flyouts { get; }

    /// <summary>Opens the folder in the active pane.</summary>
    void OpenFolder(string path);

    void OpenInNewTab(string path);

    /// <summary>Null when there is no other pane (split mode is off).</summary>
    Action<string>? OpenInOtherPane { get; }

    /// <summary>Down arrow on the last favorite: on to the first item of the folder tree below.</summary>
    void FocusTreeBelow();
}

/// <summary>
/// The Favorites list, drawn to look like the navigation tree under it (same rows, icons, arrows and
/// hover / selection colors, taken from the tree's theme). At the top of the navigation pane it also
/// shows the fixed rows of <see cref="TopSection"/> (Home, Gallery, Recycle Bin, Desktop, Downloads, Documents):
/// they can't be unpinned, only dragged among themselves or turned off from the tray menu. A group's flyout shows the group's entries.
///
/// Mouse: click opens (a folder in the active pane, a file with its app), the icon at the right of a
/// folder opens it in a new tab, the arrow or a click on a group opens it in place, resting on a
/// closed group shows its entries in a flyout. Drag entries to reorder them or move them between
/// groups; drop folders and files from anywhere to add them. Right-click for the menu.
/// </summary>
internal sealed class FavoritesView : Control
{
    private const int HoverOpenDelay = 350;
    private const int DragHoverOpenDelay = 600;

    private enum RowKind { Fixed, Shortcut, Group, Hint }

    private sealed class Row
    {
        public RowKind Kind;
        public FavoriteNode? Node;
        public string? Path;
        public string? FixedName;

        /// <summary>Id of a fixed row (see <see cref="TopSection"/>).</summary>
        public string? FixedId;
        public int Level;

        /// <summary>The group whose list holds this row's node (null = the view's own list).</summary>
        public FavoriteNode? OwnerGroup;
        public int IndexInOwner;
    }

    private readonly record struct DropPlan(FavoritesViewDropAction Action, FavoriteNode? Group, int Index, int LineY, int LineLevel, int RowIndex);

    private readonly IFavoritesHost _host;
    private readonly FavoriteNode? _group;
    private readonly bool _isTopList;
    private readonly int _rowHeight;
    private readonly int _padTop;
    private readonly List<Row> _rows = new();
    private readonly Color _bgColor;
    private readonly Color _fgColor;
    private readonly Color _dimColor;
    private readonly Color _dividerColor;
    private readonly Color _accentColor;
    private readonly System.Windows.Forms.Timer _hoverTimer = new();
    private readonly System.Windows.Forms.Timer _keyNavigateTimer = new() { Interval = 250 };
    private readonly ToolTip _toolTip = new() { InitialDelay = 600, ReshowDelay = 200 };
    private Font? _glyphFont;
    private IntPtr _theme;
    private FavoritesDropTarget? _dropTarget;

    private int _hot = -1;
    private bool _hotNewTab;
    private bool _hotChevron;
    private int _caret = -1;
    private bool _mouseInside;
    private int _scrollY;
    private int _thumbDragOffset = -1;
    private bool _thumbHot;

    private Point _mouseDownPoint;
    private int _mouseDownRow = -1;
    private MouseButtons _mouseDownButton;

    private DropPlan _drop;
    private int _dragHoverRow = -1;
    private long _dragHoverSince;

    /// <summary>The entry being dragged by one of the Favorites lists (internal reordering), or null.</summary>
    internal static FavoriteNode? DraggedNode { get; private set; }

    /// <summary>The fixed top row being dragged to reorder the top section, or null.</summary>
    internal static string? DraggedFixedId { get; private set; }

    private int _fixedCount;

    /// <summary>The flyout this list is in, or null for the list in the navigation pane.</summary>
    public FavoritesFlyoutForm? Flyout { get; set; }

    public FavoriteNode? Group => _group;

    /// <summary>Raised when the height the rows need changed.</summary>
    public event Action? ContentHeightChanged;

    public FavoritesView(IFavoritesHost host, FavoriteNode? group, int rowHeight)
    {
        _host = host;
        _group = group;
        _isTopList = group is null;
        _rowHeight = rowHeight;
        _padTop = _isTopList ? 0 : 4;

        // The window text names the list for accessibility tools; tools\record-guide.ps1 also checks
        // it, so a scripted drop lands only on a Favorites list, never in the folder tree
        Text = "Explorer2 Favorites";
        AccessibleName = "Favorites";

        bool dark = host.IsDarkMode;
        _bgColor = dark ? Color.FromArgb(25, 25, 28) : Color.FromArgb(255, 255, 255);
        _fgColor = dark ? Color.FromArgb(242, 242, 246) : Color.FromArgb(24, 24, 28);
        _dimColor = dark ? Color.FromArgb(150, 150, 158) : Color.FromArgb(120, 120, 128);
        _dividerColor = dark ? Color.FromArgb(52, 52, 58) : Color.FromArgb(226, 226, 232);
        _accentColor = dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
        if (!_isTopList)
        {
            _bgColor = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(251, 251, 253);
        }

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = false;
        BackColor = _bgColor;
        ForeColor = _fgColor;
        Font = System.Drawing.SystemFonts.IconTitleFont ??new Font("Segoe UI", 9f);

        _hoverTimer.Tick += (_, _) => OnHoverTimer();
        _keyNavigateTimer.Tick += (_, _) =>
        {
            _keyNavigateTimer.Stop();
            if (_caret >= 0 && _caret < _rows.Count && IsFolderRow(_rows[_caret]))
            {
                _host.OpenFolder(_rows[_caret].Path!);
            }
        };

        Favorites.Changed += OnFavoritesChanged;
        if (_isTopList)
        {
            TopSection.Changed += OnFavoritesChanged;
        }

        FavoritesShellInfo.Updated += OnShellInfoUpdated;
        Rebuild();
    }

    // ---------------------------------------------------------------- rows

    private List<FavoriteNode> OwnList => _group is null ? Favorites.Roots : _group.Children ?? new List<FavoriteNode>();

    private void OnFavoritesChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        string? selectedPath = _caret >= 0 && _caret < _rows.Count ? _rows[_caret].Path : null;
        FavoriteNode? selectedNode = _caret >= 0 && _caret < _rows.Count ? _rows[_caret].Node : null;
        HashSet<FavoriteNode> before = _rows.Where(r => r.Node is not null).Select(r => r.Node!).ToHashSet();
        Rebuild();
        _caret = selectedNode is not null ? _rows.FindIndex(r => r.Node == selectedNode)
            : selectedPath is not null ? _rows.FindIndex(r => r.Path == selectedPath) : -1;

        // A new entry (added from a menu or dropped) scrolls into view
        int added = before.Count == 0 ? -1 : _rows.FindIndex(r => r.Node is not null && !before.Contains(r.Node));
        if (added >= 0)
        {
            EnsureVisible(added);
        }
        FavoritesShellInfo.Refresh(_rows.Where(r => r.Path is not null).Select(r => r.Path!));
    }

    private void OnShellInfoUpdated()
    {
        if (!IsDisposed)
        {
            Invalidate();
            Flyout?.UpdateSize();
        }
    }

    private void Rebuild()
    {
        int oldHeight = ContentHeight;
        _rows.Clear();
        if (_isTopList)
        {
            foreach (TopSection.Item item in TopSection.Visible)
            {
                _rows.Add(new Row { Kind = RowKind.Fixed, Path = item.Path, FixedName = item.Name, FixedId = item.Id });
            }
        }

        _fixedCount = _rows.Count;
        AddRows(OwnList, _group, 0);
        if (OwnList.Count == 0)
        {
            _rows.Add(new Row { Kind = RowKind.Hint, OwnerGroup = _group });
        }

        _hot = -1;
        ClampScroll();
        Invalidate();
        if (ContentHeight != oldHeight)
        {
            ContentHeightChanged?.Invoke();
        }
    }

    private void AddRows(List<FavoriteNode> list, FavoriteNode? owner, int level)
    {
        for (int i = 0; i < list.Count; i++)
        {
            FavoriteNode node = list[i];
            _rows.Add(new Row
            {
                Kind = node.IsGroup ? RowKind.Group : RowKind.Shortcut,
                Node = node,
                Path = node.Path,
                Level = level,
                OwnerGroup = owner,
                IndexInOwner = i
            });

            if (node.Children is not null && node.Expanded)
            {
                AddRows(node.Children, node, level + 1);
            }
        }
    }

    private int FixedRowCount => _fixedCount;

    /// <summary>Height the rows need (the pane gives the list at most part of its height; the rest scrolls).</summary>
    public int ContentHeight => _padTop * 2 + _rows.Count * _rowHeight + (_isTopList ? 1 : 0);

    /// <summary>Width a flyout needs to show its longest name.</summary>
    public int PreferredContentWidth
    {
        get
        {
            int widest = 0;
            foreach (Row row in _rows)
            {
                int text = TextRenderer.MeasureText(RowText(row), Font).Width;
                widest = Math.Max(widest, TextLeft(row.Level) + text);
            }

            return widest + Scale(44);
        }
    }

    private string RowText(Row row) => row.Kind switch
    {
        RowKind.Group => row.Node!.Name ?? "Group",
        RowKind.Hint => _isTopList ? "Drag folders here" : "Empty - drag folders here",
        RowKind.Fixed => FavoritesShellInfo.Get(row.Path!) is { Exists: true } fixedInfo && !fixedInfo.DisplayName.StartsWith("::", StringComparison.Ordinal)
            ? fixedInfo.DisplayName
            : row.FixedName!,
        _ => !string.IsNullOrWhiteSpace(row.Node!.Name) ? row.Node.Name! : FavoritesShellInfo.Get(row.Path!).DisplayName
    };

    private bool IsFolderRow(Row row) =>
        row.Kind == RowKind.Fixed ||
        (row.Kind == RowKind.Shortcut && FavoritesShellInfo.Get(row.Path!) is { IsFolder: true, Exists: true });

    /// <summary>A group or a link with nested links: an arrow at its left expands it in place.</summary>
    private static bool HasArrow(Row row) => row.Node is { HasEntries: true } && row.Kind is RowKind.Group or RowKind.Shortcut;

    /// <summary>Resting on the row shows its entries in a flyout (a group, even an empty one, or a link with nested links).</summary>
    private static bool CanFlyout(Row row) => row.Kind == RowKind.Group || (row.Kind == RowKind.Shortcut && row.Node!.HasEntries);

    // ---------------------------------------------------------------- geometry

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    // Measured on the folder tree below (root: arrow, icon, name; each level 20 px further in)
    private int ChevronCenter(int level) => Scale(6 + level * 20);

    private int IconLeft(int level) => Scale(19 + level * 20);

    private int TextLeft(int level) => Scale(40 + level * 20);

    private int RowTop(int index) => _padTop + index * _rowHeight - _scrollY;

    private Rectangle RowRect(int index) => new(0, RowTop(index), ClientSize.Width, _rowHeight);

    private Rectangle NewTabRect(int index)
    {
        Rectangle row = RowRect(index);
        int size = Scale(24);
        return new Rectangle(row.Right - size - Scale(6) - (HasScroll ? Scale(4) : 0), row.Top + (row.Height - size) / 2, size, size);
    }

    private Rectangle ChevronRect(int index)
    {
        Rectangle row = RowRect(index);
        int c = ChevronCenter(_rows[index].Level);
        return new Rectangle(c - Scale(9), row.Top, Scale(18), row.Height);
    }

    private int RowAt(int y)
    {
        if (y < _padTop - _scrollY)
        {
            return -1;
        }

        int index = (y - _padTop + _scrollY) / _rowHeight;
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    /// <summary>Screen rectangle of a row (where its flyout is anchored).</summary>
    private Rectangle RowScreenRect(int index) => RectangleToScreen(RowRect(index));

    // ---------------------------------------------------------------- scrolling

    private bool HasScroll => ContentHeight > ClientSize.Height + 1;

    private int MaxScroll => Math.Max(0, ContentHeight - ClientSize.Height);

    private void ClampScroll() => _scrollY = Math.Clamp(_scrollY, 0, MaxScroll);

    private void ScrollBy(int delta)
    {
        int old = _scrollY;
        _scrollY += delta;
        ClampScroll();
        if (_scrollY != old)
        {
            Invalidate();
        }
    }

    private void EnsureVisible(int index)
    {
        if (index < 0)
        {
            return;
        }

        int top = _padTop + index * _rowHeight;
        if (top < _scrollY)
        {
            _scrollY = top;
        }
        else if (top + _rowHeight > _scrollY + ClientSize.Height)
        {
            _scrollY = top + _rowHeight - ClientSize.Height;
        }

        ClampScroll();
        Invalidate();
    }

    private Rectangle ThumbRect()
    {
        if (!HasScroll)
        {
            return Rectangle.Empty;
        }

        int track = ClientSize.Height - 4;
        int thumb = Math.Max(Scale(24), track * ClientSize.Height / Math.Max(1, ContentHeight));
        int top = 2 + (track - thumb) * _scrollY / Math.Max(1, MaxScroll);
        int width = _thumbHot || _thumbDragOffset >= 0 ? Scale(6) : Scale(3);
        return new Rectangle(ClientSize.Width - width - 2, top, width, thumb);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-e.Delta / 120 * _rowHeight * 3);
    }

    // ---------------------------------------------------------------- selection (the active folder)

    /// <summary>Highlights the first visible row showing <paramref name="path"/>; false when none does.</summary>
    public bool SelectPath(string? path)
    {
        int index = path is null ? -1 : _rows.FindIndex(r => r.Path is not null && Favorites.SamePath(r.Path, path));
        if (index != _caret)
        {
            _caret = index;
            Invalidate();
        }

        return index >= 0;
    }

    public bool HasSelection => _caret >= 0;

    public void ClearSelection()
    {
        if (_caret >= 0)
        {
            _caret = -1;
            Invalidate();
        }
    }

    /// <summary>
    /// Keyboard focus to the list, on the highlighted row (with <paramref name="last"/>: on the last
    /// row, which opens). With nothing highlighted no row is selected; the first arrow key picks one.
    /// </summary>
    public void FocusList(bool last = false)
    {
        if (last)
        {
            _caret = _rows.Count - 1;
        }

        Focus();
        EnsureVisible(_caret);
        if (last && _caret >= 0 && IsFolderRow(_rows[_caret]))
        {
            _host.OpenFolder(_rows[_caret].Path!);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    // ---------------------------------------------------------------- painting

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _theme = OpenThemeData(Handle, _host.IsDarkMode ? "DarkMode_Explorer::TreeView" : "Explorer::TreeView");
        if (_theme == IntPtr.Zero)
        {
            _theme = OpenThemeData(Handle, "TreeView");
        }

        Application.OleRequired(); // RegisterDragDrop needs OLE on this thread
        _dropTarget = new FavoritesDropTarget(this);
        if (ShellBrowserInterop.RegisterDragDrop(Handle, _dropTarget) != 0)
        {
            _dropTarget = null;
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_theme != IntPtr.Zero)
        {
            CloseThemeData(_theme);
            _theme = IntPtr.Zero;
        }

        if (_dropTarget is not null)
        {
            ShellBrowserInterop.RevokeDragDrop(Handle);
            _dropTarget = null;
        }

        base.OnHandleDestroyed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(_bgColor);
        _glyphFont ??= CreateGlyphFont();

        bool showChevrons = _mouseInside || Focused || !_isTopList || DraggedNode is not null || _drop.Action != FavoritesViewDropAction.None;
        for (int i = 0; i < _rows.Count; i++)
        {
            Rectangle row = RowRect(i);
            if (row.Bottom < e.ClipRectangle.Top || row.Top > e.ClipRectangle.Bottom)
            {
                continue;
            }

            PaintRow(g, i, row, showChevrons);
        }

        if (_isTopList && FixedRowCount > 0 && _rows.Count > FixedRowCount)
        {
            // Dividers as File Explorer draws them between the navigation pane's groups
            using Pen divider = new(_dividerColor, 1f);
            int y = RowTop(FixedRowCount) - 1;
            if (y > 1)
            {
                g.DrawLine(divider, Scale(12), y, ClientSize.Width - Scale(12), y);
            }
        }

        if (_isTopList)
        {
            using Pen divider = new(_dividerColor, 1f);
            int bottom = Math.Min(RowTop(_rows.Count), ClientSize.Height - 1);
            g.DrawLine(divider, Scale(12), bottom, ClientSize.Width - Scale(12), bottom);
        }

        if (_drop.Action == FavoritesViewDropAction.Insert)
        {
            int x = TextLeft(_drop.LineLevel) - Scale(22);
            int y = _drop.LineY - _scrollY;
            using Pen line = new(_accentColor, 2f);
            g.DrawLine(line, x, y, ClientSize.Width - Scale(8), y);
            using SolidBrush dot = new(_accentColor);
            g.FillEllipse(dot, x - 3, y - 3, 6, 6);
        }

        Rectangle thumb = ThumbRect();
        if (!thumb.IsEmpty && (_mouseInside || _thumbDragOffset >= 0))
        {
            using SolidBrush brush = new(Color.FromArgb(_thumbHot ? 160 : 110, _dimColor));
            g.FillRectangle(brush, thumb);
        }
    }

    private void PaintRow(Graphics g, int index, Rectangle row, bool showChevrons)
    {
        Row r = _rows[index];
        bool selected = index == _caret;
        bool hot = index == _hot && _thumbDragOffset < 0;
        bool dropInto = (_drop.Action is FavoritesViewDropAction.IntoGroup or FavoritesViewDropAction.IntoFolder) && _drop.RowIndex == index;
        FavoriteTargetInfo? info = r.Path is not null ? FavoritesShellInfo.Get(r.Path) : null;
        bool missing = r.Kind == RowKind.Shortcut && info is { Exists: false };

        IntPtr hdc = g.GetHdc();
        try
        {
            if (r.Kind != RowKind.Hint && (selected || hot || dropInto))
            {
                // TREIS_HOT 2, TREIS_SELECTED 3, TREIS_SELECTEDNOTFOCUS 5, TREIS_HOTSELECTED 6
                int state = dropInto ? 6 : selected ? (hot ? 6 : Focused ? 3 : 5) : 2;
                RECT rc = new(row.Left, row.Top, row.Right, row.Bottom);
                if (_theme != IntPtr.Zero)
                {
                    DrawThemeBackground(_theme, hdc, 1 /* TVP_TREEITEM */, state, ref rc, IntPtr.Zero);
                }
            }

            if (HasArrow(r) && showChevrons && _theme != IntPtr.Zero)
            {
                int size = Scale(16);
                int c = ChevronCenter(r.Level);
                RECT glyph = new(c - size / 2, row.Top + (row.Height - size) / 2, c - size / 2 + size, row.Top + (row.Height - size) / 2 + size);
                bool chevronHot = hot && _hotChevron;
                // TVP_GLYPH 2 / TVP_HOTGLYPH 4: closed 1, opened 2
                DrawThemeBackground(_theme, hdc, chevronHot ? 4 : 2, r.Node!.Expanded ? 2 : 1, ref glyph, IntPtr.Zero);
            }

            if (r.Kind is RowKind.Fixed or RowKind.Shortcut && info is not null)
            {
                int iconSize = Scale(16);
                ImageList_Draw(FavoritesShellInfo.SmallImageList, info.IconIndex, hdc,
                    IconLeft(r.Level), row.Top + (row.Height - iconSize) / 2, 1 /* ILD_TRANSPARENT */);
            }
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }

        if (r.Kind == RowKind.Group)
        {
            Rectangle icon = new(IconLeft(r.Level) - Scale(2), row.Top, Scale(20), row.Height);
            TextRenderer.DrawText(g, "", _glyphFont!, icon, _fgColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (missing)
        {
            using SolidBrush veil = new(Color.FromArgb(140, _bgColor));
            g.FillRectangle(veil, IconLeft(r.Level), row.Top, Scale(16), row.Height);
        }

        bool showNewTab = hot && r.Kind != RowKind.Hint && r.Kind != RowKind.Group && IsFolderRow(r);
        int textRight = showNewTab ? NewTabRect(index).Left - Scale(2) : row.Right - Scale(8);
        Rectangle text = new(TextLeft(r.Level), row.Top, Math.Max(0, textRight - TextLeft(r.Level)), row.Height);
        Color color = r.Kind == RowKind.Hint || missing ? _dimColor : _fgColor;
        TextRenderer.DrawText(g, RowText(r), Font, text, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (showNewTab)
        {
            Rectangle button = NewTabRect(index);
            if (_hotNewTab)
            {
                using SolidBrush back = new(_host.IsDarkMode ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0));
                using System.Drawing.Drawing2D.GraphicsPath path = RoundedRect(button, Scale(4));
                System.Drawing.Drawing2D.SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillPath(back, path);
                g.SmoothingMode = old;
            }

            TextRenderer.DrawText(g, "", _glyphFont!, button, _hotNewTab ? _fgColor : _dimColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        System.Drawing.Drawing2D.GraphicsPath path = new();
        int d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Font CreateGlyphFont()
    {
        Font font = new("Segoe Fluent Icons", 10f, FontStyle.Regular, GraphicsUnit.Point);
        if (font.Name.Equals("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
        {
            return font;
        }

        font.Dispose();
        return new Font("Segoe MDL2 Assets", 10f, FontStyle.Regular, GraphicsUnit.Point);
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _mouseInside = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        _thumbHot = false;
        SetHot(-1, false, false);
        _hoverTimer.Stop();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_mouseInside)
        {
            _mouseInside = true;
            Invalidate();
        }

        if (_thumbDragOffset >= 0)
        {
            int track = ClientSize.Height - 4;
            int thumb = ThumbRect().Height;
            int top = e.Y - _thumbDragOffset - 2;
            _scrollY = track - thumb <= 0 ? 0 : top * MaxScroll / (track - thumb);
            ClampScroll();
            Invalidate();
            return;
        }

        bool thumbHot = ThumbRect() is { IsEmpty: false } t && e.X >= t.Left - Scale(6);
        if (thumbHot != _thumbHot)
        {
            _thumbHot = thumbHot;
            Invalidate();
        }

        // Dragging an entry to reorder it
        if (_mouseDownRow >= 0 && _mouseDownButton == MouseButtons.Left &&
            (Math.Abs(e.X - _mouseDownPoint.X) > SystemInformation.DragSize.Width ||
             Math.Abs(e.Y - _mouseDownPoint.Y) > SystemInformation.DragSize.Height))
        {
            int row = _mouseDownRow;
            _mouseDownRow = -1;
            if (_rows[row].Node is { } node)
            {
                StartDrag(node);
                return;
            }

            if (_rows[row].FixedId is { } fixedId)
            {
                StartFixedDrag(fixedId);
                return;
            }
        }

        int index = RowAt(e.Y);
        bool onNewTab = index >= 0 && NewTabRect(index).Contains(e.Location);
        bool onChevron = index >= 0 && (_rows[index].Kind == RowKind.Group || HasArrow(_rows[index])) && ChevronRect(index).Contains(e.Location);
        SetHot(index, onNewTab, onChevron);
    }

    private void SetHot(int index, bool onNewTab, bool onChevron)
    {
        if (index == _hot && onNewTab == _hotNewTab && onChevron == _hotChevron)
        {
            return;
        }

        bool rowChanged = index != _hot;
        if (_hot >= 0 && _hot < _rows.Count)
        {
            Invalidate(RowRect(_hot));
        }

        _hot = index;
        _hotNewTab = onNewTab;
        _hotChevron = onChevron;
        if (index >= 0)
        {
            Invalidate(RowRect(index));
        }

        if (rowChanged)
        {
            _toolTip.Hide(this);
            _hoverTimer.Stop();
            if (index >= 0 && CanFlyout(_rows[index]) && !_rows[index].Node!.Expanded)
            {
                _hoverTimer.Interval = HoverOpenDelay;
                _hoverTimer.Start();
            }
        }

        if (onNewTab)
        {
            _toolTip.Show("Open in new tab", this, NewTabRect(index).Left, NewTabRect(index).Bottom + 4, 2000);
        }
    }

    private void OnHoverTimer()
    {
        _hoverTimer.Stop();
        if (_hot >= 0 && _hot < _rows.Count && CanFlyout(_rows[_hot]) && !_rows[_hot].Node!.Expanded &&
            ClientRectangle.Contains(PointToClient(Cursor.Position)))
        {
            OpenFlyout(_hot);
        }
    }

    private void OpenFlyout(int index)
    {
        if (index >= 0 && index < _rows.Count && CanFlyout(_rows[index]) && _rows[index].Node is { } group)
        {
            _host.Flyouts.Open(this, group, RowScreenRect(index));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_isTopList)
        {
            Focus();
        }

        Rectangle thumb = ThumbRect();
        if (e.Button == MouseButtons.Left && !thumb.IsEmpty && e.X >= thumb.Left - Scale(6))
        {
            _thumbDragOffset = e.Y >= thumb.Top && e.Y < thumb.Bottom ? e.Y - thumb.Top : thumb.Height / 2;
            Capture = true;
            return;
        }

        _mouseDownPoint = e.Location;
        _mouseDownRow = RowAt(e.Y);
        _mouseDownButton = e.Button;
        if (_mouseDownRow >= 0 && _rows[_mouseDownRow].Kind == RowKind.Hint)
        {
            // The hint can't be dragged or clicked (fixed rows are dragged only among themselves)
            _mouseDownRow = -1;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_thumbDragOffset >= 0)
        {
            _thumbDragOffset = -1;
            Capture = false;
            Invalidate();
            return;
        }

        int index = RowAt(e.Y);
        bool sameRow = index >= 0 && index == _mouseDownRow;
        _mouseDownRow = -1;

        if (e.Button == MouseButtons.Right)
        {
            ShowMenu(index, e.Location);
            return;
        }

        if (!sameRow)
        {
            return;
        }

        Row row = _rows[index];
        if (e.Button == MouseButtons.Middle)
        {
            if (IsFolderRow(row))
            {
                _host.OpenInNewTab(row.Path!);
            }

            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (row.Kind == RowKind.Group || (HasArrow(row) && ChevronRect(index).Contains(e.Location)))
        {
            // A group opens in place on any click; a link with nested links only on its arrow
            _host.Flyouts.CloseFrom(this);
            Favorites.SetExpanded(row.Node!, !row.Node!.Expanded);
            return;
        }

        if (NewTabRect(index).Contains(e.Location) && IsFolderRow(row))
        {
            _host.OpenInNewTab(row.Path!);
            _host.Flyouts.CloseAll();
            return;
        }

        if (_isTopList)
        {
            _caret = index;
            Invalidate();
        }

        Activate(row);
    }

    /// <summary>Opens a row: a folder in the active pane, a file with its app.</summary>
    private void Activate(Row row)
    {
        if (row.Path is null)
        {
            return;
        }

        FavoriteTargetInfo info = FavoritesShellInfo.Get(row.Path);
        if (row.Kind == RowKind.Shortcut && !info.Exists)
        {
            OfferToRemoveMissing(row);
            return;
        }

        _host.Flyouts.CloseAll();
        if (row.Kind == RowKind.Fixed || info.IsFolder)
        {
            _host.OpenFolder(row.Path);
            return;
        }

        OpenFile(row.Path);
    }

    private static void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Explorer2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OfferToRemoveMissing(Row row)
    {
        using (_host.Flyouts.Suspend())
        {
            DialogResult answer = MessageBox.Show(_host.OwnerForm,
                $"\"{row.Path}\" can't be found. It may have been moved, renamed or deleted, or its drive isn't connected.\n\nRemove it from Favorites?",
                "Explorer2", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
            {
                Favorites.Remove(row.Node!);
            }
            else
            {
                FavoritesShellInfo.Refresh(new[] { row.Path! });
            }
        }
    }

    // ---------------------------------------------------------------- keyboard (the list in the navigation pane)

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End
                   or Keys.PageUp or Keys.PageDown or Keys.Enter or Keys.Escape or Keys.Delete or Keys.F2 or Keys.Apps
               || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0)
        {
            return;
        }

        Row? current = _caret >= 0 && _caret < _rows.Count ? _rows[_caret] : null;
        if (current is null && e.KeyCode is Keys.Up or Keys.Down)
        {
            MoveCaret(0);
            e.Handled = true;
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Up:
                if (_caret > 0)
                {
                    MoveCaret(_caret - 1);
                }

                break;
            case Keys.Down:
                if (_caret < _rows.Count - 1)
                {
                    MoveCaret(_caret + 1);
                }
                else if (_isTopList)
                {
                    _keyNavigateTimer.Stop();
                    _host.FocusTreeBelow();
                }

                break;
            case Keys.Home:
                MoveCaret(0);
                break;
            case Keys.End:
                MoveCaret(_rows.Count - 1);
                break;
            case Keys.PageUp:
                MoveCaret(Math.Max(0, _caret - Math.Max(1, ClientSize.Height / _rowHeight - 1)));
                break;
            case Keys.PageDown:
                MoveCaret(Math.Min(_rows.Count - 1, _caret + Math.Max(1, ClientSize.Height / _rowHeight - 1)));
                break;
            case Keys.Right:
                if (current?.Kind == RowKind.Group || (current is not null && HasArrow(current)))
                {
                    if (!current.Node!.Expanded && current.Node.HasEntries)
                    {
                        Favorites.SetExpanded(current.Node, true);
                    }
                    else if (current.Node.Expanded && _caret + 1 < _rows.Count && _rows[_caret + 1].Level > current.Level)
                    {
                        MoveCaret(_caret + 1);
                    }
                }

                break;
            case Keys.Left:
                if (current?.Node is { Children: not null, Expanded: true })
                {
                    Favorites.SetExpanded(current.Node, false);
                }
                else if (current?.OwnerGroup is { } parent && parent != _group)
                {
                    MoveCaret(_rows.FindIndex(r => r.Node == parent));
                }

                break;
            case Keys.Enter:
                if (current is not null)
                {
                    _keyNavigateTimer.Stop();
                    if (e.Control && IsFolderRow(current))
                    {
                        _host.OpenInNewTab(current.Path!);
                    }
                    else if (current.Kind == RowKind.Group)
                    {
                        Favorites.SetExpanded(current.Node!, !current.Node!.Expanded);
                    }
                    else
                    {
                        Activate(current);
                    }
                }

                break;
            case Keys.F2:
                if (current?.Node is not null)
                {
                    Rename(current.Node);
                }

                break;
            case Keys.Delete:
                if (current?.Node is not null)
                {
                    RemoveWithConfirmation(current.Node);
                }

                break;
            case Keys.Apps:
                if (current is not null)
                {
                    Rectangle r = RowRect(_caret);
                    ShowMenu(_caret, new Point(TextLeft(current.Level), r.Bottom));
                }

                break;
            case Keys.Escape:
                _host.Flyouts.CloseAll();
                break;
            default:
                return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.F10 && e.Shift && _caret >= 0)
        {
            Rectangle r = RowRect(_caret);
            ShowMenu(_caret, new Point(TextLeft(_rows[_caret].Level), r.Bottom));
        }
    }

    private void MoveCaret(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        _caret = index;
        EnsureVisible(index);
        Invalidate();

        // As in the folder tree: moving onto a folder opens it (after a moment, so quick moves skip folders)
        _keyNavigateTimer.Stop();
        if (IsFolderRow(_rows[index]))
        {
            _keyNavigateTimer.Start();
        }
    }

    // ---------------------------------------------------------------- menu and commands

    private void ShowMenu(int index, Point location)
    {
        Row? row = index >= 0 && index < _rows.Count ? _rows[index] : null;
        bool dark = _host.IsDarkMode;
        Font menuFont = new("Segoe UI", 9f, FontStyle.Regular);
        ContextMenuStrip menu = Win11MenuRenderer.CreateMenu(dark, menuFont);
        ToolStripMenuItem Item(string text, Action action, string? glyph = null, string? shortcut = null)
        {
            EventHandler onClick = (_, _) => BeginInvoke(action);
            return glyph is null
                ? Win11MenuRenderer.CreateMenuItem(text, dark, onClick, shortcutDisplay: shortcut)
                : Win11MenuRenderer.CreateIconMenuItem(text, dark, Win11MenuRenderer.Glyph(glyph), onClick, shortcut);
        }

        if (row is not null && row.Kind is RowKind.Fixed or RowKind.Shortcut)
        {
            string path = row.Path!;
            bool folder = IsFolderRow(row);
            if (folder)
            {
                menu.Items.Add(Item("Open", () => Activate(row), ""));
                menu.Items.Add(Item("Open in new tab", () => { _host.Flyouts.CloseAll(); _host.OpenInNewTab(path); }, "", "Ctrl+Enter"));
                if (_host.OpenInOtherPane is { } openInOther)
                {
                    menu.Items.Add(Item("Open in other pane", () => { _host.Flyouts.CloseAll(); openInOther(path); }, ""));
                }
            }
            else
            {
                menu.Items.Add(Item("Open", () => Activate(row), ""));
                if (row.Kind == RowKind.Shortcut && FavoritesShellInfo.Get(path).Exists &&
                    System.IO.Path.GetDirectoryName(path) is { Length: > 0 } location2)
                {
                    menu.Items.Add(Item("Open file location", () => { _host.Flyouts.CloseAll(); _host.OpenFolder(location2); }, ""));
                }
            }

            if (Favorites.SamePath(path, Favorites.RecycleBinPath))
            {
                menu.Items.Add(Item("Empty Recycle Bin", EmptyRecycleBin, ""));
            }

            if (row.Kind == RowKind.Shortcut && row.Node!.HasEntries)
            {
                FavoriteNode link = row.Node;
                menu.Items.Add(Item(link.Expanded ? "Collapse" : "Expand", () => Favorites.SetExpanded(link, !link.Expanded)));
            }

            if (row.Kind == RowKind.Shortcut)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Item("Rename", () => Rename(row.Node!), "", "F2"));
                menu.Items.Add(Item("Unpin from Favorites", () => RemoveWithConfirmation(row.Node!), "", "Del"));
            }
        }
        else if (row?.Kind == RowKind.Group)
        {
            FavoriteNode group = row.Node!;
            menu.Items.Add(Item(group.Expanded ? "Collapse" : "Expand", () => Favorites.SetExpanded(group, !group.Expanded)));
            List<string> folders = group.Children!
                .Where(n => n.Path is not null && FavoritesShellInfo.Get(n.Path) is { IsFolder: true, Exists: true })
                .Select(n => n.Path!).ToList();
            if (folders.Count > 0)
            {
                menu.Items.Add(Item($"Open all in new tabs ({folders.Count})", () =>
                {
                    _host.Flyouts.CloseAll();
                    foreach (string folder in folders)
                    {
                        _host.OpenInNewTab(folder);
                    }
                }, ""));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("New group inside", () => AddGroup(group, -1), ""));
            menu.Items.Add(Item("Rename", () => Rename(group), "", "F2"));
            menu.Items.Add(Item("Remove group", () => RemoveWithConfirmation(group), "", "Del"));
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
        }

        // A new group goes next to the clicked entry (or at the end of the list)
        FavoriteNode? owner = row?.Node is not null ? row.OwnerGroup : _group;
        int at = row?.Node is not null ? row.IndexInOwner + 1 : -1;
        menu.Items.Add(Item("New group", () => AddGroup(owner, at), ""));

        IDisposable suspend = _host.Flyouts.Suspend();
        menu.Closed += (_, _) =>
        {
            suspend.Dispose();
            BeginInvoke(() =>
            {
                menu.Dispose();
                menuFont.Dispose();
            });
        };
        menu.Show(this, location);
    }

    private void AddGroup(FavoriteNode? parent, int index)
    {
        string? name = Prompt("New group", "Group name:", Favorites.NextGroupName(parent), "Create");
        if (name is null)
        {
            return;
        }

        if (parent is not null && !parent.Expanded && Flyout is null)
        {
            parent.Expanded = true;
        }

        Favorites.NewGroup(parent, index, name);
    }

    private void Rename(FavoriteNode node)
    {
        string current = node.IsGroup ? node.Name ?? "" : node.Name ?? FavoritesShellInfo.Get(node.Path!).DisplayName;
        string? name = Prompt("Rename", node.IsGroup
            ? "Group name:"
            : "Name shown in Favorites (leave empty to use the folder's own name):", current, "Rename");
        if (name is null || (node.IsGroup && name.Length == 0))
        {
            return;
        }

        Favorites.Rename(node, name);
    }

    /// <summary>Empties the Recycle Bin with Windows' own question and progress.</summary>
    private void EmptyRecycleBin()
    {
        _host.Flyouts.CloseAll();
        IntPtr owner = _host.OwnerForm?.Handle ?? IntPtr.Zero;
        SHEmptyRecycleBin(owner, null, 0);
        FavoritesShellInfo.Refresh(new[] { Favorites.RecycleBinPath });
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private void RemoveWithConfirmation(FavoriteNode node)
    {
        if (node.HasEntries)
        {
            int shortcuts = Favorites.Flatten(node.Children!).Count(n => !n.IsGroup);
            string what = node.IsGroup ? $"the group \"{node.Name}\"" : $"\"{node.Name ?? FavoritesShellInfo.Get(node.Path!).DisplayName}\"";
            string inside = node.IsGroup ? "in it" : "nested in it";
            using (_host.Flyouts.Suspend())
            {
                if (MessageBox.Show(_host.OwnerForm,
                        $"Remove {what} and the {shortcuts} link{(shortcuts == 1 ? "" : "s")} {inside}?\n\n" +
                        "Only the Favorites entries are removed; the folders and files stay where they are.",
                        "Explorer2", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
            }
        }

        Favorites.Remove(node);
    }

    private string? Prompt(string title, string message, string initial, string ok)
    {
        using (_host.Flyouts.Suspend())
        {
            using CommanderPromptDialog dialog = new(title, message, initial, ok, _host.IsDarkMode);
            return dialog.ShowDialog(_host.OwnerForm) == DialogResult.OK ? dialog.InputText : null;
        }
    }

    // ---------------------------------------------------------------- dragging entries (source)

    private void StartDrag(FavoriteNode node)
    {
        DraggedNode = node;
        IDisposable? suspend = Flyout is not null ? _host.Flyouts.Suspend() : null;
        try
        {
            DataObject data = new();
            data.SetData("Explorer2.Favorite", true);
            DoDragDrop(data, DragDropEffects.Move);
        }
        catch
        {
            // Drag cancelled by the system
        }
        finally
        {
            DraggedNode = null;
            suspend?.Dispose();
            _hot = -1;
            if (!IsDisposed)
            {
                Invalidate();
            }
        }
    }

    private void StartFixedDrag(string id)
    {
        DraggedFixedId = id;
        try
        {
            DataObject data = new();
            data.SetData("Explorer2.TopRow", true);
            DoDragDrop(data, DragDropEffects.Move);
        }
        catch
        {
            // Drag cancelled by the system
        }
        finally
        {
            DraggedFixedId = null;
            _hot = -1;
            if (!IsDisposed)
            {
                Invalidate();
            }
        }
    }

    /// <summary>A fixed top row being dragged goes only between the other fixed rows of the top list.</summary>
    private DropPlan PlanFixedDrop(Point client)
    {
        int index = RowAt(client.Y);
        if (!_isTopList || FixedRowCount == 0 || index < 0 || index >= FixedRowCount)
        {
            return default;
        }

        int top = _padTop + index * _rowHeight;
        bool after = (client.Y + _scrollY - top) / (double)_rowHeight > 0.5;
        int at = after ? index + 1 : index;
        return new DropPlan(FavoritesViewDropAction.Insert, null, at, _padTop + at * _rowHeight - (at == FixedRowCount ? 1 : 0), 0, -1);
    }

    // ---------------------------------------------------------------- dropping (called by FavoritesDropTarget)

    /// <summary>Where a drop at <paramref name="client"/> would go. <paramref name="folderDrop"/>: the drag can go into a folder.</summary>
    private DropPlan PlanDrop(Point client, bool external)
    {
        if (DraggedFixedId is not null)
        {
            return PlanFixedDrop(client);
        }

        FavoriteNode? dragged = DraggedNode;
        int index = RowAt(client.Y);
        if (index < 0)
        {
            // Below the rows: at the end of this list
            int favorites = _rows.Count(r => r.Kind != RowKind.Fixed && r.Kind != RowKind.Hint);
            int lineY = _padTop + _rows.Count * _rowHeight - 1;
            return client.Y >= 0 ? Valid(new DropPlan(FavoritesViewDropAction.Insert, _group, OwnList.Count, favorites > 0 ? lineY : _padTop + FixedRowCount * _rowHeight, 0, -1))
                : default;
        }

        Row row = _rows[index];
        int top = _padTop + index * _rowHeight;
        double rel = (client.Y + _scrollY - top) / (double)_rowHeight;

        switch (row.Kind)
        {
            case RowKind.Hint:
                return Valid(new DropPlan(FavoritesViewDropAction.Insert, _group, 0, top, 0, -1));
            case RowKind.Fixed:
                // Files dropped on the Recycle Bin are deleted, as in File Explorer; the other top rows take no drops
                bool lastFixed = index == FixedRowCount - 1;
                if (external && dragged is null && TopSection.AcceptsDrops(row.Path) && (!lastFixed || rel <= 0.75))
                {
                    return new DropPlan(FavoritesViewDropAction.IntoFolder, null, 0, 0, 0, index);
                }

                return lastFixed && rel > 0.5
                    ? Valid(new DropPlan(FavoritesViewDropAction.Insert, _group, 0, top + _rowHeight, 0, -1))
                    : default;
            default:
                // A group, or a link to a folder: the middle of the row nests the dropped items in it
                // (links only; the files themselves are never copied or moved). A link to a file
                // holds nothing, so a drop goes above or below it.
                FavoriteNode target = row.Node!;
                bool container = row.Kind == RowKind.Group || FavoritesShellInfo.Get(row.Path!) is { IsFolder: true };
                double edge = container ? 0.25 : 0.5;
                if (rel < edge)
                {
                    return Valid(new DropPlan(FavoritesViewDropAction.Insert, row.OwnerGroup, row.IndexInOwner, top, row.Level, -1));
                }

                if (rel >= 1 - edge)
                {
                    return target.Expanded && target.HasEntries
                        ? Valid(new DropPlan(FavoritesViewDropAction.Insert, target, 0, top + _rowHeight, row.Level + 1, -1))
                        : Valid(new DropPlan(FavoritesViewDropAction.Insert, row.OwnerGroup, row.IndexInOwner + 1, top + _rowHeight, row.Level, -1));
                }

                return Valid(new DropPlan(FavoritesViewDropAction.IntoGroup, target, target.Children?.Count ?? 0, 0, 0, index));
        }

        DropPlan Valid(DropPlan plan)
        {
            // A group can't go into itself
            if (dragged is not null && plan.Group is not null && Favorites.IsInside(plan.Group, dragged))
            {
                return default;
            }

            return plan;
        }
    }

    /// <summary>Shows where the drop would go; returns the plan and starts the flyout of a group held under the mouse.</summary>
    internal (FavoritesViewDropAction Action, string? FolderPath) DragOverAt(Point screen, bool external)
    {
        Point client = PointToClient(screen);

        // Scroll when held near the top or bottom edge
        if (HasScroll)
        {
            if (client.Y < Scale(14))
            {
                ScrollBy(-_rowHeight / 2);
            }
            else if (client.Y > ClientSize.Height - Scale(14))
            {
                ScrollBy(_rowHeight / 2);
            }
        }

        DropPlan plan = PlanDrop(client, external);
        if (!plan.Equals(_drop))
        {
            _drop = plan;
            Invalidate();
        }

        // Held over a closed group or a link with nested links: show its entries in a flyout, to drop into a place there
        int index = RowAt(client.Y);
        if (DraggedFixedId is null && index >= 0 && CanFlyout(_rows[index]) && !_rows[index].Node!.Expanded)
        {
            if (index != _dragHoverRow)
            {
                _dragHoverRow = index;
                _dragHoverSince = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _dragHoverSince > DragHoverOpenDelay)
            {
                OpenFlyout(index);
            }
        }
        else
        {
            _dragHoverRow = -1;
        }

        string? folder = plan.Action == FavoritesViewDropAction.IntoFolder ? _rows[plan.RowIndex].Path : null;
        return (plan.Action, folder);
    }

    internal void DragLeft()
    {
        _drop = default;
        _dragHoverRow = -1;
        Invalidate();
    }

    /// <summary>Adds the dropped items (external) or moves the dragged entry, as planned by the last DragOverAt.</summary>
    internal void DropHere(IReadOnlyList<string> paths)
    {
        DropPlan plan = _drop;
        _drop = default;
        _dragHoverRow = -1;
        Invalidate();

        FavoriteNode? dragged = DraggedNode;
        string? draggedFixed = DraggedFixedId;
        BeginInvoke(() =>
        {
            if (draggedFixed is not null)
            {
                if (plan.Action == FavoritesViewDropAction.Insert)
                {
                    TopSection.MoveVisible(draggedFixed, plan.Index);
                }

                return;
            }

            switch (plan.Action)
            {
                case FavoritesViewDropAction.Insert when dragged is not null:
                    Favorites.Move(dragged, plan.Group, plan.Index);
                    break;
                case FavoritesViewDropAction.IntoGroup when dragged is not null:
                    Favorites.Move(dragged, plan.Group, plan.Group!.Children?.Count ?? 0);
                    break;
                case FavoritesViewDropAction.Insert:
                    Favorites.AddPaths(paths, plan.Group, plan.Index);
                    break;
                case FavoritesViewDropAction.IntoGroup:
                    Favorites.AddPaths(paths, plan.Group);
                    break;
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Favorites.Changed -= OnFavoritesChanged;
            TopSection.Changed -= OnFavoritesChanged;
            FavoritesShellInfo.Updated -= OnShellInfoUpdated;
            _hoverTimer.Dispose();
            _keyNavigateTimer.Dispose();
            _toolTip.Dispose();
            _glyphFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- native

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenThemeData(IntPtr hwnd, string pszClassList);

    [DllImport("uxtheme.dll")]
    private static extern int CloseThemeData(IntPtr hTheme);

    [DllImport("uxtheme.dll")]
    private static extern int DrawThemeBackground(IntPtr hTheme, IntPtr hdc, int iPartId, int iStateId, ref RECT pRect, IntPtr pClipRect);

    [DllImport("comctl32.dll")]
    private static extern bool ImageList_Draw(IntPtr himl, int i, IntPtr hdcDst, int x, int y, uint fStyle);
}
