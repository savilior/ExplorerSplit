using Microsoft.Win32;

namespace Explorer2.Configuration;

/// <summary>
/// File Explorer's own search settings (Folder Options > Search, and the Search options >
/// Advanced options menu). They are shared with File Explorer, so changing one here changes it there.
/// </summary>
internal static class SearchPreferences
{
    private const string PreferencesKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Search\Preferences";
    private const string UnindexedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Search\PrimaryProperties\UnindexedLocations";

    /// <summary>Search file contents in non-indexed locations ("File contents").</summary>
    public static bool FileContents
    {
        get => ReadDword(UnindexedKey, "SearchOnly", 1) == 0;
        set => WriteDword(UnindexedKey, "SearchOnly", value ? 0 : 1);
    }

    /// <summary>Include system directories ("System files").</summary>
    public static bool SystemFiles
    {
        get => ReadDword(PreferencesKey, "SystemFolders", 1) != 0;
        set => WriteDword(PreferencesKey, "SystemFolders", value ? 1 : 0);
    }

    /// <summary>Include compressed files ("Zipped (compressed) folders").</summary>
    public static bool ZippedFolders
    {
        get => ReadDword(PreferencesKey, "ArchivedFiles", 0) != 0;
        set => WriteDword(PreferencesKey, "ArchivedFiles", value ? 1 : 0);
    }

    /// <summary>"Use natural language search".</summary>
    public static bool NaturalQuerySyntax => ReadDword(PreferencesKey, "EnableNaturalQuerySyntax", 0) != 0;

    /// <summary>"Find partial matches" (type-ahead prefix matching).</summary>
    public static bool AutoWildcard => ReadDword(PreferencesKey, "AutoWildCard", 1) != 0;

    private static int ReadDword(string keyPath, string name, int fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteDword(string keyPath, string name, int value)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(keyPath);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch
        {
            // Ignore registry write failures
        }
    }
}
