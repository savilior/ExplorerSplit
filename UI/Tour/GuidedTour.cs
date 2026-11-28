using System.Runtime.InteropServices;
using Explorer2.Configuration;
using Explorer2.Core;

namespace Explorer2.UI;

/// <summary>
/// The guided tour, played in the demo Explorer2 started by "Take the tour" (see
/// <see cref="TourLauncher"/>). Four chapters: selection keys, the work environment, Favorites and
/// the two panes. It drives the app through its own commands (no real keys or mouse input, so
/// nothing reaches another program), draws a cursor and highlights over the window
/// (<see cref="TourOverlay"/>), and reads each line aloud with captions (<see cref="TourScript"/>,
/// <see cref="TourVoice"/>). Each line's actions run while it is spoken, and the next line waits for
/// both. While another program is in front the tour pauses and its drawings hide.
/// </summary>
internal sealed class GuidedTour : IMessageFilter
{
    private const int ChapterCount = 4;

    private readonly TrayApplicationContext _tray;
    private readonly TourScript _script;
    private readonly TourVoice _voice;
    private readonly TourOverlay _overlay = new();
    private readonly TourControlBar _bar;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly List<IDisposable> _holds = new();

    private CancellationTokenSource _cts = new();
    private TaskCompletionSource _playing = CompletedSource();
    private int _chapter;
    private int? _requested;
    private long _chapterStarted;
    private bool _paused;
    private bool _autoPaused;
    private bool _captions = true;
    private bool _voiceOn = true;
    private bool _exited;
    private long _awaySince;
    private long _noWindowSince;
    private long _keyShownAt;
    private Func<Rectangle>? _ringTarget;
    private string? _currentLine;
    private string? _stoppedMessage;
    private SynchronizationContext? _ui;

    private GuidedTour(TrayApplicationContext tray, TourScript script)
    {
        _tray = tray;
        _script = script;
        _voice = new TourVoice(script.Language);
        _voiceOn = _voice.IsAvailable;
        _bar = new TourControlBar(script);
        _bar.CommandClicked += OnCommand;
        _timer.Tick += (_, _) => OnTick();
    }

