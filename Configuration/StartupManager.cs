using Microsoft.Win32;

namespace Explorer2.Configuration;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Explorer2";
    private const string LegacyValueName = "ExplorerSplit";

    /// <summary>
    /// If "Run at Windows Startup" was enabled under the pre-rename name (pointing at
    /// ExplorerSplit.exe), replace it with an entry for the current executable.
    /// </summary>
    public static void MigrateLegacyEntry()
    {
        if (AppPaths.IsDemo)
        {
            return;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(LegacyValueName) is null)
            {
                return;
            }

            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        catch
        {
            // Ignore registry failures; the user can re-enable startup from the tray menu
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        if (AppPaths.IsDemo)
        {
            return false; // demo mode never changes the installed copy's registry
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
