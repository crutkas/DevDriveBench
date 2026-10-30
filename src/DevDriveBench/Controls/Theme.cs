using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevDriveBench.Controls;

/// <summary>Resolves the stage palette (App.xaml theme dictionaries) for code-built visuals.</summary>
public static class Theme
{
    private static readonly Dictionary<string, Brush> Cache = new();

    public static Brush Brush(string key)
    {
        if (Cache.TryGetValue(key, out Brush? cached))
        {
            return cached;
        }

        ResourceDictionary resources = Application.Current.Resources;
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        string theme = highContrast ? "HighContrast" : "Dark";
        Brush brush =
            (resources.ThemeDictionaries.TryGetValue(theme, out object? dict) && dict is ResourceDictionary rd && rd.TryGetValue(key, out object? value) && value is Brush b) ? b
            : (resources.TryGetValue(key, out object? any) && any is Brush b2) ? b2
            : new SolidColorBrush(Colors.Magenta);
        Cache[key] = brush;
        return brush;
    }

    public static Brush Background => Brush("StageBackgroundBrush");

    public static Brush Hairline => Brush("StageHairlineBrush");

    public static Brush Primary => Brush("StageTextPrimaryBrush");

    public static Brush Secondary => Brush("StageTextSecondaryBrush");

    public static Brush Tertiary => Brush("StageTextTertiaryBrush");

    public static Brush SystemDrive => Brush("SystemDriveBrush");

    public static Brush SystemDriveLive => Brush("SystemDriveLiveBrush");

    public static Brush DevDrive => Brush("DevDriveBrush");

    public static Brush DevDriveLive => Brush("DevDriveLiveBrush");

    /// <summary>Code on the Dev Drive with packages still on the system drive.</summary>
    public static Brush DevDriveMixed => Brush("DevDriveMixedBrush");

    public static Brush Prepare => Brush("PrepareBrush");

    public static FontFamily NumberFont { get; } = new("Segoe UI Variable Display");
}