    public static void Start(TrayApplicationContext tray)
    {
        TourScript script;
        try
        {
            script = TourScript.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The tour could not start:\n{ex.Message}", "Explorer2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        GuidedTour tour = new(tray, script);
        (SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext()).Post(_ => tour.Run(), null);
    }

    private static TaskCompletionSource CompletedSource()
    {
        TaskCompletionSource done = new();
        done.SetResult();
        return done;
    }

    // ================================================================== running

    private async void Run()
    {
        _ui = SynchronizationContext.Current;
        _timer.Start();
        Application.AddMessageFilter(this);

        while (!_exited)
        {
            _cts = new CancellationTokenSource();
            _chapterStarted = Environment.TickCount64;
            UpdateBar();
            try
            {
                await (_chapter switch
                {
                    0 => Selection(_cts.Token),
                    1 => WorkEnvironment(_cts.Token),
                    2 => FavoritesChapter(_cts.Token),
                    3 => TwoPanes(_cts.Token),
                    _ => End(_cts.Token)
                });
                _chapter++;
            }
            catch (OperationCanceledException)
            {
                if (_exited)
                {
                    break;
                }

                CleanUp();
                if (_requested is int target)
                {
                    _chapter = target;
                    _requested = null;
                    _stoppedMessage = null;
                }
            }
            catch (Exception ex)
            {
                if (_exited)
                {
                    break;
                }

                // Something wasn't where the tour expected it: say so and wait at the end
                CleanUp();
                _stoppedMessage = $"{_script.Ui("stopped")} {ex.Message}";
                _chapter = ChapterCount;
            }
        }
    }

    private void OnCommand(TourControlBar.Command command)
    {
        switch (command)
        {
            case TourControlBar.Command.Back:
                int target = _chapter >= ChapterCount ? ChapterCount - 1
                    : Environment.TickCount64 - _chapterStarted > 4000 ? _chapter
                    : Math.Max(0, _chapter - 1);
                Jump(target);
                break;
            case TourControlBar.Command.Next:
                if (_chapter < ChapterCount)
                {
                    Jump(_chapter + 1);
                }

                break;
            case TourControlBar.Command.PlayPause:
                _autoPaused = false;
                if (_chapter >= ChapterCount)
                {
                    Jump(0); // at the end: play again from the start
                }
                else
                {
                    SetPaused(!_paused);
                }

                break;
            case TourControlBar.Command.Captions:
                _captions = !_captions;
                _overlay.SetCaption(_captions ? _currentLine : null);
                break;
            case TourControlBar.Command.Voice:
                _voiceOn = !_voiceOn;
                if (!_voiceOn)
                {
                    _voice.Stop();
                }

                break;
            case TourControlBar.Command.Close:
                Exit();
                return;
        }

        UpdateBar();
    }

    private void Jump(int chapter)
    {
        _requested = Math.Clamp(chapter, 0, ChapterCount);
        if (_paused)
        {
            SetPaused(false);
        }

        _cts.Cancel();
    }

    private void SetPaused(bool paused)
    {
        if (paused == _paused)
        {
            return;
        }

        _paused = paused;
        if (paused)
        {
            _playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _voice.Pause();
        }
        else
        {
            _voice.Resume();
            _playing.TrySetResult();
        }

        UpdateBar();
    }

    private void UpdateBar()
    {
        if (_bar.IsDisposed)
        {
            return;
        }

        string label = _chapter < ChapterCount
            ? $"{_chapter + 1}/{ChapterCount}  {(_chapter < _script.Chapters.Count ? _script.Chapters[_chapter] : "")}"
            : _script.Ui("finished");
        _bar.SetState(label, _paused || _chapter >= ChapterCount, _captions, _voiceOn, _voice.IsAvailable, _chapter < ChapterCount);
    }

    private void Exit()
    {
        if (_exited)
        {
            return;
        }

        _exited = true;
        _cts.Cancel();
        CleanUp();
        _timer.Stop();
        Application.RemoveMessageFilter(this);
        _overlay.Dispose();
        _bar.Dispose();
        _voice.Dispose();
        _tray.ExitApplication();
    }

    /// <summary>Esc ends the tour while it plays.</summary>
    public bool PreFilterMessage(ref Message m)
    {
        const int WM_KEYDOWN = 0x0100;
        if (!_exited && _chapter < ChapterCount && m.Msg == WM_KEYDOWN && (Keys)m.WParam.ToInt32() == Keys.Escape)
        {
            _ui?.Post(_ => Exit(), null);
            return true;
        }

        return false;
    }

    /// <summary>Puts things back after a chapter stopped part way: menus, dialogs, flyouts, drawings.</summary>
    private void CleanUp()
    {
        foreach (IDisposable hold in _holds)
        {
            hold.Dispose();
        }

        _holds.Clear();
        foreach (CommanderPromptDialog dialog in Application.OpenForms.OfType<CommanderPromptDialog>().ToList())
        {
            dialog.DialogResult = DialogResult.Cancel;
        }

        if (Win11MenuRenderer.LastOpenedMenu is { IsDisposed: false, Visible: true } menu)
        {
            menu.Close();
        }

        if (Window?.TourFavoritesView is { } favorites)
        {
            favorites.DragLeft();
            favorites.TourCloseFlyouts();
        }

        _voice.Stop();
        Highlight(null);
        _overlay.SetKey(null);
        _overlay.HideCursor();
    }

    private void OnTick()
    {
        if (_exited)
        {
            return;
        }

        DualPaneExplorerForm? window = Window;
        long now = Environment.TickCount64;
        if (window is null)
        {
            // The demo window was closed: the tour (and the demo Explorer2) ends
            _noWindowSince = _noWindowSince == 0 ? now : _noWindowSince;
            if (now - _noWindowSince > 3000)
            {
                Exit();
            }

            return;
        }

        _noWindowSince = 0;
        float scale = window.DeviceDpi / 96f;
        _overlay.SetScale(scale);
        _overlay.SetWindow(window.Bounds);
        _bar.PlaceIn(window.Bounds, scale);

        if (ForegroundIsOurs())
        {
            _awaySince = 0;
            _overlay.SetHidden(false);
            if (!_bar.Visible)
            {
                _bar.Show();
            }

            if (_autoPaused)
            {
                _autoPaused = false;
                SetPaused(false);
            }
        }
        else
        {
            _awaySince = _awaySince == 0 ? now : _awaySince;
            if (now - _awaySince > 600)
            {
                _overlay.SetHidden(true);
                _bar.Hide();
                if (!_paused && _chapter < ChapterCount)
                {
                    _autoPaused = true;
                    SetPaused(true);
                }
            }
        }

        if (_ringTarget is not null)
        {
            _overlay.SetRing(SafeRect(_ringTarget));
        }

        if (_keyShownAt != 0 && now - _keyShownAt > 1100)
        {
            _keyShownAt = 0;
            _overlay.SetKey(null);
        }

        _overlay.RaiseTopMost();
        if (_bar.Visible)
        {
            SetWindowPos(_bar.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
    }

    private static bool ForegroundIsOurs()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return true; // between windows (a window reopening)
        }

        GetWindowThreadProcessId(foreground, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ================================================================== building blocks

    /// <summary>The demo window in front (the newest one, after a theme change reopened it).</summary>
    private DualPaneExplorerForm? Window => _tray.Windows.LastOrDefault(w => !w.IsDisposed && w.Visible);

    private DualPaneExplorerForm W => Window ?? throw new InvalidOperationException(_script.Ui("noWindow"));

    private int S(float value) => (int)Math.Round(value * ((Window?.DeviceDpi ?? 96) / 96f));

    private async Task Gate(CancellationToken ct)
    {
        while (_paused)
        {
            await _playing.Task.WaitAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Waits <paramref name="ms"/> of playing time (paused time doesn't count).</summary>
    private async Task Wait(int ms, CancellationToken ct)
    {
        while (ms > 0)
        {
            await Gate(ct);
            int step = Math.Min(ms, 40);
            await Task.Delay(step, ct);
            ms -= step;
        }
    }

    private async Task WaitUntil(Func<bool> condition, CancellationToken ct, string what, int timeoutMs = 6000)
    {
        for (int waited = 0; !SafeCheck(condition); waited += 50)
        {
            if (waited > timeoutMs)
            {
                throw new TimeoutException(string.Format(_script.Ui("waitedFor"), what));
            }

            await Wait(50, ct);
        }
    }

    private async Task<T> WaitFor<T>(Func<T?> probe, CancellationToken ct, string what, int timeoutMs = 6000) where T : class
    {
        T? found = null;
        await WaitUntil(() => (found = probe()) is not null, ct, what, timeoutMs);
        return found!;
    }

    private static bool SafeCheck(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch
        {
            return false;
        }
    }

    private static Rectangle SafeRect(Func<Rectangle> rect)
    {
        try
        {
            return rect();
        }
        catch
        {
            return Rectangle.Empty;
        }
    }

    /// <summary>Shows and speaks a line; completes when it was spoken (or read, without the voice).</summary>
    private async Task Say(string key, CancellationToken ct)
    {
        string text = _script.Line(key);
        _currentLine = text;
        _overlay.SetCaption(_captions ? text : null);
        await Gate(ct);
        if (_voiceOn && _voice.IsAvailable)
        {
            await _voice.SpeakAsync(text, () => Gate(ct), ct);
        }
        else
        {
            int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            await Wait(Math.Clamp(words * 330 + 700, 1800, 10000), ct);
        }

        await Wait(350, ct);
    }

    private void Highlight(Func<Rectangle>? target)
    {
        _ringTarget = target;
        _overlay.SetRing(target is null ? Rectangle.Empty : SafeRect(target));
    }

    private static Rectangle ScreenRect(Control? control) =>
        control is { IsDisposed: false, IsHandleCreated: true, Visible: true }
            ? control.RectangleToScreen(control.ClientRectangle)
            : Rectangle.Empty;

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    /// <summary>Where the cursor points at a row: over its name, not the middle of a wide row.</summary>
    private Point RowPoint(Rectangle r) => new(r.Left + Math.Min(r.Width / 3, S(90)), r.Top + r.Height / 2);

    /// <summary>Moves the drawn cursor to <paramref name="to"/>, easing in and out.</summary>
    private async Task Glide(Point to, CancellationToken ct, int ms = 650, bool pressed = false, string? dragLabel = null,
        Action<Point>? onStep = null)
    {
        Point from = _overlay.CursorPosition;
        if (from.X < 0)
        {
            Rectangle w = W.Bounds;
            from = new Point(w.Left + w.Width / 2, w.Top + w.Height * 2 / 3);
        }

        int frames = Math.Max(1, ms / 16);
        for (int i = 1; i <= frames; i++)
        {
            await Gate(ct);
            float t = i / (float)frames;
            float e = t < 0.5f ? 2 * t * t : 1 - MathF.Pow(-2 * t + 2, 2) / 2;
            Point p = new((int)(from.X + (to.X - from.X) * e), (int)(from.Y + (to.Y - from.Y) * e));
            _overlay.SetCursor(p, pressed, -1f, dragLabel);
            onStep?.Invoke(p);
            await Task.Delay(16, ct);
        }
    }

    /// <summary>A click where the cursor is: a press and a ripple.</summary>
    private async Task Ripple(CancellationToken ct)
    {
        Point at = _overlay.CursorPosition;
        for (int i = 0; i <= 18; i++)
        {
            await Gate(ct);
            _overlay.SetCursor(at, pressed: i < 7, ripple: i / 18f);
            await Task.Delay(16, ct);
        }

        _overlay.SetCursor(at);
    }

    private async Task ClickAt(Point at, CancellationToken ct)
    {
        await Glide(at, ct);
        await Ripple(ct);
    }

    /// <summary>Shows the key and presses it in the window.</summary>
    private async Task Press(Keys keys, string label, CancellationToken ct, int after = 450)
    {
        await Gate(ct);
        _overlay.SetKey(label);
        _keyShownAt = Environment.TickCount64;
        W.TourKey(keys);
        await Wait(after, ct);
    }

    private async Task<ContextMenuStrip> OpenMenu(Action open, CancellationToken ct)
    {
        ContextMenuStrip? before = Win11MenuRenderer.LastOpenedMenu;
        open();
        ContextMenuStrip menu = await WaitFor(() =>
            Win11MenuRenderer.LastOpenedMenu is { Visible: true } m && m != before ? m : null, ct, _script.Ui("theMenu"));
        await Wait(250, ct);
        return menu;
    }

    private ToolStripMenuItem Item(ToolStrip strip, string text) =>
        strip.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text?.StartsWith(text, StringComparison.Ordinal) == true)
        ?? throw new InvalidOperationException(string.Format(_script.Ui("noItem"), text));

    private static Rectangle ItemRect(ToolStripItem item) =>
        item.Owner is { IsHandleCreated: true, Visible: true } owner ? owner.RectangleToScreen(item.Bounds) : Rectangle.Empty;

    /// <summary>Glides to a menu item and rings it (and selects it, as hovering does).</summary>
    private async Task PointAtItem(ToolStripMenuItem item, CancellationToken ct)
    {
        await WaitUntil(() => ItemRect(item).Width > 0, ct, item.Text ?? "");
        Highlight(() => ItemRect(item));
        await Glide(RowPoint(ItemRect(item)), ct);
        item.Select();
        await Wait(250, ct);
    }

    private async Task<CommanderPromptDialog> OpenDialog(Action open, CancellationToken ct)
    {
        open();
        CommanderPromptDialog dialog = await WaitFor(() =>
            Application.OpenForms.OfType<CommanderPromptDialog>().FirstOrDefault(d => d.Visible), ct, _script.Ui("theDialog"));
        await Wait(300, ct);
        return dialog;
    }

    private async Task TypeInto(CommanderPromptDialog dialog, string text, CancellationToken ct)
    {
        Highlight(() => ScreenRect(dialog.Input));
        await Glide(RowPoint(ScreenRect(dialog.Input)), ct);
        dialog.Input.Text = "";
        foreach (char c in text)
        {
            await Gate(ct);
            dialog.Input.AppendText(c.ToString());
            await Wait(75, ct);
        }

        await Wait(350, ct);
    }

    private async Task ClickButton(Button? button, CancellationToken ct)
    {
        if (button is null)
        {
            return;
        }

        Highlight(() => ScreenRect(button));
        await ClickAt(Center(ScreenRect(button)), ct);
        button.PerformClick();
        Highlight(null);
        await Wait(300, ct);
    }

    /// <summary>
    /// Drags an item of the file list into the Favorites list: the cursor carries its name, the list
    /// shows where it will go, and it is dropped there (a drop that would go anywhere but into the
    /// Favorites list is not made).
    /// </summary>
    private async Task DragToFavorites(string name, Func<FavoritesView, Rectangle> target, CancellationToken ct)
    {
        DualPaneExplorerForm window = W;
        ExplorerTabInstance tab = window.TourActiveTab ?? throw new InvalidOperationException(_script.Ui("noWindow"));
        string path = TourSamples.InProjects(name);
        Rectangle item = await TourFileItems.FindAsync(tab.FileListHandle, name);
        Point start = item.Width > 0
            ? new Point(item.Left + Math.Min(item.Width / 4, S(70)), item.Top + item.Height / 2)
            : Center(window.TourPaneRect(right: false));
        await Glide(start, ct);
        tab.SelectItemByNameWhenReady(name);
        await Wait(300, ct);

        FavoritesView favorites = window.TourFavoritesView ?? throw new InvalidOperationException(_script.Ui("noFavorites"));
        Rectangle drop = target(favorites);
        Point end = RowPoint(drop);
        _overlay.SetCursor(start, pressed: true, dragLabel: name);
        await Wait(200, ct);
        await Glide(end, ct, 1100, pressed: true, dragLabel: name, onStep: p =>
        {
            if (ScreenRect(favorites).Contains(p))
            {
                favorites.DragOverAt(p, external: true);
            }
            else
            {
                favorites.DragLeft();
            }
        });

        (FavoritesViewDropAction action, _) = favorites.DragOverAt(end, external: true);
        await Wait(300, ct);
        if (action is FavoritesViewDropAction.Insert or FavoritesViewDropAction.IntoGroup)
        {
            favorites.DropHere([path]);
        }
        else
        {
            favorites.DragLeft();
        }

        _overlay.SetCursor(end);
        await WaitUntil(() => Favorites.ContainsAnywhere(path), ct, name, 3000);
        favorites.TourCloseFlyouts();
    }

    private static FavoritesView? FlyoutView(FavoriteNode group) =>
        Application.OpenForms.OfType<FavoritesFlyoutForm>()
            .FirstOrDefault(f => f.Visible && f.Group == group)?
            .Controls.OfType<FavoritesView>().FirstOrDefault();

    private static FavoriteNode Node(IEnumerable<FavoriteNode>? list, string path) =>
        list?.FirstOrDefault(n => Favorites.SamePath(n.Path, path)) ?? throw new InvalidOperationException(path);

    private static bool IsAt(ExplorerTabInstance? tab, string path) =>
        tab is not null && (Favorites.SamePath(tab.DisplayPath, path) || Favorites.SamePath(tab.ParsingPath, path));

    /// <summary>One pane, on the sample Projects folder, nothing selected, the file list focused.</summary>
    private async Task OpenProjects(CancellationToken ct)
    {
        TourSamples.EnsureFolders();
        DualPaneExplorerForm window = W;
        window.TourSelectPane(right: false);
        if (window.TourActiveTab is { } tab && !IsAt(tab, TourSamples.Projects))
        {
            tab.NavigateToPath(TourSamples.Projects);
        }

        await WaitUntil(() => IsAt(W.TourActiveTab, TourSamples.Projects) && W.TourActiveTab!.GetAllItemPathsInViewOrder().Count >= 8,
            ct, "Projects");
        ExplorerTabInstance active = W.TourActiveTab!;
        active.SelectNoneItems();
        active.ResetMarking();
        active.FocusFileListView();
        await Wait(300, ct);
    }

    private async Task SetTheme(ThemeMode mode, CancellationToken ct)
    {
        if (_tray.Theme == mode)
        {
            return;
        }

        DualPaneExplorerForm old = W;
        _tray.SetTheme(mode);
        await WaitUntil(() => Window is { } w && w != old, ct, _script.Ui("theWindow"), 10000);
        await Wait(600, ct);
    }

    // ================================================================== the chapters

    /// <summary>1. Looks like File Explorer, with faster selection: Space, arrows, Esc, Num *.</summary>
    private async Task Selection(CancellationToken ct)
    {
        await OpenProjects(ct);
        await Say("welcome", ct);
        Highlight(() => W.TourPaneRect(right: false));
        await Say("c1.native", ct);

        W.TourKey(Keys.Home);
        await Wait(300, ct);
        Task line = Say("c1.space", ct);
        await Wait(1600, ct);
        for (int i = 0; i < 3; i++)
        {
            await Press(Keys.Space, "Space", ct, 600);
        }

        await line;
        line = Say("c1.arrows", ct);
        await Wait(1200, ct);
        await Press(Keys.Up, "↑", ct, 700);
        await Press(Keys.Space, "Space", ct, 600);
        await line;

        line = Say("c1.esc", ct);
        await Wait(900, ct);
        await Press(Keys.Escape, "Esc", ct);
        await line;

        line = Say("c1.starAll", ct);
        await Wait(1800, ct);
        await Press(Keys.Multiply, "Num *", ct);
        await line;

        await Press(Keys.Escape, "Esc", ct, 600);
        W.TourActiveTab?.ResetMarking();
        W.TourKey(Keys.Home);
        await Wait(300, ct);
        await Press(Keys.Space, "Space", ct, 500);
        await Press(Keys.Space, "Space", ct, 700);
        line = Say("c1.starInvert", ct);
        await Wait(2000, ct);
        await Press(Keys.Multiply, "Num *", ct);
        await line;

        line = Say("c1.escAny", ct);
        await Wait(1000, ct);
        await Press(Keys.Escape, "Esc", ct);
        await line;
        Highlight(null);
    }

    /// <summary>2. Preferences: default File Explorer, the title bar button, the theme applied at once.</summary>
    private async Task WorkEnvironment(CancellationToken ct)
    {
        await SetTheme(ThemeMode.Light, ct);
        await OpenProjects(ct);

        Task line = Say("c2.intro", ct);
        Highlight(() => ScreenRect(W.TourControl("See more")));
        await Wait(1200, ct);
        await ClickAt(Center(ScreenRect(W.TourControl("See more"))), ct);
        ContextMenuStrip menu = await OpenMenu(W.TourShowMoreMenu, ct);
        ToolStripMenuItem preferences = Item(menu, "Preferences");
        await PointAtItem(preferences, ct);
        preferences.ShowDropDown();
        await line;

        line = Say("c2.default", ct);
        await PointAtItem(Item(preferences.DropDown, "Replace Default File Explorer"), ct);
        await line;

        line = Say("c2.button", ct);
        await PointAtItem(Item(preferences.DropDown, "Enable Title Bar Toggle Button"), ct);
        await line;

        line = Say("c2.theme", ct);
        ToolStripMenuItem theme = Item(preferences.DropDown, "Theme");
        await PointAtItem(theme, ct);
        theme.ShowDropDown();
        await Wait(300, ct);
        ToolStripMenuItem dark = Item(theme.DropDown, "Dark");
        await PointAtItem(dark, ct);
        await Ripple(ct);
        Highlight(null);
        DualPaneExplorerForm old = W;
        dark.PerformClick();
        menu.Close();
        await WaitUntil(() => Window is { } w && w != old, ct, _script.Ui("theWindow"), 10000);
        await line;

        Highlight(() => W.TourPaneRect(right: false));
        await Say("c2.after", ct);
        Highlight(null);
    }

    /// <summary>3. Favorites: drag in a file, nest a folder, flyouts, open, rename, group.</summary>
    private async Task FavoritesChapter(CancellationToken ct)
    {
        Favorites.ResetTo(TourSamples.DefaultFavorites());
        await OpenProjects(ct);
        FavoritesView favorites = await WaitFor(() => W.TourFavoritesView, ct, _script.Ui("noFavorites"));
        FavoriteNode projects = Node(Favorites.Roots, TourSamples.Projects);
        await WaitUntil(() => favorites.TourRowRect(projects).Width > 0, ct, "Projects");

        Highlight(() => ScreenRect(W.TourFavoritesView));
        await Say("c3.intro", ct);
        Highlight(null);

        Task line = Say("c3.dragFile", ct);
        await DragToFavorites("Notes.txt", f => f.TourEndRect(), ct);
        await line;

        line = Say("c3.nest", ct);
        await DragToFavorites("Music", f => f.TourRowRect(projects), ct);
        await line;

        // Flyouts: held open by the tour (the real mouse may be anywhere)
        _holds.Add(favorites.TourHoldFlyouts());
        line = Say("c3.flyout", ct);
        await Glide(RowPoint(favorites.TourRowRect(projects)), ct);
        await Wait(500, ct);
        favorites.TourOpenFlyout(projects);
        FavoritesView first = await WaitFor(() => FlyoutView(projects), ct, "Projects");
        FavoriteNode invoices = Node(projects.Children, TourSamples.InProjects("Invoices"));
        await WaitUntil(() => first.TourRowRect(invoices).Width > 0, ct, "Invoices");
        await Glide(RowPoint(first.TourRowRect(invoices)), ct, 800);
        await Wait(500, ct);
        first.TourOpenFlyout(invoices);
        FavoritesView second = await WaitFor(() => FlyoutView(invoices), ct, "Invoices");
        FavoriteNode year = Node(invoices.Children, TourSamples.InProjects(@"Invoices\2026"));
        await WaitUntil(() => second.TourRowRect(year).Width > 0, ct, "2026");
        await Glide(RowPoint(second.TourRowRect(year)), ct, 800);
        await line;

        line = Say("c3.open", ct);
        await Wait(700, ct);
        await Ripple(ct);
        second.TourActivate(year);
        foreach (IDisposable hold in _holds)
        {
            hold.Dispose();
        }

        _holds.Clear();
        await WaitUntil(() => IsAt(W.TourActiveTab, TourSamples.InProjects(@"Invoices\2026")), ct, "2026");
        Highlight(() => W.TourPaneRect(right: false));
        await line;
        Highlight(null);

        line = Say("c3.rename", ct);
        await Glide(RowPoint(favorites.TourRowRect(projects)), ct);
        await Ripple(ct);
        ContextMenuStrip menu = await OpenMenu(() => favorites.TourShowMenu(projects), ct);
        ToolStripMenuItem rename = Item(menu, "Rename");
        await PointAtItem(rename, ct);
        await Ripple(ct);
        Highlight(null);
        CommanderPromptDialog dialog = await OpenDialog(rename.PerformClick, ct);
        await TypeInto(dialog, _script.Ui("renameTo"), ct);
        await ClickButton(dialog.OkButton, ct);
        await line;

        line = Say("c3.group", ct);
        FavoriteNode media = Favorites.Roots.FirstOrDefault(n => n.IsGroup && n.Name == "Media")
                             ?? throw new InvalidOperationException("Media");
        await Glide(RowPoint(favorites.TourRowRect(media)), ct);
        await Ripple(ct);
        menu = await OpenMenu(() => favorites.TourShowMenu(media), ct);
        ToolStripMenuItem newGroup = Item(menu, "New group");
        if (menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "New group") is { } exact)
        {
            newGroup = exact; // the group's own "New group", not "New group inside"
        }

        await PointAtItem(newGroup, ct);
        await Ripple(ct);
        Highlight(null);
        dialog = await OpenDialog(newGroup.PerformClick, ct);
        await TypeInto(dialog, _script.Ui("groupName"), ct);
        await ClickButton(dialog.OkButton, ct);
        Highlight(() => ScreenRect(W.TourFavoritesView));
        await line;
        Highlight(null);
    }

    /// <summary>4. Two panes: split, Tab between them, mark with Space, F5 copies to the other pane.</summary>
    private async Task TwoPanes(CancellationToken ct)
    {
        TourSamples.ClearBackup();
        W.TourSetSplit(false);
        await OpenProjects(ct);

        Task line = Say("c4.intro", ct);
        await Wait(2600, ct);
        await Press(Keys.Control | Keys.Shift | Keys.S, "Ctrl + Shift + S", ct, 600);
        await WaitUntil(() => W.TourIsSplit && W.TourPaneRect(right: true).Width > 0, ct, _script.Ui("theSplit"));
        Highlight(() => W.TourPaneRect(right: true));
        await line;

        line = Say("c4.tab", ct);
        W.TourSelectPane(right: false);
        await Wait(900, ct);
        await Press(Keys.Tab, "Tab", ct, 200);
        Highlight(() => W.TourPaneRect(W.TourRightPaneActive));
        await Wait(1100, ct);
        await Press(Keys.Tab, "Tab", ct, 200);
        await line;

        line = Say("c4.mark", ct);
        W.TourSelectPane(right: false);
        Highlight(() => W.TourPaneRect(right: false));
        W.TourActiveTab?.ResetMarking();
        W.TourKey(Keys.Home);
        await Wait(500, ct);
        for (int i = 0; i < 4; i++)
        {
            await Press(Keys.Down, "↓", ct, 280);
        }

        await Press(Keys.Space, "Space", ct, 600);
        await Press(Keys.Space, "Space", ct, 600);
        await line;

        line = Say("c4.keys", ct);
        Highlight(() => W.TourFunctionKeyBarRect);
        await Wait(1500, ct);
        foreach (Keys key in new[] { Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8 })
        {
            await Glide(Center(W.TourFunctionKeyRect(key)), ct, 380);
            await Wait(220, ct);
        }

        await line;

        line = Say("c4.copy", ct);
        Highlight(() => W.TourFunctionKeyRect(Keys.F5));
        await ClickAt(Center(W.TourFunctionKeyRect(Keys.F5)), ct);
        Highlight(null);
        CommanderPromptDialog dialog = await OpenDialog(() => W.TourFunctionKey(Keys.F5), ct);
        Highlight(() => ScreenRect(dialog.Input));
        await Glide(RowPoint(ScreenRect(dialog.Input)), ct);
        await Wait(1200, ct);
        if (!Favorites.SamePath(dialog.InputText, TourSamples.Backup))
        {
            // Copies only ever go to the sample Backup folder
            dialog.DialogResult = DialogResult.Cancel;
            throw new InvalidOperationException(_script.Ui("wrongTarget"));
        }

        await ClickButton(dialog.OkButton, ct);
        await WaitUntil(() => Directory.EnumerateFileSystemEntries(TourSamples.Backup).Any(), ct, "Backup", 10000);
        await line;

        Highlight(() => W.TourPaneRect(right: true));
        await Say("c4.done", ct);
        Highlight(null);
    }

    /// <summary>The end (or where the tour stopped): waits for Back, Play again or Close.</summary>
    private async Task End(CancellationToken ct)
    {
        _overlay.HideCursor();
        Highlight(null);
        UpdateBar();
        if (_stoppedMessage is not null)
        {
            _currentLine = _stoppedMessage;
            _overlay.SetCaption(_stoppedMessage);
        }
        else
        {
            await Say("end", ct);
        }

        await Task.Delay(Timeout.Infinite, ct);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
