using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace ClipVault.App;

/// <summary>
/// The colour palette from the Rust app's <c>styles.css</c>, picked once at
/// startup to follow the Windows light/dark setting (read once, not tracked
/// live - a theme change needs a restart to pick up, unlike the web UI's
/// <c>prefers-color-scheme</c> media query).
/// </summary>
internal static class Theme
{
    public static void Apply(ResourceDictionary resources)
    {
        var colors = IsLightThemeActive() ? Light : Dark;
        foreach (var (key, value) in colors)
        {
            resources[key] = new SolidColorBrush(value);
        }
    }

    private static bool IsLightThemeActive()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = light, 0 (or missing) = dark.
            return key?.GetValue("AppsUseLightTheme") is 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly (string Key, Color Value)[] Dark =
    [
        ("BgColor", Rgb("#16181d")),
        ("PanelColor", Rgb("#1d2026")),
        ("PanelHiColor", Rgb("#272b33")),
        ("LineColor", Rgb("#313742")),
        ("FgColor", Rgb("#e6e9ef")),
        ("FgDimColor", Rgb("#949cab")),
        ("AccentColor", Rgb("#4f8ef7")),
        ("DangerColor", Rgb("#e5484d")),
        ("PinColor", Rgb("#f5a524")),
    ];

    private static readonly (string Key, Color Value)[] Light =
    [
        ("BgColor", Rgb("#f6f7f9")),
        ("PanelColor", Rgb("#ffffff")),
        ("PanelHiColor", Rgb("#eef1f6")),
        ("LineColor", Rgb("#dde1e8")),
        ("FgColor", Rgb("#1b1f27")),
        ("FgDimColor", Rgb("#6b7382")),
        ("AccentColor", Rgb("#4f8ef7")),
        ("DangerColor", Rgb("#e5484d")),
        ("PinColor", Rgb("#f5a524")),
    ];

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
