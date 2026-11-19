using System.Drawing.Drawing2D;
using ExplorerSplit.Core;
using ExplorerSplit.Interop;
using Microsoft.Win32;

namespace ExplorerSplit.UI;

internal sealed class DualPaneExplorerForm : Form, IMessageFilter
{
    private readonly record struct BreadcrumbSegment(string Label, string TargetPath, Rectangle Bounds);

    private readonly record struct TitleTabHitBox(
        TabbedExplorerPane Pane,
        ExplorerTabInstance Tab,
        int TabIndexInPane,
        Rectangle TabBounds,
        Rectangle CloseBounds);

    private readonly Panel _titleTabBar;
    private readonly LeftSourceTreePane _leftSourceTree;
    private readonly TabbedExplorerPane _leftFilesPane;
    private readonly TabbedExplorerPane _rightFilesPane;
    private readonly SplitContainer _mainSplit;
    private readonly SplitContainer _filesSplit;

    private readonly Panel _breadcrumbView;
    private readonly TextBox _addressTextBox;
    private readonly TextBox _searchTextBox;
    private readonly Panel _statusBarPanel;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private readonly ToolTip _toolTip;
    private readonly Font _uiFont;
    private readonly Font _tabFont;
    private readonly Font _tabActiveFont;
    private readonly Font _addressFont;
    private readonly Font _statusFont;
    private readonly Icon _windowIcon;
    private readonly Bitmap _titleAppIcon;

    private readonly bool _isDarkMode;
    private readonly Color _titleBarBg;
    private readonly Color _titleTabHoverBg;
    private readonly Color _surfaceBg;
    private readonly Color _commandBarBg;
    private readonly Color _controlBg;
    private readonly Color _controlHoverBg;
    private readonly Color _textColor;
    private readonly Color _mutedTextColor;
    private readonly Color _borderColor;
    private readonly Color _accentColor = Color.FromArgb(0, 120, 212);

    // Unified top title-bar tab state
    private readonly List<TitleTabHitBox> _titleTabHits = new();
    private Rectangle _leftPaneAddTabBounds;
    private Rectangle _rightPaneAddTabBounds;
    private Rectangle _minCaptionBounds;
    private Rectangle _maxCaptionBounds;
    private Rectangle _closeCaptionBounds;
    private int _hoveredTitleTabIndex = -1;
    private int _hoveredTitleTabCloseIndex = -1;
    private int _hoveredAddBtnZone; // 0 = none, 1 = left pane '+', 2 = right pane '+'
    private int _hoveredCaptionBtn; // 0 = none, 1 = min, 2 = max, 3 = close

    // Tab drag-to-rearrange state
    private ExplorerTabInstance? _pressedTab;
    private TabbedExplorerPane? _pressedTabPane;
    private Point _tabMouseDownLocation;
    private bool _isDraggingTab;

    private readonly List<BreadcrumbSegment> _breadcrumbs = new();
    private int _hoveredBreadcrumbIndex = -1;
    private bool _isEditingAddress;
    private string _searchPlaceholder = "Search Home";
    private bool _isSearchPlaceholderActive = true;

    private TabbedExplorerPane _selectedPane;
    private bool _isSplitEnabled = true;
    private bool _sortAscending = true;
    private ShellBrowserInterop.PROPERTYKEY _currentSortKey = ShellBrowserInterop.PKEY_ItemNameDisplay;
    private ShellBrowserInterop.PROPERTYKEY? _currentGroupByKey;
    private bool _groupAscending = true;

    private FolderStatusInfo _currentStatus;
    private Rectangle _statusDetailsBtnBounds;
    private Rectangle _statusIconsBtnBounds;
    private int _hoveredStatusViewBtn; // 0 = none, 1 = details, 2 = icons

