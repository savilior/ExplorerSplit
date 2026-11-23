using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Explorer2.Configuration;

/// <summary>The theme chosen in the tray menu.</summary>
internal enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// Light / dark mode: the tray menu's Theme choice, where System follows Windows' "Choose your app
/// mode" like File Explorer. The environment variable EXPLORER2_THEME=dark|light overrides both
/// (for testing a theme without changing any setting).
/// </summary>
internal static class AppTheme
{
    private static readonly string? EnvOverride = Environment.GetEnvironmentVariable("EXPLORER2_THEME")?.Trim().ToLowerInvariant();

    /// <summary>The tray menu's choice; set at startup from the settings and when it changes.</summary>
    public static ThemeMode Mode { get; set; } = ThemeMode.System;

    private static ThemeMode EffectiveMode => EnvOverride switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => Mode
    };

    /// <summary>True when Explorer2 should draw in dark mode.</summary>
    public static bool IsDark
    {
        get
        {
            if (EffectiveMode != ThemeMode.System)
            {
                return EffectiveMode == ThemeMode.Dark;
            }

            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    // uxtheme's preferred app mode (undocumented, ordinal 135, Windows 10 1903+). File Explorer uses
    // it so its shell views, context menus, scroll bars and common controls draw dark.
    private enum PreferredAppMode
    {
        AllowDark = 1,
        ForceDark = 2,
        ForceLight = 3
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    private static extern int SetPreferredAppMode(PreferredAppMode mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = false)]
    private static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = false)]
    private static extern void RefreshImmersiveColorPolicyState();

    [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    private static bool _exportsMissing;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Marks the top-level window <paramref name="root"/> dark (dark frame, dark system and popup
    /// menus) and lets all its child windows draw dark, so menus they show are dark too. Shell views
    /// are recreated on navigation, so call this again after one.
    /// </summary>
    public static void AllowDarkForWindowTree(IntPtr root)
    {
        if (!IsDark || _exportsMissing || root == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int on = 1;
            DwmSetWindowAttribute(root, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            AllowDarkModeForWindow(root, true);
            EnumChildWindows(root, (hwnd, _) =>
            {
                AllowDarkModeForWindow(hwnd, true);
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            _exportsMissing = true;
        }
    }

    /// <summary>
    /// Lets the shell views and menus hosted in this process follow the theme. Call once at
    /// startup, before any window is created, and again when the Windows setting changes.
    /// </summary>
    public static void ApplyToProcess()
    {
        try
        {
            PreferredAppMode mode = EffectiveMode switch
            {
                ThemeMode.Dark => PreferredAppMode.ForceDark,
                ThemeMode.Light => PreferredAppMode.ForceLight,
                _ => PreferredAppMode.AllowDark
            };

            SetPreferredAppMode(mode);
            RefreshImmersiveColorPolicyState();
            FlushMenuThemes();
        }
        catch
        {
            // Older Windows without these exports: everything stays light
        }
    }
}
