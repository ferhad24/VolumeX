using System.Windows;
using Microsoft.Win32;

namespace Crescendo.Services;

/// <summary>
/// Swaps the palette dictionary at runtime.
/// </summary>
/// <remarks>
/// Only the palette is exchanged; the control dictionary references every colour
/// through <c>DynamicResource</c>, so a theme change re-styles the whole window
/// without rebuilding a single template.
/// </remarks>
public static class ThemeService
{
    private const string DarkPalette = "Themes/Palette.Dark.xaml";
    private const string LightPalette = "Themes/Palette.Light.xaml";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppTheme _current = AppTheme.System;

    public static bool IsDarkActive { get; private set; } = true;

    public static void Apply(AppTheme theme)
    {
        _current = theme;
        bool dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsWindowsDark()
        };

        if (Application.Current is null) return;

        var uri = new Uri(dark ? DarkPalette : LightPalette, UriKind.Relative);
        var palette = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;

        // The palette is always first so the control dictionary can resolve
        // against it; replacing in place avoids a flash of unstyled content.
        if (merged.Count > 0)
            merged[0] = palette;
        else
            merged.Add(palette);

        IsDarkActive = dark;
        ThemeChanged?.Invoke(dark);
    }

    public static event Action<bool>? ThemeChanged;

    /// <summary>Re-evaluates the system theme; only acts when following Windows.</summary>
    public static void RefreshFromSystem()
    {
        if (_current == AppTheme.System) Apply(AppTheme.System);
    }

    private static bool IsWindowsDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return (key?.GetValue("AppsUseLightTheme") as int?) != 1;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
