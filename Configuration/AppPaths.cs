namespace Explorer2.Configuration;

/// <summary>
/// Where Explorer2 keeps its data, and demo mode. Setting EXPLORER2_DATA_DIR to a folder runs a
/// separate Explorer2 (its own tray, settings, favorites and history in that folder) next to the
/// installed one without touching it. Demo mode never changes the registry (default File Explorer,
/// run at startup) and leaves the taskbar jump list alone. Used by tools\record-guide.ps1.
/// </summary>
internal static class AppPaths
{
    private static readonly string? DemoDataDirectory =
        Environment.GetEnvironmentVariable("EXPLORER2_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    public static bool IsDemo => DemoDataDirectory is not null;

    public static string DataDirectory => DemoDataDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Explorer2");

    /// <summary>Added to the names that tie Explorer2's processes together (mutex, pipe).</summary>
    public static string InstanceSuffix => IsDemo ? "_Demo" : string.Empty;
}
