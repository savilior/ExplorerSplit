using Microsoft.Win32;

namespace Explorer2.Configuration;

internal static class ExplorerReplacementManager
{
    private const string FolderCommandKeyPath = @"Software\Classes\Folder\shell\open\command";
    private const string DirectoryCommandKeyPath = @"Software\Classes\Directory\shell\open\command";

    public static void SetEnabled(bool enabled)
    {
        if (AppPaths.IsDemo)
        {
            return; // demo mode never changes the installed copy's registry
        }

        if (enabled)
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            string command = $"\"{exePath}\" \"%1\"";

            SetShellOpenCommand(FolderCommandKeyPath, command);
            SetShellOpenCommand(DirectoryCommandKeyPath, command);
        }
        else
        {
            RemoveShellOpenCommand(FolderCommandKeyPath);
            RemoveShellOpenCommand(DirectoryCommandKeyPath);
        }
    }

    private static void SetShellOpenCommand(string keyPath, string command)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(keyPath);
            if (key is not null)
            {
                key.SetValue(string.Empty, command, RegistryValueKind.String);
                key.SetValue("DelegateExecute", string.Empty, RegistryValueKind.String);
            }
        }
        catch
        {
            // Ignore registry write failures
        }
    }

    private static void RemoveShellOpenCommand(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore registry delete failures
        }
    }
}
