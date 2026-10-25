using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Services;
using Stratus.Services;
using Stratus.Tray;
using Stratus.ViewModels;
using Stratus.Views;
using Windows.Graphics;

namespace Stratus;

public sealed partial class MainWindow : Window
{
    private TrayIconManager? _tray;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Stratus";
        SystemBackdrop = new MicaBackdrop();

        // Replace the native title bar with our own strip so Mica runs edge to edge.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Apply the persisted theme preference (system / dark / light) before first render.
        ThemeHelper.ApplyToWindow();

        // A comfortable default size for a 5-section dashboard app.
        AppWindow.Resize(new SizeInt32(1300, 860));

        var elevated = SystemInfoService.IsElevated();
        ElevationText.Text = elevated ? "Running as administrator" : "Standard user - writes will fail";
        ElevationGlyph.Glyph = elevated ? "\uE7EF" : "\uE7BA";
        if (!elevated)
            ElevationText.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF1, 0x70, 0x7B));

        // Tray presence: close hides to the tray, the tray menu offers a real Exit.
        _tray = new TrayIconManager(
            () => DispatcherQueue.TryEnqueue(() =>
            {
                AppWindow.Show();
                Activate();
            }),
            () => DispatcherQueue.TryEnqueue(ForceClose),
            message => Log.Info(message));
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => _tray?.Dispose();

        // Keep the tray tooltip in sync with the live score.
        Vm.Dashboard.PropertyChanged += OnDashboardPropertyChanged;

        // Select Dashboard on first layout.
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SelectedItem is null && Nav.MenuItems.Count > 0)
                Nav.SelectedItem = Nav.MenuItems[0];
        };
    }

    private void OnDashboardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Score))
            _tray?.UpdateTooltip(Vm.Dashboard.Score);
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceClose || !ThemeHelper.GetCloseToTray())
            return;

        args.Cancel = true;
        AppWindow.Hide();
        Log.Info("Window hidden to the tray (close to tray enabled).");
        _tray?.OnHiddenToTray();
    }

    /// <summary>Real exit: bypasses the hide-to-tray behavior from the tray menu.</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>Caption button colors must follow the effective theme (dark or light Mica).</summary>
    public void UpdateTitleBarColors(bool dark)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var foreground = dark
                ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            var foregroundDim = dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
                : Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x6B, 0x6B);
            var hover = dark
                ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x18, 0x00, 0x00, 0x00);
            var pressed = dark
                ? Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x28, 0x00, 0x00, 0x00);

            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = foregroundDim;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonPressedBackgroundColor = pressed;
        }
        catch
        {
            // Older builds without title bar customization: the system keeps its default buttons.
        }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
            return;

        var page = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "tweaks" => typeof(TweaksPage),
            "startup" => typeof(StartupPage),
            "tasks" => typeof(ScheduledTasksPage),
            "debloat" => typeof(DebloatPage),
            "cleanup" => typeof(CleanupPage),
            "history" => typeof(HistoryPage),
            "tools" => typeof(ToolsPage),
            _ => null,
        };

        if (page is not null && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }
}
