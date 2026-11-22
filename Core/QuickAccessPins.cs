using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Explorer2.Core;

/// <summary>
/// The folders pinned to Quick access, in their pinned order.
///
/// Windows has no documented call to reorder pins (File Explorer uses an internal one). New pins
/// always go to the end, so a folder is moved by unpinning the pins from the first changed position
/// on and pinning them again in the new order. Each unpin / pin takes 0.1 - 0.8 s, so the new order
/// is shown at once (the cached order changes and <see cref="Changed"/> fires) and the pins are
/// saved on a background thread, one move after another. When the queue is empty the real pin list
/// is read once and checked.
///
/// Changes made elsewhere (File Explorer, other apps) rewrite Windows' pin file; when it changes the
/// pin list is read again and <see cref="Changed"/> fires if it differs.
/// </summary>
internal static class QuickAccessPins
{
    private const string QuickAccessNamespace = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    private static readonly object Sync = new();
    private static List<string>? _cache;
    private static SynchronizationContext? _uiContext;
    private static BlockingCollection<Action<dynamic>>? _jobs;
    private static Thread? _worker;
    private static int _pendingMoves;
    private static long _ignoreNoticesUntil;
    private static PinFileWatcher? _pinFileWatcher;

    /// <summary>Raised on the UI thread when the pinned order changed (here or elsewhere).</summary>
    public static event Action? Changed;

    /// <summary>Pinned folders in order. The first call reads them (slow); later calls use the cache.</summary>
    public static List<string> Current
    {
        get
        {
            lock (Sync)
            {
                if (_cache is not null)
                {
                    return new List<string>(_cache);
                }
            }

            List<string> read = new();
            WithShell(shell => read = ReadPinnedFolders(shell));
            lock (Sync)
            {
                _cache ??= read;
                return new List<string>(_cache);
            }
        }
    }

    /// <summary>Called on the UI thread: starts listening for Quick access changes made elsewhere.</summary>
    public static void StartWatching()
    {
        _uiContext ??= SynchronizationContext.Current;
        if (_pinFileWatcher is null && _uiContext is not null)
        {
            _pinFileWatcher = new PinFileWatcher();
            Application.ApplicationExit += (_, _) => FinishPendingSaves();
        }
    }

    /// <summary>
    /// Puts <paramref name="path"/> at <paramref name="newIndex"/> of the pinned list (pinning it if
    /// needed). The cached order changes now; the pins are saved in the background.
    /// </summary>
    public static void RequestMove(string path, int newIndex)
    {
        List<string> before = Current;
        List<string> after = before.Where(p => !SamePath(p, path)).ToList();
        after.Insert(Math.Clamp(newIndex, 0, after.Count), path);
        if (SameOrder(before, after))
        {
            return;
        }

        lock (Sync)
        {
            _cache = after;
        }

        Interlocked.Increment(ref _pendingMoves);
        RaiseChanged();
        Enqueue(shell =>
        {
            try
            {
                ApplyMove(shell, before, after);
            }
            catch
            {
                // Checked below once the queue is empty
            }

            if (Interlocked.Decrement(ref _pendingMoves) == 0)
            {
                VerifyAfterSave(shell);
            }
        });
    }

    /// <summary>Re-reads the pins in the background (e.g. after Pin to Quick access).</summary>
    public static void NotifyChanged() => Enqueue(Refresh);

    private static void ApplyMove(dynamic shell, List<string> before, List<string> after)
    {
        int first = 0;
        while (first < before.Count && first < after.Count && SamePath(before[first], after[first]))
        {
            first++;
        }

        // Unpin everything from the first changed position on (the Quick access items carry the verb)...
        HashSet<string> toUnpin = new(before.Skip(first), StringComparer.OrdinalIgnoreCase);
        List<dynamic> unpin = new();
        dynamic? quickAccess = shell.NameSpace(QuickAccessNamespace);
        if (quickAccess?.Items() is IEnumerable items)
        {
            foreach (dynamic? item in items)
            {
                if (item?.Path is string itemPath && toUnpin.Contains(itemPath))
                {
                    unpin.Add(item);
                }
            }
        }

        foreach (dynamic item in unpin)
        {
            item.InvokeVerb("unpinfromhome");
        }

        // ...and pin them again in the new order (each pin goes to the end). The verbs finish
        // before they return, so no waiting is needed between them.
        for (int i = first; i < after.Count; i++)
        {
            shell.NameSpace(after[i])?.Self?.InvokeVerb("pintohome");
        }
    }

