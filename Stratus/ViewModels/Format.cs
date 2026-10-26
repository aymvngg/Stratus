using Microsoft.UI.Xaml;

namespace Stratus.ViewModels;

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        > 0 => $"{bytes} B",
        _ => "0 B",
    };

    public static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

/// <summary>Converters usable directly from x:Bind function bindings.</summary>
public static class Conv
{
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotVis(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>For IsEnabled bindings: true while the view model is not busy.</summary>
    public static bool Not(bool value) => !value;
}
