using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Models;
using Stratus.ViewModels;
using Windows.Foundation;

namespace Stratus.Views;

public sealed partial class DashboardPage : Page
{
    // Ring geometry - must match the Ellipse in XAML (207px diameter, 13px stroke, 220px box).
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
        else if (e.PropertyName == nameof(DashboardViewModel.ScoreHistory))
            UpdateGraph();
    }

    private async void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        UpdateRing();
        UpdateGraph();
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

    // ---- Score over time ----

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGraph();

    /// <summary>Redraws the sparkline: last 80 snapshots, newest on the right, newest point in the health colour.</summary>
    private void UpdateGraph()
    {
        var history = VM.ScoreHistory;
        if (history.Count == 0 || GraphHost.ActualWidth < 20)
        {
            GraphLine.Data = null;
            GraphArea.Data = null;
            return;
        }

        const int MaxPoints = 80;
        var points = history.Skip(Math.Max(0, history.Count - MaxPoints)).ToArray();

        var width = GraphHost.ActualWidth;
        var height = GraphHost.ActualHeight;
        var pad = 7.0;
        var usableW = Math.Max(1.0, width - pad * 2);
        var usableH = Math.Max(1.0, height - pad * 2);

        double X(int i) => pad + (points.Length == 1 ? usableW : usableW * i / (points.Length - 1));
        double Y(int score) => pad + usableH * (1 - Math.Clamp(score, 0, 100) / 100.0);

        var start = new Point(X(0), Y(points[0].Score));
        var bottom = height - 2;

        var line = new PathFigure { StartPoint = start };
        var area = new PathFigure { StartPoint = new Point(X(0), bottom), IsClosed = true };
        area.Segments.Add(new LineSegment { Point = start });

        if (points.Length == 1)
        {
            // A single snapshot draws as a flat line so the graph is never a lone dot.
            line.Segments.Add(new LineSegment { Point = new Point(width - pad, start.Y) });
            area.Segments.Add(new LineSegment { Point = new Point(width - pad, start.Y) });
        }
        else
        {
            var lineRest = new PolyLineSegment();
            var areaRest = new PolyLineSegment();
            for (var i = 1; i < points.Length; i++)
            {
                var p = new Point(X(i), Y(points[i].Score));
                lineRest.Points.Add(p);
                areaRest.Points.Add(p);
            }
            line.Segments.Add(lineRest);
            area.Segments.Add(areaRest);
            area.Segments.Add(new LineSegment { Point = new Point(X(points.Length - 1), bottom) });
        }
        area.Segments.Add(new LineSegment { Point = new Point(width - pad, bottom) });

        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(line);
        GraphLine.Data = lineGeometry;

        var areaGeometry = new PathGeometry();
        areaGeometry.Figures.Add(area);
        GraphArea.Data = areaGeometry;

        GraphLine.Stroke = RiskStyles.HealthBrush(VM.Score);
        var c = RiskStyles.HealthBrush(VM.Score).Color;
        GraphArea.Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, c.R, c.G, c.B));
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
            VM.ShowStatus("Nothing to do - every recommended tweak is already applied.", InfoBarSeverity.Success);
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

    private async void OnRestartNow(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Restart now?",
            Content = new TextBlock
            {
                Text = "Windows will restart in 5 seconds. Close anything you are working on first: all applied tweaks stay applied, they just need one restart to take effect.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var result = await Task.Run(() => Stratus.Core.Services.ProcessRunner.Run("shutdown.exe", "/r /t 5", 15_000));
            if (!result.Success)
                VM.ShowStatus($"Restart failed: {result.StdErr.Trim()}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            VM.ShowStatus($"Restart failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnApplyPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Preset preset })
            await ApplyPresetFlowAsync(preset);
    }

    private async Task ApplyPresetFlowAsync(Preset preset)
    {
        if (VM.IsBusy)
        {
            VM.ShowStatus("Another operation is still running. Wait for it to finish first.", InfoBarSeverity.Informational);
            return;
        }

        var pending = VM.PresetPendingCount(preset);
        if (pending == 0)
        {
            VM.ShowStatus($"The {preset.Name} preset is already fully applied.", InfoBarSeverity.Success);
            return;
        }

        var restorePointCheck = new CheckBox
        {
            Content = "Create a restore point first (recommended)",
            IsChecked = true,
        };

        // Reviewable preset: list the tweaks that would actually change.
        var names = VM.PendingPresetNames(preset);
        var listPanel = new StackPanel { Spacing = 2 };
        foreach (var name in names.Take(8))
            listPanel.Children.Add(new TextBlock { Text = name, FontSize = 12, Opacity = 0.8 });
        if (names.Count > 8)
            listPanel.Children.Add(new TextBlock { Text = $"and {names.Count - 8} more", FontSize = 12, Opacity = 0.8, FontStyle = Windows.UI.Text.FontStyle.Italic });

        var dialog = new ContentDialog
        {
            Title = $"Apply the {preset.Name} preset?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Applies the {pending} remaining tweak{(pending == 1 ? "" : "s")} from the '{preset.Name}' set. Only Safe and Recommended tweaks are included, and each one can be reverted individually from the Tweaks page.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    listPanel,
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

        var message = await VM.ApplyPresetAsync(preset, restorePointCheck.IsChecked == true);
        VM.ShowStatus(message, message.Contains("failed")
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success);
    }
}