    public DualPaneExplorerForm(string? initialLeftPath = null, string? initialRightPath = null, Rectangle? initialBounds = null)
    {
        _isDarkMode = IsSystemDarkMode();

        _titleBarBg = _isDarkMode ? Color.FromArgb(26, 26, 30) : Color.FromArgb(224, 224, 231);
        _titleTabHoverBg = _isDarkMode ? Color.FromArgb(34, 34, 40) : Color.FromArgb(234, 234, 240);
        _surfaceBg = _isDarkMode ? Color.FromArgb(40, 40, 46) : Color.FromArgb(243, 243, 246);
        _commandBarBg = _isDarkMode ? Color.FromArgb(32, 32, 36) : Color.FromArgb(248, 248, 251);
        _controlBg = _isDarkMode ? Color.FromArgb(28, 28, 32) : Color.FromArgb(255, 255, 255);
        _controlHoverBg = _isDarkMode ? Color.FromArgb(54, 54, 62) : Color.FromArgb(230, 230, 236);
        _textColor = _isDarkMode ? Color.FromArgb(244, 244, 248) : Color.FromArgb(22, 22, 26);
        _mutedTextColor = _isDarkMode ? Color.FromArgb(164, 164, 174) : Color.FromArgb(100, 100, 110);
        _borderColor = _isDarkMode ? Color.FromArgb(58, 58, 66) : Color.FromArgb(212, 212, 218);

        _windowIcon = IconFactory.CreateTrayIcon();
        _titleAppIcon = _windowIcon.ToBitmap();
        _uiFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        _tabFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        _tabActiveFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        _addressFont = new Font("Segoe UI", 9.25f, FontStyle.Regular);
        _statusFont = new Font("Segoe UI", 8.75f, FontStyle.Regular);
        _toolTip = new ToolTip { InitialDelay = 300, ReshowDelay = 150 };

        Text = "File Explorer";
        Icon = _windowIcon;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        BackColor = _borderColor;
        ForeColor = _textColor;
        Font = _uiFont;
        MinimumSize = new Size(900, 540);

        if (initialBounds is { Width: > 400, Height: > 300 } bounds)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1380, 840);
        }

        // =====================================================================
        // ROW 0 (VERY TOP): Unified Title Bar & Dual-Zone Tab Strip + Min/Max/Close
        // =====================================================================
        _titleTabBar = new DoubleBufferedPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = _titleBarBg
        };
        _titleTabBar.Paint += OnTitleTabBarPaint;
        _titleTabBar.MouseMove += OnTitleTabBarMouseMove;
        _titleTabBar.MouseLeave += OnTitleTabBarMouseLeave;
        _titleTabBar.MouseDown += OnTitleTabBarMouseDown;
        _titleTabBar.MouseUp += OnTitleTabBarMouseUp;
        _titleTabBar.MouseDoubleClick += OnTitleTabBarMouseDoubleClick;
        _titleTabBar.Resize += (_, _) => _titleTabBar.Invalidate();

        // =====================================================================
        // ROW 1: Back, Forward, Up, Refresh + Breadcrumb Address Bar + Search Box
        // =====================================================================
        Panel navAddressSearchRow = new()
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = _surfaceBg,
            Padding = new Padding(8, 7, 10, 7)
        };

        FlowLayoutPanel navButtons = new()
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 6, 0)
        };

        Control backBtn = CreateVectorIconButton("Back (Alt+Left)", 34, 32, DrawBackIcon, () =>
            _selectedPane?.ActiveTab?.NavigateRelative(ShellBrowserInterop.SBSP_NAVIGATEBACK));

        Control forwardBtn = CreateVectorIconButton("Forward (Alt+Right)", 34, 32, DrawForwardIcon, () =>
            _selectedPane?.ActiveTab?.NavigateRelative(ShellBrowserInterop.SBSP_NAVIGATEFORWARD));

        Control upBtn = CreateVectorIconButton("Up one level (Alt+Up)", 34, 32, DrawUpIcon, () =>
            _selectedPane?.ActiveTab?.NavigateRelative(ShellBrowserInterop.SBSP_PARENT));

        Control refreshBtn = CreateVectorIconButton("Refresh (F5)", 34, 32, DrawRefreshIcon, () =>
            _selectedPane?.ActiveTab?.RefreshView());

        navButtons.Controls.AddRange([backBtn, forwardBtn, upBtn, refreshBtn]);

        Panel searchOuter = new()
        {
            Dock = DockStyle.Right,
            Width = 256,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = _surfaceBg
        };

        RoundedBoxPanel searchFrame = new(_controlBg, _borderColor)
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 30, 4)
        };

        Control searchIconBtn = new DoubleBufferedControl
        {
            Size = new Size(26, 26),
            Dock = DockStyle.Right,
            Cursor = Cursors.Hand,
            BackColor = _controlBg
        };
        searchIconBtn.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSearchMagnifierIcon(e.Graphics, searchIconBtn.ClientRectangle, _mutedTextColor);
        };
        searchIconBtn.Click += (_, _) => RunSearchFromBox();

        _searchTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = _controlBg,
            ForeColor = _mutedTextColor,
            Font = _addressFont,
            Text = _searchPlaceholder
        };
        _searchTextBox.GotFocus += (_, _) =>
        {
            if (_isSearchPlaceholderActive)
            {
                _isSearchPlaceholderActive = false;
                _searchTextBox.Text = string.Empty;
                _searchTextBox.ForeColor = _textColor;
            }
        };
        _searchTextBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_searchTextBox.Text))
            {
                _isSearchPlaceholderActive = true;
                _searchTextBox.ForeColor = _mutedTextColor;
                _searchTextBox.Text = _searchPlaceholder;
            }
        };
        _searchTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                RunSearchFromBox();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                _searchTextBox.Text = string.Empty;
                _selectedPane?.ActiveTab?.ExecuteSearch(null);
                _selectedPane?.ActiveTab?.HostPanel.Focus();
            }
        };

        searchFrame.Controls.Add(_searchTextBox);
        searchFrame.Controls.Add(searchIconBtn);
        searchOuter.Controls.Add(searchFrame);

        RoundedBoxPanel addressFrame = new(_controlBg, _borderColor)
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(2, 2, 2, 2)
        };

        _addressTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = _controlBg,
            ForeColor = _textColor,
            Font = _addressFont,
            Visible = false
        };
        _addressTextBox.KeyDown += OnAddressTextBoxKeyDown;
        _addressTextBox.LostFocus += (_, _) => ExitAddressEditMode();

        _breadcrumbView = new DoubleBufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = _controlBg,
            Cursor = Cursors.IBeam
        };
        _breadcrumbView.Paint += OnBreadcrumbPaint;
        _breadcrumbView.MouseMove += OnBreadcrumbMouseMove;
        _breadcrumbView.MouseLeave += (_, _) =>
        {
            if (_hoveredBreadcrumbIndex != -1)
            {
                _hoveredBreadcrumbIndex = -1;
                _breadcrumbView.Invalidate();
            }
        };
        _breadcrumbView.MouseDown += OnBreadcrumbMouseDown;
        _breadcrumbView.Resize += (_, _) =>
        {
            RebuildBreadcrumbs(_selectedPane?.ActiveTab);
            _breadcrumbView.Invalidate();
        };

        Panel textBoxHost = new()
        {
            Dock = DockStyle.Fill,
            BackColor = _controlBg,
            Padding = new Padding(8, 4, 8, 2),
            Visible = false
        };
        textBoxHost.Controls.Add(_addressTextBox);
        _addressTextBox.Tag = textBoxHost;

        addressFrame.Controls.Add(_breadcrumbView);
        addressFrame.Controls.Add(textBoxHost);

        navAddressSearchRow.Controls.Add(addressFrame);
        navAddressSearchRow.Controls.Add(searchOuter);
        navAddressSearchRow.Controls.Add(navButtons);

        Panel navDivider = new()
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = _borderColor
        };

        // =====================================================================
        // ROW 2: Windows 11 Command Bar (New v | Cut Copy Paste Rename Share Delete | Sort v View v ... | Split)
        // =====================================================================
        Panel commandBarRow = new()
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = _commandBarBg,
            Padding = new Padding(10, 6, 12, 6)
        };

        FlowLayoutPanel leftCommandFlow = new()
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        Control newDropdownBtn = CreateCommandBarDropdownButton("New", 78, DrawNewPlusCircleIcon, ShowNewMenu);
        Control cutCmdBtn = CreateVectorIconButton("Cut (Ctrl+X)", 36, 32, DrawCutScissorsIcon, () =>
            _selectedPane?.ActiveTab?.InvokeShellVerb("cut"));
        Control copyCmdBtn = CreateVectorIconButton("Copy (Ctrl+C)", 36, 32, DrawCopyPagesIcon, () =>
            _selectedPane?.ActiveTab?.InvokeShellVerb("copy"));
        Control pasteCmdBtn = CreateVectorIconButton("Paste (Ctrl+V)", 36, 32, DrawPasteClipboardIcon, () =>
            _selectedPane?.ActiveTab?.InvokeShellVerb("paste", onBackground: true));
        Control renameCmdBtn = CreateVectorIconButton("Rename (F2)", 36, 32, DrawRenameBoxIcon, () =>
            _selectedPane?.ActiveTab?.InvokeShellVerb("rename"));
        Control shareCmdBtn = CreateVectorIconButton("Copy Path / Share", 36, 32, DrawShareArrowIcon, CopySelectedOrCurrentPathToClipboard);
        Control deleteCmdBtn = CreateVectorIconButton("Delete (Del)", 36, 32, DrawTrashCanIcon, () =>
            _selectedPane?.ActiveTab?.InvokeShellVerb("delete"));

        Control sortDropdownBtn = CreateCommandBarDropdownButton("Sort", 76, DrawSortArrowsIcon, ShowSortMenu);
        Control viewDropdownBtn = CreateCommandBarDropdownButton("View", 78, DrawViewLinesIcon, ShowViewMenu);
        Control? moreBtn = null;
        moreBtn = CreateVectorIconButton("See more", 36, 32, DrawMoreDotsIcon, () => ShowMoreMenu(moreBtn!));

        leftCommandFlow.Controls.AddRange([
            newDropdownBtn,
            CreateVerticalCommandSeparator(),
            cutCmdBtn,
            copyCmdBtn,
            pasteCmdBtn,
            renameCmdBtn,
            shareCmdBtn,
            deleteCmdBtn,
            CreateVerticalCommandSeparator(),
            sortDropdownBtn,
            viewDropdownBtn,
            CreateVerticalCommandSeparator(),
            moreBtn
        ]);

        Control splitPreviewBtn = CreateCommandBarToggleButton("Split", 82, DrawSplitPaneIcon, () => _isSplitEnabled, ToggleDualPaneSplit);
        splitPreviewBtn.Dock = DockStyle.Right;

        commandBarRow.Controls.Add(splitPreviewBtn);
        commandBarRow.Controls.Add(leftCommandFlow);

        Panel commandBottomBorder = new()
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = _borderColor
        };

        // =====================================================================
        // BOTTOM: Native Windows 11 Status Bar ("12 items | 1 item selected 40.1 MB" + View Toggle Icons)
        // =====================================================================
        _statusBarPanel = new DoubleBufferedPanel
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            BackColor = _surfaceBg
        };
        _statusBarPanel.Paint += OnStatusBarPaint;
        _statusBarPanel.MouseMove += OnStatusBarMouseMove;
        _statusBarPanel.MouseLeave += (_, _) =>
        {
            if (_hoveredStatusViewBtn != 0)
            {
                _hoveredStatusViewBtn = 0;
                _statusBarPanel.Invalidate();
            }
        };
        _statusBarPanel.MouseDown += OnStatusBarMouseDown;

        // =====================================================================
        // CENTER: Left Source Tree Pane | Nested Dual File Panes (tabs are on the top title bar!)
        // =====================================================================
        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
            FixedPanel = FixedPanel.Panel1,
            BackColor = _borderColor
        };
        _mainSplit.SplitterMoved += (_, _) =>
        {
            _titleTabBar.Invalidate();
            SyncAllPaneViewRects();
        };

        _leftSourceTree = new LeftSourceTreePane(_isDarkMode);
        _leftSourceTree.SourceSelected += path =>
        {
            _selectedPane?.ActiveTab?.NavigateToPath(path);
        };
        _mainSplit.Panel1.Controls.Add(_leftSourceTree);

        _filesSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5,
            BackColor = _borderColor
        };
        _filesSplit.SplitterMoved += (_, _) =>
        {
            _titleTabBar.Invalidate();
            SyncAllPaneViewRects();
        };

        _leftFilesPane = new TabbedExplorerPane("LEFT PANE", _isDarkMode);
        _rightFilesPane = new TabbedExplorerPane("RIGHT PANE", _isDarkMode);

        _leftFilesPane.PaneSelected += SetSelectedPane;
        _rightFilesPane.PaneSelected += SetSelectedPane;

        _leftFilesPane.ActiveTabStateChanged += OnPaneActiveTabStateChanged;
        _rightFilesPane.ActiveTabStateChanged += OnPaneActiveTabStateChanged;

        _leftFilesPane.TabsChanged += _ => _titleTabBar.Invalidate();
        _rightFilesPane.TabsChanged += _ => _titleTabBar.Invalidate();

        _filesSplit.Panel1.Controls.Add(_leftFilesPane);
        _filesSplit.Panel2.Controls.Add(_rightFilesPane);
        _mainSplit.Panel2.Controls.Add(_filesSplit);

        Controls.Add(_mainSplit);
        Controls.Add(_statusBarPanel);
        Controls.Add(commandBottomBorder);
        Controls.Add(commandBarRow);
        Controls.Add(navDivider);
        Controls.Add(navAddressSearchRow);
        Controls.Add(_titleTabBar);

        _selectedPane = _leftFilesPane;
        _leftFilesPane.SetIsActivePane(true);
        _rightFilesPane.SetIsActivePane(false);

        string startLeft = !string.IsNullOrWhiteSpace(initialLeftPath)
            ? initialLeftPath
            : ExplorerDetector.HomeShellGuid;
        string startRight = !string.IsNullOrWhiteSpace(initialRightPath)
            ? initialRightPath
            : startLeft;

        _leftFilesPane.AddNewTab(startLeft, activate: true);
        _rightFilesPane.AddNewTab(startRight, activate: true);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _statusTimer.Tick += (_, _) => RefreshStatusInfo();
        _statusTimer.Start();

        Application.AddMessageFilter(this);
    }

    public bool IsSplitEnabled => _isSplitEnabled;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            int cornerPref = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPref,
                sizeof(int));
        }
        catch
        {
            // Ignore on older OS builds
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        try
        {
            if (_mainSplit.Width > 600)
            {
                _mainSplit.Panel1MinSize = 160;
                _mainSplit.Panel2MinSize = 360;
                _mainSplit.SplitterDistance = Math.Clamp(225, _mainSplit.Panel1MinSize, _mainSplit.Width - _mainSplit.Panel2MinSize);
            }

            if (_filesSplit.Width > 500)
            {
                _filesSplit.Panel1MinSize = 200;
                _filesSplit.Panel2MinSize = 200;
                _filesSplit.SplitterDistance = _filesSplit.Width / 2;
            }
        }
        catch
        {
            // Ignore layout sizing exceptions on narrow startup states
        }

        SetSelectedPane(_leftFilesPane);
        _leftFilesPane.ActiveTab?.EnsureInitialized();
        _rightFilesPane.ActiveTab?.EnsureInitialized();
        SyncAllPaneViewRects();
        _titleTabBar.Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        SyncAllPaneViewRects();
        _titleTabBar?.Invalidate();
    }

    private void SyncAllPaneViewRects()
    {
        if (IsDisposed)
        {
            return;
        }

        _leftFilesPane?.ActiveTab?.SyncRectForced();
        if (_isSplitEnabled)
        {
            _rightFilesPane?.ActiveTab?.SyncRectForced();
        }

        if (IsHandleCreated)
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed)
                {
                    _leftFilesPane?.ActiveTab?.SyncRectForced();
                    if (_isSplitEnabled)
                    {
                        _rightFilesPane?.ActiveTab?.SyncRectForced();
                    }
                }
            });
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_NCHITTEST &&
            WindowState == FormWindowState.Normal &&
            m.Result == (IntPtr)NativeMethods.HTCLIENT)
        {
            Point pt = PointToClient(new Point(m.LParam.ToInt32()));
            const int grip = 6;

            bool left = pt.X < grip;
            bool right = pt.X >= Width - grip;
            bool top = pt.Y < grip;
            bool bottom = pt.Y >= Height - grip;

            if (top && left) m.Result = (IntPtr)NativeMethods.HTTOPLEFT;
            else if (top && right) m.Result = (IntPtr)NativeMethods.HTTOPRIGHT;
            else if (bottom && left) m.Result = (IntPtr)NativeMethods.HTBOTTOMLEFT;
            else if (bottom && right) m.Result = (IntPtr)NativeMethods.HTBOTTOMRIGHT;
            else if (left) m.Result = (IntPtr)NativeMethods.HTLEFT;
            else if (right) m.Result = (IntPtr)NativeMethods.HTRIGHT;
            else if (top) m.Result = (IntPtr)NativeMethods.HTTOP;
            else if (bottom) m.Result = (IntPtr)NativeMethods.HTBOTTOM;
        }
    }

    private void ToggleMaximizeRestore()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }

        _titleTabBar.Invalidate();
    }

    // =====================================================================
    // Unified Top Title & Dual-Zone Tab Bar (Tabs + Minimize/Maximize/Close)
    // =====================================================================
    private int GetLeftFilesPaneHorizontalStartX()
    {
        int x = _mainSplit.Left + _mainSplit.SplitterDistance + _mainSplit.SplitterWidth;
        int maxStart = Math.Max(160, _titleTabBar.Width - 138 - 260);
        return Math.Clamp(x, 120, maxStart);
    }

    private int GetRightFilesPaneHorizontalStartX()
    {
        int x = GetLeftFilesPaneHorizontalStartX() +
                _filesSplit.SplitterDistance + _filesSplit.SplitterWidth;
        int maxStart = Math.Max(320, _titleTabBar.Width - 138 - 160);
        return Math.Clamp(x, GetLeftFilesPaneHorizontalStartX() + 140, maxStart);
    }

    private void OnTitleTabBarPaint(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int barW = _titleTabBar.Width;
        int barH = _titleTabBar.Height;

        // Bottom border separating title bar from the Navigation/Address row
        using (Pen bottomPen = new(_borderColor, 1))
        {
            g.DrawLine(bottomPen, 0, barH - 1, barW, barH - 1);
        }

        // 1. Caption buttons on the far right (Minimize, Maximize/Restore, Close)
        const int capBtnW = 46;
        int capBtnH = barH - 1;
        _closeCaptionBounds = new Rectangle(barW - capBtnW, 0, capBtnW, capBtnH);
        _maxCaptionBounds = new Rectangle(barW - capBtnW * 2, 0, capBtnW, capBtnH);
        _minCaptionBounds = new Rectangle(barW - capBtnW * 3, 0, capBtnW, capBtnH);

        DrawCaptionButtons(g);

        _titleTabHits.Clear();
        _leftPaneAddTabBounds = Rectangle.Empty;
        _rightPaneAddTabBounds = Rectangle.Empty;

        int captionLeft = _minCaptionBounds.Left;
        int leftZoneStartX = GetLeftFilesPaneHorizontalStartX();

        // Draw App Icon and Name ("ExplorerSplit") on the left side of the title bar above the Left Source Pane
        int iconY = (barH - 18) / 2;
        g.DrawImage(_titleAppIcon, new Rectangle(12, iconY, 18, 18));

        int brandMaxRight = Math.Max(40, leftZoneStartX - 10);
        if (brandMaxRight > 42)
        {
            Rectangle brandTextRect = new(36, 0, brandMaxRight - 36, barH);
            TextRenderer.DrawText(
                g,
                "ExplorerSplit",
                _tabActiveFont,
                brandTextRect,
                _textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if (_isSplitEnabled)
        {
            int rightZoneStartX = GetRightFilesPaneHorizontalStartX();
            int leftZoneMaxRight = Math.Max(leftZoneStartX + 110, rightZoneStartX - 10);
            int rightZoneMaxRight = Math.Max(rightZoneStartX + 110, captionLeft - 10);

            // Zone 1 (Vertically aligned above Left Files Pane): Tabs for Left Files Pane
            DrawPaneTabZone(
                g,
                _leftFilesPane,
                startX: leftZoneStartX,
                maxRightX: leftZoneMaxRight,
                isLeftZone: true,
                out _leftPaneAddTabBounds);

            // Zone 2 (Vertically aligned above Right Files Pane): Tabs for Right Files Pane
            DrawPaneTabZone(
                g,
                _rightFilesPane,
                startX: rightZoneStartX,
                maxRightX: rightZoneMaxRight,
                isLeftZone: false,
                out _rightPaneAddTabBounds);
        }
        else
        {
            // Single-pane mode: Left Files Pane tabs aligned above Left Files Pane
            DrawPaneTabZone(
                g,
                _leftFilesPane,
                startX: leftZoneStartX,
                maxRightX: Math.Max(leftZoneStartX + 140, captionLeft - 12),
                isLeftZone: true,
                out _leftPaneAddTabBounds);
        }
    }

    private void DrawPaneTabZone(
        Graphics g,
        TabbedExplorerPane pane,
        int startX,
        int maxRightX,
        bool isLeftZone,
        out Rectangle addBtnBounds)
    {
        IReadOnlyList<ExplorerTabInstance> tabs = pane.Tabs;
        int count = tabs.Count;
        int topY = 7;
        int tabHeight = _titleTabBar.Height - topY;
        const int addBtnW = 30;
        const int addBtnH = 24;

        int availableWidth = Math.Max(80, maxRightX - startX - addBtnW - 8);
        int tabWidth = count > 0 ? Math.Clamp(availableWidth / count, 96, 220) : 180;

        int currentX = startX;
        for (int i = 0; i < count; i++)
        {
            ExplorerTabInstance tab = tabs[i];
            bool isPaneActiveTab = tab == pane.ActiveTab;
            bool isWindowSelectedTab = isPaneActiveTab && pane == _selectedPane;

            int hitIndex = _titleTabHits.Count;
            bool isHovered = hitIndex == _hoveredTitleTabIndex;

            Rectangle tabRect = new(currentX, topY, tabWidth - 2, tabHeight);
            Rectangle closeRect = new(tabRect.Right - 24, tabRect.Top + (tabRect.Height - 18) / 2, 18, 18);
            _titleTabHits.Add(new TitleTabHitBox(pane, tab, i, tabRect, closeRect));

            bool isBeingDragged = _isDraggingTab && tab == _pressedTab;
            if (isPaneActiveTab || isBeingDragged)
            {
                Color tabFill = isWindowSelectedTab
                    ? _surfaceBg
                    : (_isDarkMode ? Color.FromArgb(34, 34, 39) : Color.FromArgb(236, 236, 241));

                using (GraphicsPath tabPath = CreateTopRoundedTabPath(tabRect, 8))
                using (SolidBrush tabBrush = new(tabFill))
                using (Pen tabBorderPen = new(isBeingDragged ? _accentColor : _borderColor, isBeingDragged ? 1.5f : 1f))
                {
                    g.FillPath(tabBrush, tabPath);
                    g.DrawPath(tabBorderPen, tabPath);
                }

                // Seamlessly connect the active tab to the Navigation/Address row below
                if (isWindowSelectedTab)
                {
                    using SolidBrush seamlessBottom = new(_surfaceBg);
                    g.FillRectangle(seamlessBottom, tabRect.Left + 1, tabRect.Bottom - 2, tabRect.Width - 2, 3);
                }
            }
            else
            {
                if (isHovered)
                {
                    Rectangle hoverRect = new(tabRect.Left + 2, tabRect.Top + 2, tabRect.Width - 4, tabRect.Height - 4);
                    using GraphicsPath hoverPath = IconFactory.CreateRoundedRect(hoverRect, 6);
                    using SolidBrush hoverBrush = new(_titleTabHoverBg);
                    g.FillPath(hoverBrush, hoverPath);
                }

                bool nextIsActive = i + 1 < count && tabs[i + 1] == pane.ActiveTab;
                if (!nextIsActive)
                {
                    using Pen divPen = new(_borderColor, 1f);
                    g.DrawLine(divPen, tabRect.Right, tabRect.Top + 7, tabRect.Right, tabRect.Bottom - 7);
                }
            }

            // 16x16 native folder/device icon on left of tab
            int textLeft = tabRect.Left + 10;
            if (tab.TabIcon is not null)
            {
                int iconY = tabRect.Top + (tabRect.Height - 16) / 2;
                g.DrawImage(tab.TabIcon, new Rectangle(textLeft, iconY, 16, 16));
                textLeft += 22;
            }

            Rectangle textRect = new(textLeft, tabRect.Top + 1, Math.Max(10, closeRect.Left - textLeft - 4), tabRect.Height - 2);
            TextRenderer.DrawText(
                g,
                tab.TabDisplayHeader,
                isWindowSelectedTab ? _tabActiveFont : _tabFont,
                textRect,
                isPaneActiveTab ? _textColor : _mutedTextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (_hoveredTitleTabCloseIndex == hitIndex)
            {
                using GraphicsPath closeBgPath = IconFactory.CreateRoundedRect(closeRect, 4);
                using SolidBrush closeHoverBrush = new(_isDarkMode ? Color.FromArgb(65, 65, 72) : Color.FromArgb(208, 208, 216));
                g.FillPath(closeHoverBrush, closeBgPath);
            }

            Color closeColor = isPaneActiveTab ? _textColor : _mutedTextColor;
            using (Pen xPen = new(closeColor, 1.3f))
            {
                int cx = closeRect.Left + closeRect.Width / 2;
                int cy = closeRect.Top + closeRect.Height / 2;
                g.DrawLine(xPen, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f);
                g.DrawLine(xPen, cx + 3.5f, cy - 3.5f, cx - 3.5f, cy + 3.5f);
            }

            currentX += tabWidth;
        }

        // '+' Add New Tab button for this pane's tab zone
        addBtnBounds = new Rectangle(currentX + 4, topY + (tabHeight - addBtnH) / 2, addBtnW, addBtnH);
        bool addHovered = (isLeftZone && _hoveredAddBtnZone == 1) || (!isLeftZone && _hoveredAddBtnZone == 2);
        if (addHovered)
        {
            using GraphicsPath addPath = IconFactory.CreateRoundedRect(addBtnBounds, 5);
            using SolidBrush addHoverBrush = new(_titleTabHoverBg);
            g.FillPath(addHoverBrush, addPath);
        }

        using (Pen plusPen = new(_textColor, 1.4f))
        {
            int px = addBtnBounds.Left + addBtnBounds.Width / 2;
            int py = addBtnBounds.Top + addBtnBounds.Height / 2;
            g.DrawLine(plusPen, px - 4.5f, py, px + 4.5f, py);
            g.DrawLine(plusPen, px, py - 4.5f, px, py + 4.5f);
        }
    }

    private void DrawCaptionButtons(Graphics g)
    {
        // Minimize button (─)
        if (_hoveredCaptionBtn == 1)
        {
            using SolidBrush b = new(_titleTabHoverBg);
            g.FillRectangle(b, _minCaptionBounds);
        }
        using (Pen p = new(_textColor, 1.2f))
        {
            int cx = _minCaptionBounds.Left + _minCaptionBounds.Width / 2;
            int cy = _minCaptionBounds.Top + _minCaptionBounds.Height / 2;
            g.DrawLine(p, cx - 5, cy, cx + 5, cy);
        }

        // Maximize / Restore button (□)
        if (_hoveredCaptionBtn == 2)
        {
            using SolidBrush b = new(_titleTabHoverBg);
            g.FillRectangle(b, _maxCaptionBounds);
        }
        using (Pen p = new(_textColor, 1.2f))
        {
            int cx = _maxCaptionBounds.Left + _maxCaptionBounds.Width / 2;
            int cy = _maxCaptionBounds.Top + _maxCaptionBounds.Height / 2;
            if (WindowState == FormWindowState.Maximized)
            {
                g.DrawRectangle(p, cx - 5, cy - 3, 8, 8);
                g.DrawLine(p, cx - 3, cy - 5, cx + 5, cy - 5);
                g.DrawLine(p, cx + 5, cy - 5, cx + 5, cy + 3);
            }
            else
            {
                g.DrawRectangle(p, cx - 5, cy - 5, 10, 10);
            }
        }

        // Close button (✕)
        Color closeIconColor = _textColor;
        if (_hoveredCaptionBtn == 3)
        {
            using SolidBrush b = new(Color.FromArgb(196, 43, 28));
            g.FillRectangle(b, _closeCaptionBounds);
            closeIconColor = Color.White;
        }
        using (Pen p = new(closeIconColor, 1.3f))
        {
            int cx = _closeCaptionBounds.Left + _closeCaptionBounds.Width / 2;
            int cy = _closeCaptionBounds.Top + _closeCaptionBounds.Height / 2;
            g.DrawLine(p, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
            g.DrawLine(p, cx + 4.5f, cy - 4.5f, cx - 4.5f, cy + 4.5f);
        }
    }

    private void OnTitleTabBarMouseMove(object? sender, MouseEventArgs e)
    {
        if (_pressedTab is not null && _pressedTabPane is not null && (e.Button & MouseButtons.Left) != 0)
        {
            if (!_isDraggingTab && Math.Abs(e.X - _tabMouseDownLocation.X) >= 6)
            {
                _isDraggingTab = true;
            }

            if (_isDraggingTab)
            {
                HandleTabDragMove(e.X);
                return;
            }
        }

        int newCaption = 0;
        if (_minCaptionBounds.Contains(e.Location)) newCaption = 1;
        else if (_maxCaptionBounds.Contains(e.Location)) newCaption = 2;
        else if (_closeCaptionBounds.Contains(e.Location)) newCaption = 3;

        int newAddZone = 0;
        if (_leftPaneAddTabBounds.Contains(e.Location)) newAddZone = 1;
        else if (_isSplitEnabled && _rightPaneAddTabBounds.Contains(e.Location)) newAddZone = 2;

        int newTabIdx = -1;
        int newCloseIdx = -1;

        for (int i = 0; i < _titleTabHits.Count; i++)
        {
            if (_titleTabHits[i].TabBounds.Contains(e.Location))
            {
                newTabIdx = i;
                if (_titleTabHits[i].CloseBounds.Contains(e.Location))
                {
                    newCloseIdx = i;
                }
                break;
            }
        }

        if (newCaption != _hoveredCaptionBtn ||
            newAddZone != _hoveredAddBtnZone ||
            newTabIdx != _hoveredTitleTabIndex ||
            newCloseIdx != _hoveredTitleTabCloseIndex)
        {
            _hoveredCaptionBtn = newCaption;
            _hoveredAddBtnZone = newAddZone;
            _hoveredTitleTabIndex = newTabIdx;
            _hoveredTitleTabCloseIndex = newCloseIdx;
            _titleTabBar.Invalidate();
        }
    }

    private void OnTitleTabBarMouseLeave(object? sender, EventArgs e)
    {
        if (_hoveredCaptionBtn != 0 ||
            _hoveredAddBtnZone != 0 ||
            _hoveredTitleTabIndex != -1 ||
            _hoveredTitleTabCloseIndex != -1)
        {
            _hoveredCaptionBtn = 0;
            _hoveredAddBtnZone = 0;
            _hoveredTitleTabIndex = -1;
            _hoveredTitleTabCloseIndex = -1;
            _titleTabBar.Invalidate();
        }
    }

    private void OnTitleTabBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_minCaptionBounds.Contains(e.Location))
            {
                WindowState = FormWindowState.Minimized;
                return;
            }

            if (_maxCaptionBounds.Contains(e.Location))
            {
                ToggleMaximizeRestore();
                return;
            }

            if (_closeCaptionBounds.Contains(e.Location))
            {
                Close();
                return;
            }

            if (_leftPaneAddTabBounds.Contains(e.Location))
            {
                SetSelectedPane(_leftFilesPane);
                _leftFilesPane.AddNewTab(activate: true);
                return;
            }

            if (_isSplitEnabled && _rightPaneAddTabBounds.Contains(e.Location))
            {
                SetSelectedPane(_rightFilesPane);
                _rightFilesPane.AddNewTab(activate: true);
                return;
            }

            for (int i = 0; i < _titleTabHits.Count; i++)
            {
                TitleTabHitBox hit = _titleTabHits[i];
                if (hit.CloseBounds.Contains(e.Location))
                {
                    hit.Pane.CloseTab(hit.Tab);
                    return;
                }

                if (hit.TabBounds.Contains(e.Location))
                {
                    SetSelectedPane(hit.Pane);
                    hit.Pane.SelectTab(hit.Tab, notifyPaneSelected: true);
                    _pressedTab = hit.Tab;
                    _pressedTabPane = hit.Pane;
                    _tabMouseDownLocation = e.Location;
                    _isDraggingTab = false;
                    _titleTabBar.Capture = true;
                    return;
                }
            }

            // Dragging unoccupied title-bar space moves the window natively
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(
                Handle,
                (uint)NativeMethods.WM_NCLBUTTONDOWN,
                (IntPtr)NativeMethods.HTCAPTION,
                IntPtr.Zero);
        }
        else if (e.Button == MouseButtons.Middle)
        {
            for (int i = 0; i < _titleTabHits.Count; i++)
            {
                if (_titleTabHits[i].TabBounds.Contains(e.Location))
                {
                    _titleTabHits[i].Pane.CloseTab(_titleTabHits[i].Tab);
                    return;
                }
            }
        }
    }

    private void OnTitleTabBarMouseUp(object? sender, MouseEventArgs e)
    {
        if (_pressedTab is not null || _isDraggingTab)
        {
            _pressedTab = null;
            _pressedTabPane = null;
            _isDraggingTab = false;
            _titleTabBar.Capture = false;
            _titleTabBar.Invalidate();
        }
    }

    private void HandleTabDragMove(int mouseX)
    {
        if (_pressedTab is null || _pressedTabPane is null)
        {
            return;
        }

        // Support dragging a tab across to the other pane when Split View is active and source pane has >1 tab
        if (_isSplitEnabled && _pressedTabPane.Tabs.Count > 1)
        {
            int rightZoneStart = GetRightFilesPaneHorizontalStartX();
            if (_pressedTabPane == _leftFilesPane && mouseX >= rightZoneStart + 10)
            {
                if (_leftFilesPane.TryTransferTabTo(_pressedTab, _rightFilesPane, 0))
                {
                    _pressedTabPane = _rightFilesPane;
                    SetSelectedPane(_rightFilesPane);
                    _titleTabBar.Invalidate();
                    return;
                }
            }
            else if (_pressedTabPane == _rightFilesPane && mouseX < rightZoneStart - 10)
            {
                if (_rightFilesPane.TryTransferTabTo(_pressedTab, _leftFilesPane, _leftFilesPane.Tabs.Count))
                {
                    _pressedTabPane = _leftFilesPane;
                    SetSelectedPane(_leftFilesPane);
                    _titleTabBar.Invalidate();
                    return;
                }
            }
        }

        int currentIdx = _pressedTabPane.IndexOfTab(_pressedTab);
        if (currentIdx < 0)
        {
            return;
        }

        foreach (TitleTabHitBox hit in _titleTabHits)
        {
            if (hit.Pane != _pressedTabPane || hit.Tab == _pressedTab)
            {
                continue;
            }

            int midX = hit.TabBounds.Left + hit.TabBounds.Width / 2;
            if ((currentIdx < hit.TabIndexInPane && mouseX > midX) ||
                (currentIdx > hit.TabIndexInPane && mouseX < midX))
            {
                _pressedTabPane.MoveTab(currentIdx, hit.TabIndexInPane);
                _titleTabBar.Invalidate();
                break;
            }
        }
    }

    private void OnTitleTabBarMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // Only toggle maximize/restore when double-clicking empty title-bar area
        if (_minCaptionBounds.Contains(e.Location) ||
            _maxCaptionBounds.Contains(e.Location) ||
            _closeCaptionBounds.Contains(e.Location) ||
            _leftPaneAddTabBounds.Contains(e.Location) ||
            (_isSplitEnabled && _rightPaneAddTabBounds.Contains(e.Location)))
        {
            return;
        }

        for (int i = 0; i < _titleTabHits.Count; i++)
        {
            if (_titleTabHits[i].TabBounds.Contains(e.Location))
            {
                return;
            }
        }

        ToggleMaximizeRestore();
    }

    private static GraphicsPath CreateTopRoundedTabPath(Rectangle rect, int radius)
    {
        GraphicsPath path = new();
        int d = Math.Max(2, radius * 2);
        path.AddLine(rect.Left, rect.Bottom, rect.Left, rect.Top + radius);
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddLine(rect.Right, rect.Top + radius, rect.Right, rect.Bottom);
        return path;
    }

    // =====================================================================
    // Dual-Pane & Active Pane Management
    // =====================================================================
    public void OpenOrFocusFolder(string? leftPath, string? rightPath = null, bool ensureSplitOn = true)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        if (ensureSplitOn && !_isSplitEnabled)
        {
            ToggleDualPaneSplit();
        }

        if (!string.IsNullOrWhiteSpace(leftPath))
        {
            if (_leftFilesPane.ActiveTab is not null)
            {
                _leftFilesPane.ActiveTab.NavigateToPath(leftPath);
            }
            else
            {
                _leftFilesPane.AddNewTab(leftPath, activate: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(rightPath))
        {
            if (_rightFilesPane.ActiveTab is not null)
            {
                _rightFilesPane.ActiveTab.NavigateToPath(rightPath);
            }
            else
            {
                _rightFilesPane.AddNewTab(rightPath, activate: true);
            }
        }

        Show();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);

        if (_isSplitEnabled && _filesSplit.Width > 500)
        {
            try
            {
                _filesSplit.SplitterDistance = _filesSplit.Width / 2;
            }
            catch
            {
                // Ignore
            }
        }

        SyncAllPaneViewRects();
    }

    public void ToggleDualPaneSplit()
    {
        _isSplitEnabled = !_isSplitEnabled;
        _filesSplit.Panel2Collapsed = !_isSplitEnabled;

        if (_isSplitEnabled)
        {
            if (_filesSplit.Width > 500)
            {
                try
                {
                    _filesSplit.SplitterDistance = _filesSplit.Width / 2;
                }
                catch
                {
                    // Ignore
                }
            }

            _rightFilesPane.ActiveTab?.EnsureInitialized();
        }
        else if (_selectedPane == _rightFilesPane)
        {
            SetSelectedPane(_leftFilesPane);
        }

        SyncAllPaneViewRects();
        _titleTabBar.Invalidate();
        Invalidate(true);
    }

    public void SetSelectedPane(TabbedExplorerPane pane)
    {
        if (!_isSplitEnabled && pane == _rightFilesPane)
        {
            pane = _leftFilesPane;
        }

        _selectedPane = pane;
        _leftFilesPane.SetIsActivePane(pane == _leftFilesPane);
        _rightFilesPane.SetIsActivePane(pane == _rightFilesPane);

        _titleTabBar.Invalidate();
        SyncTopAndBottomBarsWithActiveTab(pane.ActiveTab);
    }

    private void OnPaneActiveTabStateChanged(TabbedExplorerPane pane, ExplorerTabInstance tab)
    {
        _titleTabBar.Invalidate();
        if (pane == _selectedPane)
        {
            SyncTopAndBottomBarsWithActiveTab(tab);
        }
    }

    private void SyncTopAndBottomBarsWithActiveTab(ExplorerTabInstance? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (!_isEditingAddress)
        {
            _addressTextBox.Text = tab.DisplayPath;
            RebuildBreadcrumbs(tab);
            _breadcrumbView.Invalidate();
        }

        _searchPlaceholder = $"Search {tab.Title}";
        if (_isSearchPlaceholderActive && !_searchTextBox.Focused)
        {
            _searchTextBox.Text = _searchPlaceholder;
        }

        Text = $"{tab.Title} — File Explorer";
        _leftSourceTree.SyncHighlightToPath(tab.DisplayPath);
        _titleTabBar.Invalidate();
        RefreshStatusInfo(forceRepaint: true);
    }

    private void RefreshStatusInfo(bool forceRepaint = false)
    {
        if (IsDisposed || !Visible)
        {
            return;
        }

        ExplorerTabInstance? activeTab = _selectedPane?.ActiveTab;
        if (activeTab is null)
        {
            return;
        }

        FolderStatusInfo newStatus = activeTab.GetStatusInfo();
        if (forceRepaint || newStatus != _currentStatus)
        {
            _currentStatus = newStatus;
            _statusBarPanel.Invalidate();
        }
    }

    // =====================================================================
    // Breadcrumb Address Bar Rendering & Interaction (Matching Image 1)
    // =====================================================================
    private void RebuildBreadcrumbs(ExplorerTabInstance? tab)
    {
        _breadcrumbs.Clear();
        _hoveredBreadcrumbIndex = -1;

        if (tab is null)
        {
            return;
        }

        List<(string Label, string Target)> rawParts = new();
        string path = tab.DisplayPath;

        if (string.IsNullOrWhiteSpace(path) ||
            path.Equals(ExplorerDetector.HomeShellGuid, StringComparison.OrdinalIgnoreCase) ||
            path.Equals("Home", StringComparison.OrdinalIgnoreCase))
        {
            rawParts.Add(("Home", ExplorerDetector.HomeShellGuid));
        }
        else if (path.Equals("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase) ||
                 path.Equals("This PC", StringComparison.OrdinalIgnoreCase))
        {
            rawParts.Add(("This PC", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
        }
        else if (path.Contains(Path.DirectorySeparatorChar) || (path.Length >= 2 && path[1] == ':'))
        {
            rawParts.Add(("This PC", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));

            string[] parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string running = string.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                running = i == 0 ? parts[0] + Path.DirectorySeparatorChar : Path.Combine(running, parts[i]);
                rawParts.Add((parts[i], running));
            }
        }
        else
        {
            rawParts.Add((tab.Title, tab.ParsingPath));
        }

        int maxSegments = 6;
        if (rawParts.Count > maxSegments)
        {
            rawParts = rawParts.Skip(rawParts.Count - maxSegments).ToList();
        }

        int currentX = 34;
        int height = Math.Max(22, _breadcrumbView.Height - 4);
        int topY = (_breadcrumbView.Height - height) / 2;

        foreach ((string label, string target) in rawParts)
        {
            Size textSize = TextRenderer.MeasureText(label, _addressFont);
            int segWidth = Math.Min(180, textSize.Width + 14);
            Rectangle segRect = new(currentX, topY, segWidth, height);
            _breadcrumbs.Add(new BreadcrumbSegment(label, target, segRect));
            currentX += segWidth + 16;
        }
    }

    private void OnBreadcrumbPaint(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        ExplorerTabInstance? tab = _selectedPane?.ActiveTab;
        if (tab?.TabIcon is not null)
        {
            int iconY = (_breadcrumbView.Height - 16) / 2;
            g.DrawImage(tab.TabIcon, new Rectangle(10, iconY, 16, 16));
        }

        DrawRightChevron(g, new Rectangle(28, (_breadcrumbView.Height - 12) / 2, 8, 12), _mutedTextColor);

        for (int i = 0; i < _breadcrumbs.Count; i++)
        {
            BreadcrumbSegment seg = _breadcrumbs[i];

            if (i == _hoveredBreadcrumbIndex)
            {
                using GraphicsPath hoverPath = IconFactory.CreateRoundedRect(seg.Bounds, 4);
                using SolidBrush hoverBrush = new(_controlHoverBg);
                g.FillPath(hoverBrush, hoverPath);
            }

            TextRenderer.DrawText(
                g,
                seg.Label,
                _addressFont,
                seg.Bounds,
                _textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            Rectangle chevRect = new(seg.Bounds.Right + 4, (_breadcrumbView.Height - 12) / 2, 8, 12);
            DrawRightChevron(g, chevRect, _mutedTextColor);
        }
    }

    private void OnBreadcrumbMouseMove(object? sender, MouseEventArgs e)
    {
        int hovered = -1;
        for (int i = 0; i < _breadcrumbs.Count; i++)
        {
            if (_breadcrumbs[i].Bounds.Contains(e.Location))
            {
                hovered = i;
                break;
            }
        }

        _breadcrumbView.Cursor = hovered >= 0 ? Cursors.Hand : Cursors.IBeam;

        if (hovered != _hoveredBreadcrumbIndex)
        {
            _hoveredBreadcrumbIndex = hovered;
            _breadcrumbView.Invalidate();
        }
    }

    private void OnBreadcrumbMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        for (int i = 0; i < _breadcrumbs.Count; i++)
        {
            if (_breadcrumbs[i].Bounds.Contains(e.Location))
            {
                _selectedPane?.ActiveTab?.NavigateToPath(_breadcrumbs[i].TargetPath);
                return;
            }
        }

        EnterAddressEditMode();
    }

    private void EnterAddressEditMode()
    {
        if (_isEditingAddress)
        {
            return;
        }

        _isEditingAddress = true;
        _addressTextBox.Text = _selectedPane?.ActiveTab?.DisplayPath ?? string.Empty;
        _breadcrumbView.Visible = false;
        if (_addressTextBox.Tag is Control host)
        {
            host.Visible = true;
        }
        _addressTextBox.Visible = true;
        _addressTextBox.Focus();
        _addressTextBox.SelectAll();
    }

    private void ExitAddressEditMode()
    {
        if (!_isEditingAddress)
        {
            return;
        }

        _isEditingAddress = false;
        _addressTextBox.Visible = false;
        if (_addressTextBox.Tag is Control host)
        {
            host.Visible = false;
        }
        _breadcrumbView.Visible = true;
        RebuildBreadcrumbs(_selectedPane?.ActiveTab);
        _breadcrumbView.Invalidate();
    }

    private void OnAddressTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            string input = _addressTextBox.Text;
            ExitAddressEditMode();

            ExplorerTabInstance? activeTab = _selectedPane?.ActiveTab;
            if (activeTab is not null)
            {
                activeTab.NavigateToPath(input);
                activeTab.HostPanel.Focus();
            }
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            ExitAddressEditMode();
            _selectedPane?.ActiveTab?.HostPanel.Focus();
        }
    }

    private void RunSearchFromBox()
    {
        if (_isSearchPlaceholderActive)
        {
            return;
        }

        string query = _searchTextBox.Text.Trim();
        _selectedPane?.ActiveTab?.ExecuteSearch(query);
    }

    // =====================================================================
    // Bottom Status Bar Rendering & Interaction (Matching Image 2)
    // =====================================================================
    private void OnStatusBarPaint(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (Pen topBorder = new(_borderColor, 1))
        {
            g.DrawLine(topBorder, 0, 0, _statusBarPanel.Width, 0);
        }

        int currentX = 14;
        int centerY = _statusBarPanel.Height / 2;

        string totalText = _currentStatus.TotalItems == 1 ? "1 item" : $"{_currentStatus.TotalItems:N0} items";
        Size totalSize = TextRenderer.MeasureText(g, totalText, _statusFont, Size.Empty, TextFormatFlags.NoPadding);
        Rectangle totalRect = new(currentX, 0, totalSize.Width + 4, _statusBarPanel.Height);
        TextRenderer.DrawText(
            g,
            totalText,
            _statusFont,
            totalRect,
            _textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        currentX = totalRect.Right + 10;

        using (Pen sepPen = new(_borderColor, 1))
        {
            g.DrawLine(sepPen, currentX, centerY - 7, currentX, centerY + 7);
        }

        currentX += 12;

        if (_currentStatus.SelectedItems > 0)
        {
            string selText = _currentStatus.SelectedItems == 1
                ? "1 item selected"
                : $"{_currentStatus.SelectedItems:N0} items selected";

            if (_currentStatus.HasSelectedFileSize)
            {
                selText += $"  {FormatFileSize(_currentStatus.SelectedSizeBytes)}";
            }

            Size selSize = TextRenderer.MeasureText(g, selText, _statusFont, Size.Empty, TextFormatFlags.NoPadding);
            Rectangle selRect = new(currentX, 0, selSize.Width + 8, _statusBarPanel.Height);
            TextRenderer.DrawText(
                g,
                selText,
                _statusFont,
                selRect,
                _textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            currentX = selRect.Right + 10;
            using Pen sepPen2 = new(_borderColor, 1);
            g.DrawLine(sepPen2, currentX, centerY - 7, currentX, centerY + 7);
        }

        int btnW = 24;
        int btnH = 22;
        int btnY = (_statusBarPanel.Height - btnH) / 2;

        _statusIconsBtnBounds = new Rectangle(_statusBarPanel.Width - 10 - btnW, btnY, btnW, btnH);
        _statusDetailsBtnBounds = new Rectangle(_statusIconsBtnBounds.Left - 4 - btnW, btnY, btnW, btnH);

        uint currentViewMode = _selectedPane?.ActiveTab?.CurrentViewMode ?? ShellBrowserInterop.FVM_DETAILS;
        bool isDetailsActive = currentViewMode == ShellBrowserInterop.FVM_DETAILS;
        bool isIconsActive = !isDetailsActive;

        DrawStatusViewToggleButton(g, _statusDetailsBtnBounds, isDetailsActive, _hoveredStatusViewBtn == 1, isDetailsIcon: true);
        DrawStatusViewToggleButton(g, _statusIconsBtnBounds, isIconsActive, _hoveredStatusViewBtn == 2, isDetailsIcon: false);
    }

    private void DrawStatusViewToggleButton(Graphics g, Rectangle bounds, bool isActive, bool isHovered, bool isDetailsIcon)
    {
        if (isActive)
        {
            Color activeFill = _isDarkMode ? Color.FromArgb(26, 62, 98) : Color.FromArgb(217, 236, 255);
            Color activeBorder = _isDarkMode ? Color.FromArgb(72, 148, 224) : Color.FromArgb(120, 178, 232);
            using SolidBrush fillBrush = new(activeFill);
            using Pen borderPen = new(activeBorder, 1f);
            g.FillRectangle(fillBrush, bounds);
            g.DrawRectangle(borderPen, bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);
        }
        else if (isHovered)
        {
            using SolidBrush hoverBrush = new(_controlHoverBg);
            g.FillRectangle(hoverBrush, bounds);
        }

        using Pen iconPen = new(_textColor, 1.2f);
        if (isDetailsIcon)
        {
            int left = bounds.Left + 5;
            int right = bounds.Right - 5;
            int midY = bounds.Top + bounds.Height / 2;
            g.DrawLine(iconPen, left, midY - 5, right, midY - 5);
            g.DrawLine(iconPen, left, midY - 2, right, midY - 2);
            g.DrawLine(iconPen, left, midY + 1, right, midY + 1);
            g.DrawLine(iconPen, left, midY + 4, right, midY + 4);
        }
        else
        {
            Rectangle box = new(bounds.Left + 5, bounds.Top + 4, bounds.Width - 10, bounds.Height - 9);
            g.DrawRectangle(iconPen, box);
            g.DrawLine(iconPen, box.Left + 1, box.Bottom + 2, box.Right - 1, box.Bottom + 2);
        }
    }

    private void OnStatusBarMouseMove(object? sender, MouseEventArgs e)
    {
        int hovered = 0;
        if (_statusDetailsBtnBounds.Contains(e.Location))
        {
            hovered = 1;
        }
        else if (_statusIconsBtnBounds.Contains(e.Location))
        {
            hovered = 2;
        }

        if (hovered != _hoveredStatusViewBtn)
        {
            _hoveredStatusViewBtn = hovered;
            _statusBarPanel.Invalidate();
        }
    }

    private void OnStatusBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_statusDetailsBtnBounds.Contains(e.Location))
        {
            _selectedPane?.ActiveTab?.SetViewMode(ShellBrowserInterop.FVM_DETAILS);
            _statusBarPanel.Invalidate();
        }
        else if (_statusIconsBtnBounds.Contains(e.Location))
        {
            _selectedPane?.ActiveTab?.SetViewMode(ShellBrowserInterop.FVM_ICON);
            _statusBarPanel.Invalidate();
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} bytes";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:0.#} KB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:0.#} MB";
        }

        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }

    // =====================================================================
    // Command Bar Menus (New, Sort + Group By, View, More, Share/Copy Path)
    // =====================================================================
    private void ShowNewMenu(Control anchor)
    {
        ContextMenuStrip menu = Win11MenuRenderer.CreateMenu(_isDarkMode, _uiFont);
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("Folder", _isDarkMode, (_, _) => _selectedPane?.ActiveTab?.CreateNewFolder(), shortcutDisplay: "Ctrl+Shift+N"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("Text Document", _isDarkMode, (_, _) => _selectedPane?.ActiveTab?.CreateNewTextDocument()));
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("New Tab in Active Pane", _isDarkMode, (_, _) => _selectedPane?.AddNewTab(activate: true), shortcutDisplay: "Ctrl+T"));
        menu.Show(anchor, new Point(0, anchor.Height + 2));
    }

    private void ShowSortMenu(Control anchor)
    {
        ContextMenuStrip menu = Win11MenuRenderer.CreateMenu(_isDarkMode, _uiFont);

        void AddSortField(string label, ShellBrowserInterop.PROPERTYKEY key)
        {
            ToolStripMenuItem item = Win11MenuRenderer.CreateMenuItem(
                label,
                _isDarkMode,
                (_, _) =>
                {
                    _currentSortKey = key;
                    _selectedPane?.ActiveTab?.ApplySort(_currentSortKey, _sortAscending);
                },
                isChecked: _currentSortKey.pid == key.pid);
            menu.Items.Add(item);
        }

        AddSortField("Name", ShellBrowserInterop.PKEY_ItemNameDisplay);
        AddSortField("Date modified", ShellBrowserInterop.PKEY_DateModified);
        AddSortField("Type", ShellBrowserInterop.PKEY_ItemTypeText);
        AddSortField("Size", ShellBrowserInterop.PKEY_Size);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Win11MenuRenderer.CreateMenuItem(
            "Ascending",
            _isDarkMode,
            (_, _) =>
            {
                _sortAscending = true;
                _selectedPane?.ActiveTab?.ApplySort(_currentSortKey, true);
            },
            isChecked: _sortAscending));

        menu.Items.Add(Win11MenuRenderer.CreateMenuItem(
            "Descending",
            _isDarkMode,
            (_, _) =>
            {
                _sortAscending = false;
                _selectedPane?.ActiveTab?.ApplySort(_currentSortKey, false);
            },
            isChecked: !_sortAscending));

        menu.Items.Add(new ToolStripSeparator());

        // "Group by" submenu (matching native Windows 11 File Explorer)
        ToolStripMenuItem groupByMenu = Win11MenuRenderer.CreateMenuItem("Group by", _isDarkMode);

        groupByMenu.DropDownItems.Add(Win11MenuRenderer.CreateMenuItem(
            "(None)",
            _isDarkMode,
            (_, _) =>
            {
                _currentGroupByKey = null;
                _selectedPane?.ActiveTab?.ApplyGroupBy(null, _groupAscending);
            },
            isChecked: _currentGroupByKey is null));

        void AddGroupField(string label, ShellBrowserInterop.PROPERTYKEY key)
        {
            ToolStripMenuItem item = Win11MenuRenderer.CreateMenuItem(
                label,
                _isDarkMode,
                (_, _) =>
                {
                    _currentGroupByKey = key;
                    _selectedPane?.ActiveTab?.ApplyGroupBy(_currentGroupByKey, _groupAscending);
                },
                isChecked: _currentGroupByKey is { } gk && gk.pid == key.pid);
            groupByMenu.DropDownItems.Add(item);
        }

        AddGroupField("Name", ShellBrowserInterop.PKEY_ItemNameDisplay);
        AddGroupField("Date modified", ShellBrowserInterop.PKEY_DateModified);
        AddGroupField("Type", ShellBrowserInterop.PKEY_ItemTypeText);
        AddGroupField("Size", ShellBrowserInterop.PKEY_Size);

        groupByMenu.DropDownItems.Add(new ToolStripSeparator());

        groupByMenu.DropDownItems.Add(Win11MenuRenderer.CreateMenuItem(
            "Ascending",
            _isDarkMode,
            (_, _) =>
            {
                _groupAscending = true;
                if (_currentGroupByKey is not null)
                {
                    _selectedPane?.ActiveTab?.ApplyGroupBy(_currentGroupByKey, true);
                }
            },
            isChecked: _groupAscending));

        groupByMenu.DropDownItems.Add(Win11MenuRenderer.CreateMenuItem(
            "Descending",
            _isDarkMode,
            (_, _) =>
            {
                _groupAscending = false;
                if (_currentGroupByKey is not null)
                {
                    _selectedPane?.ActiveTab?.ApplyGroupBy(_currentGroupByKey, false);
                }
            },
            isChecked: !_groupAscending));

        menu.Items.Add(groupByMenu);
        menu.Show(anchor, new Point(0, anchor.Height + 2));
    }

    private void ShowViewMenu(Control anchor)
    {
        ContextMenuStrip menu = Win11MenuRenderer.CreateMenu(_isDarkMode, _uiFont);
        uint currentMode = _selectedPane?.ActiveTab?.CurrentViewMode ?? ShellBrowserInterop.FVM_DETAILS;

        void AddViewOption(string label, uint mode)
        {
            ToolStripMenuItem item = Win11MenuRenderer.CreateMenuItem(
                label,
                _isDarkMode,
                (_, _) =>
                {
                    _selectedPane?.ActiveTab?.SetViewMode(mode);
                    _statusBarPanel.Invalidate();
                },
                isChecked: currentMode == mode);
            menu.Items.Add(item);
        }

        AddViewOption("Extra large icons", ShellBrowserInterop.FVM_THUMBNAIL);
        AddViewOption("Large icons", ShellBrowserInterop.FVM_ICON);
        AddViewOption("Small icons", ShellBrowserInterop.FVM_SMALLICON);
        AddViewOption("List", ShellBrowserInterop.FVM_LIST);
        AddViewOption("Details", ShellBrowserInterop.FVM_DETAILS);
        AddViewOption("Tiles", ShellBrowserInterop.FVM_TILE);
        AddViewOption("Content", ShellBrowserInterop.FVM_THUMBSTRIP);

        menu.Show(anchor, new Point(0, anchor.Height + 2));
    }

    private void ShowMoreMenu(Control anchor)
    {
        ContextMenuStrip menu = Win11MenuRenderer.CreateMenu(_isDarkMode, _uiFont);
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("New Tab", _isDarkMode, (_, _) => _selectedPane?.AddNewTab(activate: true), shortcutDisplay: "Ctrl+T"));
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("Close Tab", _isDarkMode, (_, _) => _selectedPane?.CloseActiveTab(), shortcutDisplay: "Ctrl+W"));
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("Sync Other Pane to Active Folder", _isDarkMode, (_, _) => SyncInactivePaneToActive()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("Copy Path", _isDarkMode, (_, _) => CopySelectedOrCurrentPathToClipboard()));
        menu.Items.Add(Win11MenuRenderer.CreateMenuItem("Properties", _isDarkMode, (_, _) => _selectedPane?.ActiveTab?.InvokeShellVerb("properties"), shortcutDisplay: "Alt+Enter"));
        menu.Show(anchor, new Point(0, anchor.Height + 2));
    }

    private void CopySelectedOrCurrentPathToClipboard()
    {
        string? path = _selectedPane?.ActiveTab?.GetFirstSelectedOrCurrentPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                Clipboard.SetText(path);
            }
            catch
            {
                // Ignore clipboard contention
            }
        }
    }

    private void SyncInactivePaneToActive()
    {
        string? activePath = _selectedPane?.ActiveTab?.ParsingPath;
        if (string.IsNullOrWhiteSpace(activePath))
        {
            return;
        }

        if (!_isSplitEnabled)
        {
            ToggleDualPaneSplit();
        }

        TabbedExplorerPane otherPane = _selectedPane == _leftFilesPane ? _rightFilesPane : _leftFilesPane;
        otherPane.ActiveTab?.NavigateToPath(activePath);
    }

    // =====================================================================
    // Windows 11 Vector Icon Controls & Drawing Helpers
    // =====================================================================
    private Control CreateVectorIconButton(
        string tooltip,
        int width,
        int height,
        Action<Graphics, Rectangle, Color> drawIcon,
        Action onClick)
    {
        DoubleBufferedControl btn = new()
        {
            Size = new Size(width, height),
            Margin = new Padding(1, 0, 1, 0),
            Cursor = Cursors.Hand
        };

        bool hovered = false;
        btn.MouseEnter += (_, _) => { hovered = true; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { hovered = false; btn.Invalidate(); };
        btn.Click += (_, _) => onClick();
        btn.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (hovered)
            {
                using GraphicsPath path = IconFactory.CreateRoundedRect(new Rectangle(1, 1, btn.Width - 2, btn.Height - 2), 4);
                using SolidBrush hoverBrush = new(_controlHoverBg);
                e.Graphics.FillPath(hoverBrush, path);
            }

            drawIcon(e.Graphics, btn.ClientRectangle, _textColor);
        };

        _toolTip.SetToolTip(btn, tooltip);
        return btn;
    }

    private Control CreateCommandBarDropdownButton(
        string label,
        int width,
        Action<Graphics, Rectangle, Color> drawIcon,
        Action<Control> onOpenMenu)
    {
        DoubleBufferedControl btn = new()
        {
            Size = new Size(width, 32),
            Margin = new Padding(2, 0, 2, 0),
            Cursor = Cursors.Hand
        };

        bool hovered = false;
        btn.MouseEnter += (_, _) => { hovered = true; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { hovered = false; btn.Invalidate(); };
        btn.Click += (_, _) => onOpenMenu(btn);
        btn.Paint += (_, e) =>
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (hovered)
            {
                using GraphicsPath path = IconFactory.CreateRoundedRect(new Rectangle(1, 1, btn.Width - 2, btn.Height - 2), 4);
                using SolidBrush hoverBrush = new(_controlHoverBg);
                g.FillPath(hoverBrush, path);
            }

            Rectangle iconBox = new(6, 4, 22, 24);
            drawIcon(g, iconBox, _textColor);

            Rectangle textBox = new(28, 0, btn.Width - 44, btn.Height);
            TextRenderer.DrawText(
                g,
                label,
                _uiFont,
                textBox,
                _textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            DrawDownChevron(g, new Rectangle(btn.Width - 16, (btn.Height - 8) / 2 + 1, 8, 8), _mutedTextColor);
        };

        return btn;
    }

    private Control CreateCommandBarToggleButton(
        string label,
        int width,
        Action<Graphics, Rectangle, Color> drawIcon,
        Func<bool> isToggledOn,
        Action onClick)
    {
        DoubleBufferedControl btn = new()
        {
            Size = new Size(width, 32),
            Margin = new Padding(2, 0, 2, 0),
            Cursor = Cursors.Hand
        };

        bool hovered = false;
        btn.MouseEnter += (_, _) => { hovered = true; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { hovered = false; btn.Invalidate(); };
        btn.Click += (_, _) => { onClick(); btn.Invalidate(); };
        btn.Paint += (_, e) =>
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool active = isToggledOn();
            if (active || hovered)
            {
                Color fill = active
                    ? (_isDarkMode ? Color.FromArgb(26, 62, 98) : Color.FromArgb(224, 238, 252))
                    : _controlHoverBg;

                using GraphicsPath path = IconFactory.CreateRoundedRect(new Rectangle(1, 1, btn.Width - 2, btn.Height - 2), 4);
                using SolidBrush brush = new(fill);
                g.FillPath(brush, path);
            }

            Rectangle iconBox = new(8, 4, 22, 24);
            drawIcon(g, iconBox, _textColor);

            Rectangle textBox = new(32, 0, btn.Width - 36, btn.Height);
            TextRenderer.DrawText(
                g,
                label,
                _uiFont,
                textBox,
                _textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };

        _toolTip.SetToolTip(btn, "Toggle side-by-side Split View (Ctrl+Shift+S)");
        return btn;
    }

    private Control CreateVerticalCommandSeparator()
    {
        Panel sep = new()
        {
            Size = new Size(1, 22),
            Margin = new Padding(7, 5, 7, 5),
            BackColor = _borderColor
        };
        return sep;
    }

    private static void DrawBackIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawLine(p, cx + 6, cy, cx - 5, cy);
        g.DrawLine(p, cx - 5, cy, cx - 1, cy - 4);
        g.DrawLine(p, cx - 5, cy, cx - 1, cy + 4);
    }

    private static void DrawForwardIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawLine(p, cx - 6, cy, cx + 5, cy);
        g.DrawLine(p, cx + 5, cy, cx + 1, cy - 4);
        g.DrawLine(p, cx + 5, cy, cx + 1, cy + 4);
    }

    private static void DrawUpIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawLine(p, cx, cy + 6, cx, cy - 5);
        g.DrawLine(p, cx, cy - 5, cx - 4, cy - 1);
        g.DrawLine(p, cx, cy - 5, cx + 4, cy - 1);
    }

    private static void DrawRefreshIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        Rectangle arcRect = new(cx - 5, cy - 5, 10, 10);
        g.DrawArc(p, arcRect, 35, 290);
        g.DrawLine(p, cx + 4, cy - 5, cx + 5, cy - 1);
        g.DrawLine(p, cx + 1, cy - 2, cx + 5, cy - 1);
    }

    private static void DrawSearchMagnifierIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.Left + r.Width / 2 - 1;
        int cy = r.Top + r.Height / 2 - 1;
        g.DrawEllipse(p, cx - 4, cy - 4, 8, 8);
        g.DrawLine(p, cx + 2, cy + 2, cx + 6, cy + 6);
    }

    private static void DrawRightChevron(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawLine(p, cx - 2, cy - 3, cx + 1, cy);
        g.DrawLine(p, cx + 1, cy, cx - 2, cy + 3);
    }

    private static void DrawDownChevron(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawLine(p, cx - 3, cy - 1, cx, cy + 2);
        g.DrawLine(p, cx, cy + 2, cx + 3, cy - 1);
    }

    private void DrawNewPlusCircleIcon(Graphics g, Rectangle r, Color c)
    {
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        using Pen circlePen = new(_accentColor, 1.4f);
        g.DrawEllipse(circlePen, cx - 7, cy - 7, 14, 14);
        using Pen plusPen = new(_accentColor, 1.4f);
        g.DrawLine(plusPen, cx - 3.5f, cy, cx + 3.5f, cy);
        g.DrawLine(plusPen, cx, cy - 3.5f, cx, cy + 3.5f);
    }

    private static void DrawCutScissorsIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.3f);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawEllipse(p, cx - 5, cy + 1, 4, 4);
        g.DrawEllipse(p, cx + 1, cy + 1, 4, 4);
        g.DrawLine(p, cx - 2, cy + 1, cx + 3, cy - 6);
        g.DrawLine(p, cx + 2, cy + 1, cx - 3, cy - 6);
    }

    private static void DrawCopyPagesIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.3f);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawRectangle(p, cx - 2, cy - 4, 8, 10);
        g.DrawLine(p, cx - 5, cy - 6, cx + 3, cy - 6);
        g.DrawLine(p, cx - 5, cy - 6, cx - 5, cy + 3);
    }

    private static void DrawPasteClipboardIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.3f);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawRectangle(p, cx - 5, cy - 5, 10, 12);
        g.DrawRectangle(p, cx - 2, cy - 7, 4, 2);
    }

    private static void DrawRenameBoxIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.3f);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawRectangle(p, cx - 7, cy - 4, 14, 9);
        using Pen accent = new(Color.FromArgb(0, 120, 212), 1.4f);
        g.DrawLine(accent, cx + 2, cy - 6, cx + 2, cy + 7);
    }

    private static void DrawShareArrowIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.3f);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawRectangle(p, cx - 6, cy - 3, 11, 9);
        g.DrawLine(p, cx - 1, cy + 1, cx + 6, cy - 5);
        g.DrawLine(p, cx + 6, cy - 5, cx + 2, cy - 5);
        g.DrawLine(p, cx + 6, cy - 5, cx + 6, cy - 1);
    }

    private static void DrawTrashCanIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.3f);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawRectangle(p, cx - 4, cy - 3, 8, 10);
        g.DrawLine(p, cx - 6, cy - 3, cx + 6, cy - 3);
        g.DrawLine(p, cx - 2, cy - 5, cx + 2, cy - 5);
        g.DrawLine(p, cx - 1, cy - 1, cx - 1, cy + 4);
        g.DrawLine(p, cx + 1, cy - 1, cx + 1, cy + 4);
    }

    private void DrawSortArrowsIcon(Graphics g, Rectangle r, Color c)
    {
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        using Pen upPen = new(_accentColor, 1.4f);
        g.DrawLine(upPen, cx - 3, cy + 5, cx - 3, cy - 5);
        g.DrawLine(upPen, cx - 3, cy - 5, cx - 6, cy - 2);
        g.DrawLine(upPen, cx - 3, cy - 5, cx, cy - 2);

        using Pen downPen = new(c, 1.4f);
        g.DrawLine(downPen, cx + 3, cy - 5, cx + 3, cy + 5);
        g.DrawLine(downPen, cx + 3, cy + 5, cx, cy + 2);
        g.DrawLine(downPen, cx + 3, cy + 5, cx + 6, cy + 2);
    }

    private static void DrawViewLinesIcon(Graphics g, Rectangle r, Color c)
    {
        using Pen p = new(c, 1.4f);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.DrawLine(p, cx - 6, cy - 4, cx + 6, cy - 4);
        g.DrawLine(p, cx - 6, cy, cx + 6, cy);
        g.DrawLine(p, cx - 6, cy + 4, cx + 6, cy + 4);
    }

    private static void DrawMoreDotsIcon(Graphics g, Rectangle r, Color c)
    {
        using SolidBrush b = new(c);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        g.FillEllipse(b, cx - 7, cy - 1, 3, 3);
        g.FillEllipse(b, cx - 1, cy - 1, 3, 3);
        g.FillEllipse(b, cx + 5, cy - 1, 3, 3);
    }

    private void DrawSplitPaneIcon(Graphics g, Rectangle r, Color c)
    {
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        Rectangle box = new(cx - 7, cy - 5, 14, 11);
        using Pen p = new(c, 1.3f);
        g.DrawRectangle(p, box);
        using Pen accent = new(_accentColor, 1.6f);
        g.DrawLine(accent, cx, box.Top, cx, box.Bottom);
    }

    // =====================================================================
    // Message Filter for Active Pane Selection & Keyboard Shortcuts
    // =====================================================================
    public bool PreFilterMessage(ref Message m)
    {
        if (IsDisposed || !Visible)
        {
            return false;
        }

        if (m.Msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
        {
            if (_isEditingAddress &&
                m.HWnd != _addressTextBox.Handle &&
                !NativeMethods.IsChild(_breadcrumbView.Parent?.Handle ?? IntPtr.Zero, m.HWnd))
            {
                ExitAddressEditMode();
            }

            if (_leftFilesPane.IsHandleCreated &&
                (m.HWnd == _leftFilesPane.Handle || NativeMethods.IsChild(_leftFilesPane.Handle, m.HWnd)))
            {
                if (_selectedPane != _leftFilesPane)
                {
                    SetSelectedPane(_leftFilesPane);
                }
            }
            else if (_isSplitEnabled &&
                     _rightFilesPane.IsHandleCreated &&
                     (m.HWnd == _rightFilesPane.Handle || NativeMethods.IsChild(_rightFilesPane.Handle, m.HWnd)))
            {
                if (_selectedPane != _rightFilesPane)
                {
                    SetSelectedPane(_rightFilesPane);
                }
            }
        }

        if (m.Msg == NativeMethods.WM_KEYDOWN && Form.ActiveForm == this)
        {
            Keys keyCode = (Keys)m.WParam.ToInt32() & Keys.KeyCode;
            Keys modifiers = Control.ModifierKeys;

            if (modifiers == Keys.Control && keyCode == Keys.T)
            {
                _selectedPane.AddNewTab(activate: true);
                return true;
            }

            if (modifiers == Keys.Control && keyCode == Keys.W)
            {
                _selectedPane.CloseActiveTab();
                return true;
            }

            if (modifiers == Keys.Control && keyCode == Keys.Tab)
            {
                _selectedPane.CycleTab(forward: true);
                return true;
            }

            if (modifiers == (Keys.Control | Keys.Shift) && keyCode == Keys.Tab)
            {
                _selectedPane.CycleTab(forward: false);
                return true;
            }

            if (modifiers == (Keys.Control | Keys.Shift) && keyCode == Keys.N)
            {
                _selectedPane.ActiveTab?.CreateNewFolder();
                return true;
            }

            if ((modifiers == Keys.Control && keyCode == Keys.L) ||
                (modifiers == Keys.Alt && keyCode == Keys.D) ||
                (modifiers == Keys.None && keyCode == Keys.F4))
            {
                EnterAddressEditMode();
                return true;
            }

            if ((modifiers == Keys.Control && keyCode == Keys.F) ||
                (modifiers == Keys.None && keyCode == Keys.F3))
            {
                _searchTextBox.Focus();
                _searchTextBox.SelectAll();
                return true;
            }

            if (modifiers == Keys.None && keyCode == Keys.F5)
            {
                _selectedPane.ActiveTab?.RefreshView();
                return true;
            }

            if (!IsTextEditControlFocused(m.HWnd))
            {
                // Backspace goes up a folder level
                if (modifiers == Keys.None && keyCode == Keys.Back)
                {
                    _selectedPane.ActiveTab?.NavigateRelative(ShellBrowserInterop.SBSP_PARENT);
                    return true;
                }

                // Tab / Shift+Tab switches active focus between Left Files Pane and Right Files Pane
                if (keyCode == Keys.Tab && modifiers is Keys.None or Keys.Shift)
                {
                    if (_isSplitEnabled)
                    {
                        TabbedExplorerPane nextPane = _selectedPane == _leftFilesPane ? _rightFilesPane : _leftFilesPane;
                        SetSelectedPane(nextPane);
                        nextPane.ActiveTab?.FocusFileListView();
                    }
                    else
                    {
                        _leftFilesPane.ActiveTab?.FocusFileListView();
                    }

                    return true;
                }

                // Alt+Left (Back), Alt+Right (Forward), Alt+Up (Up one level), Alt+Enter (Properties)
                if (modifiers == Keys.Alt)
                {
                    if (keyCode == Keys.Left)
                    {
                        _selectedPane.ActiveTab?.NavigateRelative(ShellBrowserInterop.SBSP_NAVIGATEBACK);
                        return true;
                    }

                    if (keyCode == Keys.Right)
                    {
                        _selectedPane.ActiveTab?.NavigateRelative(ShellBrowserInterop.SBSP_NAVIGATEFORWARD);
                        return true;
                    }

                    if (keyCode == Keys.Up)
                    {
                        _selectedPane.ActiveTab?.NavigateRelative(ShellBrowserInterop.SBSP_PARENT);
                        return true;
                    }

                    if (keyCode == Keys.Return)
                    {
                        _selectedPane.ActiveTab?.InvokeShellVerb("properties");
                        return true;
                    }
                }

                if (modifiers == Keys.None && keyCode == Keys.F2)
                {
                    _selectedPane.ActiveTab?.InvokeShellVerb("rename");
                    return true;
                }

                if (_selectedPane.ActiveTab is not null &&
                    NativeMethods.IsChild(_selectedPane.Handle, m.HWnd))
                {
                    NativeMethods.MSG nativeMsg = new()
                    {
                        hwnd = m.HWnd,
                        message = (uint)m.Msg,
                        wParam = m.WParam,
                        lParam = m.LParam
                    };

                    if (_selectedPane.ActiveTab.TranslateAccelerator(ref nativeMsg))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool IsTextEditControlFocused(IntPtr hwnd)
    {
        if (_addressTextBox.Focused || _searchTextBox.Focused ||
            hwnd == _addressTextBox.Handle || hwnd == _searchTextBox.Handle)
        {
            return true;
        }

        if (hwnd != IntPtr.Zero)
        {
            System.Text.StringBuilder sb = new(32);
            if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 &&
                sb.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            _statusTimer.Stop();
            _statusTimer.Dispose();
            _toolTip.Dispose();
            _uiFont.Dispose();
            _tabFont.Dispose();
            _tabActiveFont.Dispose();
            _addressFont.Dispose();
            _statusFont.Dispose();
            _titleAppIcon.Dispose();
            _windowIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int intVal && intVal == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    private sealed class DoubleBufferedControl : Control
    {
        public DoubleBufferedControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
        }
    }

    private sealed class RoundedBoxPanel : Panel
    {
        private readonly Color _fillColor;
        private readonly Color _borderColor;

        public RoundedBoxPanel(Color fillColor, Color borderColor)
        {
            _fillColor = fillColor;
            _borderColor = borderColor;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new(0, 0, Width - 1, Height - 1);
            using GraphicsPath path = IconFactory.CreateRoundedRect(rect, 4);
            using SolidBrush fillBrush = new(_fillColor);
            using Pen borderPen = new(_borderColor, 1f);
            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);
        }
    }
}
