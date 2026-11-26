using Explorer2.Configuration;

namespace Explorer2.Core;

/// <summary>
/// The fixed rows at the top of the navigation pane, above the Favorites list: Home, Gallery, Recycle
/// Bin, Desktop, Downloads and Documents. Each can be hidden (tray menu, "Navigation Pane Top
/// Section...") and the shown rows are reordered by dragging them. Order and hidden rows are kept in
/// settings.json; a change raises <see cref="Changed"/> in every window of this process.
/// </summary>
internal static class TopSection
{
    public const string GalleryPath = "::{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";

    /// <summary>One row. <see cref="AcceptsDrops"/>: dropped files go into it (only the Recycle Bin, which deletes them).</summary>
    public sealed record Item(string Id, string Name, string Path, bool AcceptsDrops);

    private static readonly string[] DefaultOrder = { "Home", "Gallery", "RecycleBin", "Desktop", "Downloads", "Documents" };

    private static Dictionary<string, Item>? _items;
    private static bool _loaded;

    /// <summary>Raised on the UI thread after the order or the hidden rows changed.</summary>
    public static event Action? Changed;

    private static Dictionary<string, Item> Items => _items ??= CreateItems();

    private static Dictionary<string, Item> CreateItems()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string downloads = ExplorerDetector.GetDownloadsPath() ?? System.IO.Path.Combine(userProfile, "Downloads");

        Item[] items =
        {
            new("Home", "Home", ExplorerDetector.HomeShellGuid, false),
            new("Gallery", "Gallery", GalleryPath, false),
            new("RecycleBin", "Recycle Bin", Favorites.RecycleBinPath, true),
            new("Desktop", "Desktop", string.IsNullOrEmpty(desktop) ? System.IO.Path.Combine(userProfile, "Desktop") : desktop, false),
            new("Downloads", "Downloads", downloads, false),
            new("Documents", "Documents", string.IsNullOrEmpty(documents) ? System.IO.Path.Combine(userProfile, "Documents") : documents, false)
        };

        return items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureLoaded()
    {
        if (!_loaded)
        {
            _loaded = true;
            AppSettings.Load(); // fills AppSettings.TopSectionOrder / TopSectionHidden
        }
    }

    /// <summary>Every row in the current order, shown or not.</summary>
    public static IReadOnlyList<Item> All
    {
        get
        {
            EnsureLoaded();
            List<string> order = new();
            foreach (string id in (AppSettings.StoredTopSectionOrder ?? new List<string>()).Concat(DefaultOrder))
            {
                if (Items.ContainsKey(id) && !order.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(id);
                }
            }

            return order.Select(id => Items[id]).ToList();
        }
    }

    /// <summary>The rows shown, in order.</summary>
    public static IReadOnlyList<Item> Visible => All.Where(i => !IsHidden(i.Id)).ToList();

    public static bool IsHidden(string id)
    {
        EnsureLoaded();
        return AppSettings.StoredTopSectionHidden?.Contains(id, StringComparer.OrdinalIgnoreCase) == true;
    }

    /// <summary>File-system paths of the folder rows (Desktop, Downloads, Documents), shown or not.</summary>
    public static IEnumerable<string> FolderPaths =>
        new[] { "Desktop", "Downloads", "Documents" }.Select(id => Items[id].Path);

    /// <summary>True when a shown row points to <paramref name="path"/>.</summary>
    public static bool IsShownPath(string? path) => Visible.Any(i => Favorites.SamePath(i.Path, path));

    /// <summary>True when a shown row points to <paramref name="path"/> and dropped files go into it.</summary>
    public static bool AcceptsDrops(string? path) => Visible.Any(i => i.AcceptsDrops && Favorites.SamePath(i.Path, path));

    /// <summary>Shows only the rows not in <paramref name="hiddenIds"/>.</summary>
    public static void SetHidden(IEnumerable<string> hiddenIds)
    {
        List<string> hidden = hiddenIds.Where(Items.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save(All.Select(i => i.Id).ToList(), hidden);
    }

    /// <summary>
    /// Moves the shown row <paramref name="id"/> before the shown row at <paramref name="visibleIndex"/>
    /// (the count of shown rows = to the end). Hidden rows keep their places.
    /// </summary>
    public static void MoveVisible(string id, int visibleIndex)
    {
        List<string> all = All.Select(i => i.Id).ToList();
        List<string> visible = all.Where(i => !IsHidden(i)).ToList();
        int oldIndex = visible.FindIndex(v => string.Equals(v, id, StringComparison.OrdinalIgnoreCase));
        if (oldIndex < 0)
        {
            return;
        }

        if (visibleIndex > oldIndex)
        {
            visibleIndex--; // the row's own slot goes away first
        }

        visibleIndex = Math.Clamp(visibleIndex, 0, visible.Count - 1);
        if (visibleIndex == oldIndex)
        {
            return;
        }

        visible.RemoveAt(oldIndex);
        visible.Insert(visibleIndex, id);

        // Put the new order of the shown rows back into the slots shown rows had
        int next = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (!IsHidden(all[i]))
            {
                all[i] = visible[next++];
            }
        }

        Save(all, AppSettings.StoredTopSectionHidden ?? new List<string>());
    }

    private static void Save(List<string> order, List<string> hidden)
    {
        AppSettings settings = AppSettings.Load();
        settings.TopSectionOrder = order;
        settings.TopSectionHidden = hidden;
        settings.Save();
        Changed?.Invoke();
    }
}
