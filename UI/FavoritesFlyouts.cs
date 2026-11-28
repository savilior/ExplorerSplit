using System.Runtime.InteropServices;
using Explorer2.Core;

namespace Explorer2.UI;

/// <summary>
/// The open group flyouts of one navigation pane: a chain, each one opened from a group row of the
/// list before it (the first from the navigation pane). A flyout stays while the mouse is over it,
/// over the row it came from, or over a flyout opened from it; shortly after the mouse leaves them
/// it closes. Menus, dialogs and drags started in a flyout hold them open.
/// </summary>
internal sealed class FavoritesFlyouts : IDisposable
{
    private const int CloseDelay = 350;

    private readonly IFavoritesHost _host;
    private readonly List<(Rectangle Anchor, FavoritesFlyoutForm Form)> _chain = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 80 };
    private int _suspended;
    private long _outsideSince;

    public FavoritesFlyouts(IFavoritesHost host)
    {
        _host = host;
        _timer.Tick += (_, _) => Check();
    }

    public bool IsOpen => _chain.Count > 0;

    /// <summary>Opens <paramref name="group"/>'s flyout next to <paramref name="anchor"/> (a row of <paramref name="source"/>).</summary>
    public void Open(FavoritesView source, FavoriteNode group, Rectangle anchor)
    {
        int depth = source.Flyout is { } parent ? _chain.FindIndex(c => c.Form == parent) + 1 : 0;
        if (depth < 0)
        {
            return;
        }

        if (depth < _chain.Count && _chain[depth].Form.Group == group)
        {
            return; // already open
        }

        CloseFromDepth(depth);
        Form? owner = _host.OwnerForm;
        if (owner is null)
        {
            return;
        }

        FavoritesFlyoutForm form = new(_host, group);
        _chain.Add((anchor, form));

        Rectangle sourceBounds = source.RectangleToScreen(source.ClientRectangle);
        Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
        Size size = form.WantedSize(work.Height - 16);
        int x;
        int y;
        if (depth == 0)
        {
            // From the navigation pane: to the right of it (to the left when there is no room)
            x = sourceBounds.Right + 2;
            if (x + size.Width > work.Right)
            {
                x = Math.Max(work.Left, sourceBounds.Left - size.Width - 2);
            }

            y = Math.Clamp(anchor.Top - 4, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - size.Height - 8));
        }
        else
        {
            // A deeper level cascades: right below its row, indented one step, like a tree
            // (above the row when there is no room below)
            int indent = (int)Math.Round(20 * source.DeviceDpi / 96f);
            x = Math.Clamp(anchor.Left + indent, work.Left, Math.Max(work.Left, work.Right - size.Width));
            y = anchor.Bottom;
            if (y + size.Height > work.Bottom)
            {
                y = anchor.Top - size.Height >= work.Top
                    ? anchor.Top - size.Height
                    : Math.Max(work.Top, work.Bottom - size.Height);
            }
        }

        form.Bounds = new Rectangle(x, y, size.Width, size.Height);
        form.Show(owner);
        _outsideSince = 0;
        _timer.Start();
    }

    /// <summary>Closes the flyouts opened from <paramref name="source"/>'s rows (and deeper).</summary>
    public void CloseFrom(FavoritesView source)
    {
        int depth = source.Flyout is { } parent ? _chain.FindIndex(c => c.Form == parent) + 1 : 0;
        if (depth >= 0)
        {
            CloseFromDepth(depth);
        }
    }

    public void CloseAll() => CloseFromDepth(0);

    /// <summary>Closes the flyout <paramref name="form"/> and the ones opened from it.</summary>
    public void Close(FavoritesFlyoutForm form)
    {
        int index = _chain.FindIndex(c => c.Form == form);
        if (index >= 0)
        {
            CloseFromDepth(index);
        }
    }

    private void CloseFromDepth(int depth)
    {
        for (int i = _chain.Count - 1; i >= depth && i >= 0; i--)
        {
            FavoritesFlyoutForm form = _chain[i].Form;
            _chain.RemoveAt(i);
            form.Close();
            form.Dispose();
        }

        if (_chain.Count == 0)
        {
            _timer.Stop();
        }
    }

    /// <summary>Keeps the flyouts open until the returned object is disposed (menus, dialogs, drags).</summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return new Resume(this);
    }

    private sealed class Resume : IDisposable
    {
        private FavoritesFlyouts? _owner;

        public Resume(FavoritesFlyouts owner) => _owner = owner;

        public void Dispose()
        {
            if (_owner is not null)
            {
                _owner._suspended--;
                _owner._outsideSince = 0;
                _owner = null;
            }
        }
    }

    private void Check()
    {
        if (_chain.Count == 0)
        {
            _timer.Stop();
            return;
        }

        if (_suspended > 0)
        {
            return;
        }

        Point cursor = Cursor.Position;
        int keep = 0;
        for (int i = 0; i < _chain.Count; i++)
        {
            if (_chain[i].Anchor.Contains(cursor) || (!_chain[i].Form.IsDisposed && _chain[i].Form.Bounds.Contains(cursor)))
            {
                keep = i + 1;
            }
        }

        if (keep >= _chain.Count)
        {
            _outsideSince = 0;
            return;
        }

        long now = Environment.TickCount64;
        if (_outsideSince == 0)
        {
            _outsideSince = now;
        }
        else if (now - _outsideSince > CloseDelay)
        {
            _outsideSince = 0;
            CloseFromDepth(keep);
        }
    }

    public void Dispose()
    {
        CloseAll();
        _timer.Dispose();
    }
}

/// <summary>
/// A group's entries, shown next to the group when the mouse rests on it. It never takes the
/// keyboard focus from the window (clicks work without activating it).
/// </summary>
internal sealed class FavoritesFlyoutForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private readonly IFavoritesHost _host;
    private readonly FavoritesView _view;

    public FavoritesFlyoutForm(IFavoritesHost host, FavoriteNode group)
    {
        _host = host;
        Group = group;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        bool dark = host.IsDarkMode;
        BackColor = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(251, 251, 253);

        _view = new FavoritesView(host, group, 28) { Dock = DockStyle.Fill, Flyout = this };
        _view.ContentHeightChanged += UpdateSize;
        Controls.Add(_view);
        Favorites.Changed += OnFavoritesChanged;
    }

    public FavoriteNode Group { get; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = new IntPtr(MA_NOACTIVATE);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Rounded corners with the system border and shadow, as Windows 11 menus have
        int round = 3; // DWMWCP_ROUNDSMALL
        DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        if (_host.IsDarkMode)
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        }
    }

    /// <summary>Size for the group's entries, at most <paramref name="maxHeight"/> high (then it scrolls).</summary>
    public Size WantedSize(int maxHeight)
    {
        int width = Math.Clamp(_view.PreferredContentWidth, LogicalToDeviceUnits(200), LogicalToDeviceUnits(440));
        return new Size(width, Math.Min(_view.ContentHeight, maxHeight));
    }

    /// <summary>Grows or shrinks with its entries, keeping its top-left corner.</summary>
    public void UpdateSize()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        Rectangle work = Screen.FromControl(this).WorkingArea;
        Size size = WantedSize(work.Bottom - Top - 8);
        if (size != Size)
        {
            Size = size;
        }
    }

    private void OnFavoritesChanged()
    {
        // The group was removed (here or in another window): close
        if (!IsDisposed && Favorites.ListOf(Group) is null)
        {
            BeginInvoke(() => _host.Flyouts.Close(this));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Favorites.Changed -= OnFavoritesChanged;
        }

        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
