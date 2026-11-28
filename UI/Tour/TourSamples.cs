using System.Text.Json;
using System.Text.Json.Serialization;
using Explorer2.Core;

namespace Explorer2.UI;

/// <summary>
/// The guided tour's sample folders and data, in %TEMP%\Explorer2Tour. The tour only ever changes
/// things in there: its Favorites, settings and the Backup folder it copies into.
/// </summary>
internal static class TourSamples
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "Explorer2Tour");

    public static string Projects => Path.Combine(Root, "Projects");

    public static string Backup => Path.Combine(Root, "Backup");

    /// <summary>The demo instance's data folder (EXPLORER2_DATA_DIR).</summary>
    public static string Data => Path.Combine(Root, "data");

    private static readonly string[] Folders = ["Designs", "Invoices", @"Invoices\2025", @"Invoices\2026", "Music", "Photos"];

    private static readonly (string Name, string Text)[] Files =
    [
        ("Budget.csv", "Item,Cost\r\nLaptop stand,51\r\nCables,12\r\n"),
        ("Notes.txt", "Explorer2 tour\r\n\r\nSplit mode, tabs, Favorites and Total Commander keys in one window.\r\n"),
        ("Plan.md", "# Plan\r\n- Try split mode\r\n- Pin favorites\r\n"),
        ("Readme.txt", "Sample files for the Explorer2 tour.\r\n"),
        (@"Invoices\2026\Invoice 001.txt", "Invoice 001\r\n")
    ];

    /// <summary>Starts over: new sample folders and data, the old ones removed.</summary>
    public static void CreateFresh()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        EnsureFolders();
        Directory.CreateDirectory(Data);

        JsonSerializerOptions options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        File.WriteAllText(Path.Combine(Data, "favorites.json"), JsonSerializer.Serialize(DefaultFavorites(), options));
        // Marks the one-time Favorites clean-ups as done, so the list is used as written
        File.WriteAllLines(Path.Combine(Data, "favorites.json.defaults"), ["RecycleBinFixed", "FoldersFixed"]);
        File.WriteAllText(Path.Combine(Data, "settings.json"), JsonSerializer.Serialize(new
        {
            Theme = "Light",
            ShowTitleBarButton = false,
            FavoritesSection = true,
            DefaultStartInSplitMode = false,
            DefaultRightPaneMode = "Custom",
            CustomRightPanePath = Backup,
            AskBeforeCopyMove = true,
            EnforceGlobalDetailsAndNoGroup = true
        }, options));
    }

    /// <summary>Puts back any sample folder or file that is missing (the user may have tried things).</summary>
    public static void EnsureFolders()
    {
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Backup);
        foreach (string folder in Folders)
        {
            Directory.CreateDirectory(Path.Combine(Projects, folder));
        }

        foreach ((string name, string text) in Files)
        {
            string path = Path.Combine(Projects, name);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, text);
            }
        }
    }

    /// <summary>Empties the Backup folder (what the tour copied there).</summary>
    public static void ClearBackup()
    {
        if (!Directory.Exists(Backup))
        {
            Directory.CreateDirectory(Backup);
            return;
        }

        foreach (string file in Directory.EnumerateFiles(Backup))
        {
            File.Delete(file);
        }

        foreach (string dir in Directory.EnumerateDirectories(Backup))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Projects with Invoices (2025, 2026) and Designs nested in it, and a Media group with Photos.</summary>
    public static List<FavoriteNode> DefaultFavorites()
    {
        FavoriteNode invoices = FavoriteNode.Shortcut(InProjects("Invoices"));
        invoices.Children = [FavoriteNode.Shortcut(InProjects(@"Invoices\2025")), FavoriteNode.Shortcut(InProjects(@"Invoices\2026"))];
        FavoriteNode projects = FavoriteNode.Shortcut(Projects);
        projects.Children = [invoices, FavoriteNode.Shortcut(InProjects("Designs"))];
        FavoriteNode media = FavoriteNode.Group("Media");
        media.Children!.Add(FavoriteNode.Shortcut(InProjects("Photos")));
        return [projects, media];
    }

    public static string InProjects(string name) => Path.Combine(Projects, name);
}
