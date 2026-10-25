using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Services;
using Stratus.Views;
using Windows.Graphics;

namespace Stratus;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Stratus";
        SystemBackdrop = new MicaBackdrop();

        // A comfortable default size for a 5-section dashboard app.
        AppWindow.Resize(new SizeInt32(1300, 860));

        var elevated = SystemInfoService.IsElevated();
        ElevationText.Text = elevated ? "Running as administrator" : "Standard user — writes will fail";
        ElevationGlyph.Glyph = elevated ? "\uE7EF" : "\uE7BA";
        if (!elevated)
            ElevationText.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF1, 0x70, 0x7B));

        // Select Dashboard on first layout.
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SelectedItem is null && Nav.MenuItems.Count > 0)
                Nav.SelectedItem = Nav.MenuItems[0];
        };
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
            return;

        var page = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "tweaks" => typeof(TweaksPage),
            "debloat" => typeof(DebloatPage),
            "cleanup" => typeof(CleanupPage),
            "tools" => typeof(ToolsPage),
            _ => null,
        };

        if (page is not null && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }
}