    private static void VerifyAfterSave(dynamic shell)
    {
        List<string> expected;
        lock (Sync)
        {
            expected = new List<string>(_cache ?? new List<string>());
        }

        List<string> actual = ReadPinnedFolders(shell);
        List<string> missing = expected.Where(p => !actual.Any(a => SamePath(a, p))).ToList();
        foreach (string path in missing)
        {
            // Never lose a pin: pin anything that did not come back (at the end)
            shell.NameSpace(path)?.Self?.InvokeVerb("pintohome");
        }

        if (missing.Count > 0)
        {
            actual = ReadPinnedFolders(shell);
        }

        Interlocked.Exchange(ref _ignoreNoticesUntil, Environment.TickCount64 + 1500);
        if (Volatile.Read(ref _pendingMoves) != 0 || SameOrder(actual, expected))
        {
            return;
        }

        lock (Sync)
        {
            _cache = actual;
        }

        RaiseChanged();
        _uiContext?.Post(_ => MessageBox.Show(
            "Quick access could not be put in the new order. It now shows the order Windows saved.",
            "Quick access", MessageBoxButtons.OK, MessageBoxIcon.Warning), null);
    }

    private static void Refresh(dynamic shell)
    {
        if (Volatile.Read(ref _pendingMoves) != 0)
        {
            return; // our own save is running; it checks the result when done
        }

        List<string> actual = ReadPinnedFolders(shell);
        bool changed;
        lock (Sync)
        {
            changed = _cache is null || !SameOrder(_cache, actual);
            if (changed && Volatile.Read(ref _pendingMoves) == 0)
            {
                _cache = actual;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private static List<string> ReadPinnedFolders(dynamic shell)
    {
        List<string> results = new();
        try
        {
            dynamic? folder = shell.NameSpace(QuickAccessNamespace);
            if (folder?.Items() is not IEnumerable items)
            {
                return results;
            }

            foreach (dynamic? item in items)
            {
                if (item is null)
                {
                    continue;
                }

                try
                {
                    // Frequent (not pinned) folders report IsPinned = false
                    object? pinned = item.ExtendedProperty("System.Home.IsPinned");
                    string? path = item.Path as string;
                    if (Convert.ToBoolean(item.IsFolder) && pinned is not false && !string.IsNullOrWhiteSpace(path))
                    {
                        results.Add(path!);
                    }
                }
                catch
                {
                    // Skip items without a path
                }
            }
        }
        catch
        {
            // Quick access unavailable
        }

        return results;
    }

    private static void RaiseChanged()
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => Changed?.Invoke(), null);
        }
        else
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Runs <paramref name="job"/> on the Quick access thread (STA, one shell object).</summary>
    private static void Enqueue(Action<dynamic> job)
    {
        lock (Sync)
        {
            if (_jobs is null)
            {
                _jobs = new BlockingCollection<Action<dynamic>>();
                BlockingCollection<Action<dynamic>> jobs = _jobs;
                _worker = new Thread(() => RunWorker(jobs)) { IsBackground = true, Name = "Quick access pins" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }

            if (!_jobs.IsAddingCompleted)
            {
                _jobs.Add(job);
            }
        }
    }

    private static void RunWorker(BlockingCollection<Action<dynamic>> jobs)
    {
        WithShell(shell =>
        {
            foreach (Action<dynamic> job in jobs.GetConsumingEnumerable())
            {
                try
                {
                    job(shell);
                }
                catch
                {
                    // Keep the thread alive for later jobs
                }
            }
        });
    }

    /// <summary>On exit, let a save in progress finish so no pins are left unpinned.</summary>
    private static void FinishPendingSaves()
    {
        Thread? worker;
        lock (Sync)
        {
            _jobs?.CompleteAdding();
            worker = _worker;
        }

        worker?.Join(TimeSpan.FromSeconds(15));
    }

    private static void OnChangeNotice()
    {
        if (Volatile.Read(ref _pendingMoves) != 0 || Environment.TickCount64 < Interlocked.Read(ref _ignoreNoticesUntil))
        {
            return; // caused by our own unpin / pin
        }

        Enqueue(Refresh);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SameOrder(List<string> a, List<string> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => SamePath(pair.First, pair.Second));

    private static void WithShell(Action<dynamic> action)
    {
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is not null)
            {
                action(shell);
            }
        }
        catch
        {
            // Shell automation unavailable
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }

    /// <summary>
    /// Watches the file Windows keeps the Quick access pins in (File Explorer's jump list,
    /// f01b4d95cf55d32a.automaticDestinations-ms). Every pin change, from any program, rewrites it;
    /// writes come in bursts, so the pins are read once it is quiet for a moment.
    /// </summary>
    private sealed class PinFileWatcher
    {
        private const string PinFileName = "f01b4d95cf55d32a.automaticDestinations-ms";
        private readonly FileSystemWatcher? _watcher;
        private readonly System.Threading.Timer _quiet;

        public PinFileWatcher()
        {
            _quiet = new System.Threading.Timer(_ => OnChangeNotice());
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Recent\AutomaticDestinations");
            if (!Directory.Exists(folder))
            {
                return;
            }

            _watcher = new FileSystemWatcher(folder, PinFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += (_, _) => Restart();
            _watcher.Created += (_, _) => Restart();
            _watcher.Renamed += (_, _) => Restart();
            _watcher.EnableRaisingEvents = true;
        }

        private void Restart() => _quiet.Change(400, Timeout.Infinite);
    }
}
