using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.ViewModels;
using Windows.Foundation;

namespace Stratus.Views;

public sealed partial class DashboardPage : Page
{
    // Ring geometry — must match the Ellipse in XAML (207px diameter, 13px stroke, 220px box).
    private const double RingSize = 220;
    private const double RingDiameter = 207;

    public DashboardViewModel VM => Vm.Dashboard;

    private bool _loadedOnce;

    public DashboardPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        VM.PropertyChanged += OnViewModelChanged;
        Loaded += OnFirstLoad;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Score))
            UpdateRing();
    }

    private async void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        UpdateRing();
        if (_loadedOnce)
            return;
        _loadedOnce = true;
        await VM.RescanAsync();
    }

    private void UpdateRing() => ScoreArc.Data = BuildArc(VM.Score);

    /// <summary>Builds the progress arc: starts at 12 o'clock, sweeps clockwise.</summary>
    private static Geometry BuildArc(double percent)
    {
        var center = RingSize / 2;
        var radius = RingDiameter / 2;

        if (percent <= 0.5)
            return new PathGeometry();

        if (percent >= 99.5)
        {
            return new EllipseGeometry
            {
                Center = new Point(center, center),
                RadiusX = radius,
                RadiusY = radius,
            };
        }

        var sweep = Math.Clamp(percent, 0, 100) / 100 * 360;
        var radians = (sweep - 90) * Math.PI / 180;
        var start = new Point(center, center - radius);
        var end = new Point(center + radius * Math.Cos(radians), center + radius * Math.Sin(radians));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await VM.RescanAsync();

    private async void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        var message = await VM.CreateRestorePointAsync();
        VM.ShowStatus(message, message.StartsWith("Restore point failed")
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Success);
    }

    private async void OnApplyRecommended(object sender, RoutedEventArgs e)
    {
        var pending = VM.PendingRecommendedCount();
        if (pending == 0)
        {
            VM.ShowStatus("Nothing to do — every recommended tweak is already applied.", InfoBarSeverity.Success);
            return;
        }

        var restorePointCheck = new CheckBox
        {
            Content = "Create a restore point first (recommended)",
            IsChecked = true,
        };

        var dialog = new ContentDialog
        {
            Title = $"Apply {pending} recommended tweak(s)?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Only Safe and Recommended tweaks are applied. Advanced tweaks are opt-in, one by one, from the Tweaks page.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    restorePointCheck,
                },
            },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.ApplyRecommendedAsync(restorePointCheck.IsChecked == true);
        VM.ShowStatus(message, message.Contains("failed")
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success);
    }
}
