using System.Text.Json;
using System.Text.Json.Serialization;
using Explorer2.Interop;

namespace Explorer2.Configuration;

internal enum RightPanePathMode
{
    SameAsLeft,
    Home,
    Custom
}

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Explorer2");

    private static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    // Settings location used before the app was renamed from ExplorerSplit to Explorer2.
    private static string LegacySettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplorerSplit", "settings.json");

    private static void MigrateLegacySettingsIfNeeded()
    {
        try
        {
            if (!File.Exists(SettingsFilePath) && File.Exists(LegacySettingsFilePath))
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.Copy(LegacySettingsFilePath, SettingsFilePath);
            }
        }
        catch
        {
            // Fall back to defaults if the legacy file cannot be copied
        }
    }

    public bool ShowTitleBarButton { get; set; } = true;

    public bool ReplaceDefaultExplorer { get; set; } = false;

    public bool DefaultStartInSplitMode { get; set; } = false;

    public bool EnforceGlobalDetailsAndNoGroup { get; set; } = false;

    internal static bool GlobalDetailsAndNoGroupEnabled { get; set; }

    public uint HotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;

    public Keys HotkeyKey { get; set; } = Keys.S;

    public RightPanePathMode DefaultRightPaneMode { get; set; } = RightPanePathMode.SameAsLeft;

    public string CustomRightPanePath { get; set; } = string.Empty;

    /// <summary>System (follow Windows), Light or Dark.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// The navigation pane shows Explorer2's Favorites (with groups) instead of the Quick access
    /// pins. Read when a window opens, so a change applies to new windows.
    /// </summary>
    public bool FavoritesSection { get; set; } = true;

    internal static bool FavoritesSectionEnabled { get; set; } = FavoritesOverride ?? true;

    /// <summary>EXPLORER2_FAVORITES=0|1 overrides the setting (for testing, like EXPLORER2_THEME).</summary>
    private static bool? FavoritesOverride => Environment.GetEnvironmentVariable("EXPLORER2_FAVORITES") switch
    {
        "0" => false,
        "1" => true,
        _ => null
    };

    /// <summary>
    /// F5 Copy / F6 Move ask for the destination first (off: they go straight to the other pane's
    /// folder). Shared by every AppSettings object, so the dialog's "Don't ask" and the tray menu
    /// never overwrite each other.
    /// </summary>
    public bool AskBeforeCopyMove
    {
        get => _askBeforeCopyMove;
        set => _askBeforeCopyMove = value;
    }

    private static bool _askBeforeCopyMove = true;

    /// <summary>
    /// Order of the navigation pane's top rows (ids from <see cref="Core.TopSection"/>; null = the
    /// default order). Shared by every AppSettings object, like <see cref="AskBeforeCopyMove"/>.
    /// </summary>
    public List<string>? TopSectionOrder
    {
        get => StoredTopSectionOrder;
        set => StoredTopSectionOrder = value;
    }

    /// <summary>Top rows turned off in the tray menu's "Navigation Pane Top Section..." dialog.</summary>
    public List<string>? TopSectionHidden
    {
        get => StoredTopSectionHidden;
        set => StoredTopSectionHidden = value;
    }

    internal static List<string>? StoredTopSectionOrder { get; private set; }

    internal static List<string>? StoredTopSectionHidden { get; private set; }

    public static AppSettings Load()
    {
        MigrateLegacySettingsIfNeeded();

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (loaded is not null)
                {
                    if (loaded.HotkeyKey == Keys.None)
                    {
                        loaded.HotkeyKey = Keys.S;
                    }

                    if (loaded.HotkeyModifiers == 0)
                    {
                        loaded.HotkeyModifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;
                    }

                    GlobalDetailsAndNoGroupEnabled = loaded.EnforceGlobalDetailsAndNoGroup;
                    FavoritesSectionEnabled = FavoritesOverride ?? loaded.FavoritesSection;
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupt or unreadable config
        }

        AppSettings defaults = new();
        GlobalDetailsAndNoGroupEnabled = defaults.EnforceGlobalDetailsAndNoGroup;
        return defaults;
    }

    public void Save()
    {
        GlobalDetailsAndNoGroupEnabled = EnforceGlobalDetailsAndNoGroup;
        FavoritesSectionEnabled = FavoritesOverride ?? FavoritesSection;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore write failures
        }
    }

    public static void SetGlobalDetailsAndNoGroup(bool enabled)
    {
        AppSettings settings = Load();
        settings.EnforceGlobalDetailsAndNoGroup = enabled;
        settings.Save();
    }

    public string FormatHotkey() => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public static string FormatHotkey(uint modifiers, Keys key)
    {
        List<string> parts = new();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(FormatKeyName(key));
        return string.Join("+", parts);
    }

    private static string FormatKeyName(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9)
        {
            return ((char)('0' + (key - Keys.D0))).ToString();
        }

        return key.ToString();
    }
}
