using System.Text.Json;
using System.Text.Json.Serialization;

namespace Explorer2.Core;

/// <summary>
/// One entry of the Favorites section: a shortcut to a folder or file (<see cref="Path"/> set), or a
/// group that holds other entries (<see cref="Children"/> set). Groups can be nested.
/// </summary>
internal sealed class FavoriteNode
{
    /// <summary>Name shown for a group, or a custom name for a shortcut (null = the target's own name).</summary>
    public string? Name { get; set; }

    /// <summary>Shell parsing path of the target (a file-system path or "::{GUID}"); null for a group.</summary>
    public string? Path { get; set; }

    /// <summary>Entries of a group; null for a shortcut.</summary>
    public List<FavoriteNode>? Children { get; set; }

    /// <summary>A group shown open in the navigation pane.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Expanded { get; set; }

    [JsonIgnore]
    public bool IsGroup => Children is not null;

    public static FavoriteNode Shortcut(string path) => new() { Path = path };

    public static FavoriteNode Group(string name) => new() { Name = name, Children = new List<FavoriteNode>() };
}

/// <summary>
/// The Favorites list shown in the navigation pane instead of the Quick access pins. It lives in
/// %AppData%\Explorer2\favorites.json (EXPLORER2_FAVORITES_FILE overrides it, for tests). The first
/// time it is used it starts with a copy of the Quick access pins; Quick access is never changed.
/// Every change is saved at once and raises <see cref="Changed"/> in every window. A change made by
/// another Explorer2 process (a standalone window) is picked up from the file.
/// </summary>
internal static class Favorites
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static List<FavoriteNode>? _roots;
    private static FileSystemWatcher? _watcher;
    private static SynchronizationContext? _uiContext;
    private static long _ignoreFileChangesUntil;

    /// <summary>Raised on the UI thread after any change.</summary>
    public static event Action? Changed;

    private static string FilePath =>
        Environment.GetEnvironmentVariable("EXPLORER2_FAVORITES_FILE") is { Length: > 0 } overridePath
            ? overridePath
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Explorer2", "favorites.json");

    /// <summary>The top-level entries. The first call loads the file (or copies the Quick access pins).</summary>
    public static List<FavoriteNode> Roots
    {
        get
        {
            if (_roots is null)
            {
                _uiContext ??= SynchronizationContext.Current;
                _roots = Load() ?? ImportQuickAccessPins();
                AddDefaultsOnce();
                StartWatching();
            }

            return _roots;
        }
    }

    private static List<FavoriteNode>? Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                List<FavoriteNode>? loaded = JsonSerializer.Deserialize<List<FavoriteNode>>(File.ReadAllText(FilePath), SerializerOptions);
                if (loaded is not null)
                {
                    Clean(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // Unreadable file: start again from the pins (the bad file is overwritten on the next change)
        }

        return null;
    }

    /// <summary>Drops entries that are neither a shortcut nor a group (hand-edited file).</summary>
    private static void Clean(List<FavoriteNode> list)
    {
        list.RemoveAll(n => n.Children is null && string.IsNullOrWhiteSpace(n.Path));
        foreach (FavoriteNode group in list.Where(n => n.Children is not null))
        {
            group.Path = null;
            Clean(group.Children!);
        }
    }

    private static List<FavoriteNode> ImportQuickAccessPins()
    {
        List<FavoriteNode> roots = new();
        try
        {
            foreach (string pin in QuickAccessPins.Current)
            {
                if (!roots.Any(n => SamePath(n.Path, pin)))
                {
                    roots.Add(FavoriteNode.Shortcut(pin));
                }
            }
        }
        catch
        {
            // No pins readable: start empty
        }

        _roots = roots;
        Save();
        return roots;
    }

    public const string RecycleBinPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>
    /// One-time changes to the stored list, remembered in the file next to it. "RecycleBinFixed":
    /// the Recycle Bin moved from the list to the fixed rows at the top of the navigation pane (with
    /// Home and Gallery), so the entry earlier versions added to the list is taken out.
    /// </summary>
    private static void AddDefaultsOnce()
    {
        string markerPath = FilePath + ".defaults";
        try
        {
            HashSet<string> done = File.Exists(markerPath)
                ? File.ReadAllLines(markerPath).Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (done.Contains("RecycleBinFixed"))
            {
                return;
            }

            if (RemoveFixedPaths(_roots!))
            {
                Save();
            }

            done.Add("RecycleBinFixed");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(markerPath)!);
            File.WriteAllLines(markerPath, done);
        }
        catch
        {
            // Tried again next start
        }
    }

    /// <summary>Paths always shown above the list (Home and Gallery are not stored, so only the Recycle Bin).</summary>
    public static bool IsFixedPath(string? path) => SamePath(path, RecycleBinPath);

    private static bool RemoveFixedPaths(List<FavoriteNode> list)
    {
        bool removed = list.RemoveAll(n => !n.IsGroup && IsFixedPath(n.Path)) > 0;
        foreach (FavoriteNode group in list.Where(n => n.IsGroup))
        {
            removed |= RemoveFixedPaths(group.Children!);
        }

        return removed;
    }

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The list that holds <paramref name="node"/> (the top level or a group's children).</summary>
    public static List<FavoriteNode>? ListOf(FavoriteNode node) => FindList(Roots, node);

    private static List<FavoriteNode>? FindList(List<FavoriteNode> list, FavoriteNode node)
    {
        if (list.Contains(node))
        {
            return list;
        }

        foreach (FavoriteNode group in list.Where(n => n.IsGroup))
        {
            if (FindList(group.Children!, node) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The group that holds <paramref name="node"/>, or null at the top level.</summary>
    public static FavoriteNode? ParentOf(FavoriteNode node) => FindParent(Roots, null, node);

    private static FavoriteNode? FindParent(List<FavoriteNode> list, FavoriteNode? owner, FavoriteNode node)
    {
        if (list.Contains(node))
        {
            return owner;
        }

        foreach (FavoriteNode group in list.Where(n => n.IsGroup))
        {
            if (FindParent(group.Children!, group, node) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="node"/> is <paramref name="ancestor"/> or inside it.</summary>
    public static bool IsInside(FavoriteNode node, FavoriteNode ancestor)
    {
        if (node == ancestor)
        {
            return true;
        }

        return ancestor.IsGroup && ancestor.Children!.Any(c => IsInside(node, c));
    }

    /// <summary>True when the top level (not inside groups) already has a shortcut to <paramref name="path"/>.</summary>
    public static bool ContainsAtTopLevel(string path) => Roots.Any(n => SamePath(n.Path, path));

    /// <summary>True when any shortcut, at any level, points to <paramref name="path"/>.</summary>
    public static bool ContainsAnywhere(string path) => Flatten(Roots).Any(n => SamePath(n.Path, path));

    public static IEnumerable<FavoriteNode> Flatten(IEnumerable<FavoriteNode> list)
    {
        foreach (FavoriteNode node in list)
        {
            yield return node;
            if (node.IsGroup)
            {
                foreach (FavoriteNode child in Flatten(node.Children!))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>
    /// Adds shortcuts to <paramref name="paths"/> in <paramref name="group"/> (null = top level) at
    /// <paramref name="index"/> (-1 = at the end). A path the list already holds is skipped.
    /// </summary>
    public static int AddPaths(IEnumerable<string> paths, FavoriteNode? group = null, int index = -1)
    {
        List<FavoriteNode> list = group?.Children ?? Roots;
        int at = index < 0 || index > list.Count ? list.Count : index;
        int added = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || IsFixedPath(path) || list.Any(n => SamePath(n.Path, path)))
            {
                continue;
            }

            list.Insert(at++, FavoriteNode.Shortcut(path));
            added++;
        }

        if (added > 0)
        {
            Commit();
        }

        return added;
    }

    /// <summary>Moves <paramref name="node"/> into <paramref name="group"/> (null = top level) before position <paramref name="index"/>.</summary>
    public static void Move(FavoriteNode node, FavoriteNode? group, int index)
    {
        if (group is not null && IsInside(group, node))
        {
            return; // a group can't go inside itself
        }

        List<FavoriteNode>? from = ListOf(node);
        List<FavoriteNode> to = group?.Children ?? Roots;
        if (from is null)
        {
            return;
        }

        int oldIndex = from.IndexOf(node);
        if (from == to && index > oldIndex)
        {
            index--; // the node's own slot goes away first
        }

        if (from == to && index == oldIndex)
        {
            return;
        }

        from.RemoveAt(oldIndex);
        to.Insert(Math.Clamp(index, 0, to.Count), node);
        Commit();
    }

    public static void Remove(FavoriteNode node)
    {
        if (ListOf(node) is { } list && list.Remove(node))
        {
            Commit();
        }
    }

    /// <summary>Removes the top-level shortcut to <paramref name="path"/> (the file view's "Remove from Favorites").</summary>
    public static void RemovePath(string path)
    {
        if (Roots.RemoveAll(n => SamePath(n.Path, path)) > 0)
        {
            Commit();
        }
    }

    public static void Rename(FavoriteNode node, string? name)
    {
        node.Name = string.IsNullOrWhiteSpace(name) ? (node.IsGroup ? node.Name : null) : name.Trim();
        Commit();
    }

    public static FavoriteNode NewGroup(FavoriteNode? parent, int index, string name)
    {
        List<FavoriteNode> list = parent?.Children ?? Roots;
        FavoriteNode group = FavoriteNode.Group(name);
        list.Insert(index < 0 || index > list.Count ? list.Count : index, group);
        Commit();
        return group;
    }

    public static void SetExpanded(FavoriteNode group, bool expanded)
    {
        if (group.IsGroup && group.Expanded != expanded)
        {
            group.Expanded = expanded;
            Commit();
        }
    }

    /// <summary>A free "New group" name at this level.</summary>
    public static string NextGroupName(FavoriteNode? parent)
    {
        List<FavoriteNode> list = parent?.Children ?? Roots;
        string name = "New group";
        for (int i = 2; list.Any(n => n.IsGroup && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"New group ({i})";
        }

        return name;
    }

    private static void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    private static void Save()
    {
        if (_roots is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            _ignoreFileChangesUntil = Environment.TickCount64 + 1500;
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_roots, SerializerOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Not saved; the list in memory still works
        }
    }

    /// <summary>Another Explorer2 process changed the file: read it again.</summary>
    private static void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            string directory = System.IO.Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            FileSystemEventHandler onChange = (_, _) => OnFileChanged();
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (_, _) => OnFileChanged();
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null;
        }
    }

    private static void OnFileChanged()
    {
        if (Environment.TickCount64 < _ignoreFileChangesUntil || _uiContext is null)
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            Thread.Sleep(50); // let the writer finish
            if (Load() is { } loaded &&
                JsonSerializer.Serialize(loaded, SerializerOptions) != JsonSerializer.Serialize(_roots, SerializerOptions))
            {
                _roots = loaded;
                Changed?.Invoke();
            }
        }, null);
    }
}
