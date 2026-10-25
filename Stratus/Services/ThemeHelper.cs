using Microsoft.UI.Xaml;
using Stratus.ViewModels;

namespace Stratus.Services;

/// <summary>
/// Theme and tray preferences, persisted through SettingsStore so they survive restarts.
/// Keys: "theme" = system|dark|light (default system), "closeToTray" = true|false (default true).
/// </summary>
public static class ThemeHelper
{
    public const string ModeKey = "theme";
    public const string TrayKey = "closeToTray";

    public static string GetMode() =>
        AppServices.Settings.Get(ModeKey) is { } mode && mode is "dark" or "light" ? mode : "system";

    public static bool GetCloseToTray() => AppServices.Settings.Get(TrayKey) is not { } value || value != "false";

    public static void SetMode(string mode)
    {
        AppServices.Settings.Set(ModeKey, mode);
        ApplyToWindow();
    }

    public static void SetCloseToTray(bool enabled) =>
        AppServices.Settings.Set(TrayKey, enabled ? "true" : "false");

    /// <summary>Called at startup and whenever the preference changes. Also reacts to system theme changes.</summary>
    public static void ApplyToWindow()
    {
        if (App.Window is not { } window || window.Content is not FrameworkElement root)
            return;

        root.RequestedTheme = GetMode() switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };

        // Keep caption button colors in sync, including live system theme switches.
        root.ActualThemeChanged -= OnRootActualThemeChanged;
        root.ActualThemeChanged += OnRootActualThemeChanged;
        window.UpdateTitleBarColors(root.ActualTheme == ElementTheme.Dark);
    }

    private static void OnRootActualThemeChanged(FrameworkElement sender, object args)
    {
        if (App.Window is { } window)
            window.UpdateTitleBarColors(sender.ActualTheme == ElementTheme.Dark);
    }
}
