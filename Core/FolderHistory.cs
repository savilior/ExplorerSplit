using System.Text.Json;

namespace Explorer2.Core;

/// <summary>One folder in the history: its path and the name shown for it.</summary>
internal sealed record FolderHistoryEntry(string Path, string Name);

/// <summary>
/// Recently opened folders, newest first, shared by all windows: the address bar's history list and
/// the taskbar's "Recent" list. Saved in %AppData%\Explorer2\history.json (EXPLORER2_HISTORY_FILE
/// overrides it, for tests). Search results are not kept.
/// </summary>
internal static class FolderHistory
{
    private const int MaxEntries = 25;

    private static List<FolderHistoryEntry>? _entries;
    private static System.Windows.Forms.Timer? _saveTimer;

    /// <summary>Raised on the UI thread after a folder was added or the list cleared.</summary>
    public static event Action? Changed;

    private static string FilePath =>
        Environment.GetEnvironmentVariable("EXPLORER2_HISTORY_FILE") is { Length: > 0 } overridePath
            ? overridePath
            : System.IO.Path.Combine(Configuration.AppPaths.DataDirectory, "history.json");

    public static IReadOnlyList<FolderHistoryEntry> Entries => _entries ??= Load();

    public static IReadOnlyList<string> Paths => Entries.Select(e => e.Path).ToList();

    /// <summary>Puts <paramref name="path"/> at the top (once).</summary>
    public static void Add(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        List<FolderHistoryEntry> entries = _entries ??= Load();
        if (entries.Count > 0 && Favorites.SamePath(entries[0].Path, path))
        {
            return;
        }

        entries.RemoveAll(e => Favorites.SamePath(e.Path, path));
        entries.Insert(0, new FolderHistoryEntry(path, string.IsNullOrWhiteSpace(name) ? path : name));
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        (_entries ??= Load()).Clear();
        ScheduleSave();
        Changed?.Invoke();
    }

    private static List<FolderHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath) &&
                JsonSerializer.Deserialize<List<FolderHistoryEntry>>(File.ReadAllText(FilePath)) is { } loaded)
            {
                return loaded.Where(e => !string.IsNullOrWhiteSpace(e.Path)).Take(MaxEntries).ToList();
            }
        }
        catch
        {
            // Start empty
        }

        return new List<FolderHistoryEntry>();
    }

    /// <summary>Saves a moment later, so fast clicking through folders writes the file once.</summary>
    private static void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                Save();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void Save()
    {
        if (_entries is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Not saved; the list in memory still works
        }
    }
}
