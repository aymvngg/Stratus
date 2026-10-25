using Microsoft.UI.Xaml.Media;
using Stratus.Core.Models;

namespace Stratus.Services;

/// <summary>
/// Per-category accent palette used across the UI: category bars on the Dashboard,
/// group dots and row stripes on the Tweaks page. Mid-tone colours read well on
/// both light and dark themes.
/// </summary>
public static class CategoryStyle
{
    public static SolidColorBrush For(TweakCategory category) => category switch
    {
        TweakCategory.Gaming => Gaming,
        TweakCategory.Visuals => Visuals,
        TweakCategory.Network => Network,
        TweakCategory.Services => Services,
        TweakCategory.Privacy => Privacy,
        _ => Power,
    };

    public static readonly SolidColorBrush Gaming = Make(0xFF4CC38A);
    public static readonly SolidColorBrush Visuals = Make(0xFFB8A1FF);
    public static readonly SolidColorBrush Network = Make(0xFF60CDFF);
    public static readonly SolidColorBrush Services = Make(0xFFFFC83D);
    public static readonly SolidColorBrush Privacy = Make(0xFFFF8FA3);
    public static readonly SolidColorBrush Power = Make(0xFFF58F5E);

    private static SolidColorBrush Make(uint argb) => new(Microsoft.UI.ColorHelper.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
