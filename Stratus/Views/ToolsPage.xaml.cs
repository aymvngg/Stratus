using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.Services;
using Stratus.ViewModels;
using Stratus.Core.Services;

namespace Stratus.Views;

public sealed partial class ToolsPage : Page
{
    public ToolsViewModel VM => Vm.Tools;

    private bool _loadedOnce;

    public ToolsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnFirstLoad;
    }

    private async void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;

        // Reflect the persisted preferences (set after InitializeComponent so no mid-parse events fire).
        ThemeBox.SelectedIndex = ThemeHelper.GetMode() switch { "dark" => 1, "light" => 2, _ => 0 };
        TrayToggle.IsOn = ThemeHelper.GetCloseToTray();

        await VM.LoadAsync();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedIndex < 0)
            return;
        ThemeHelper.SetMode(ThemeBox.SelectedIndex switch { 1 => "dark", 2 => "light", _ => "system" });
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e) =>
        ThemeHelper.SetCloseToTray(TrayToggle.IsOn);

    private async void OnReload(object sender, RoutedEventArgs e) => await VM.LoadAsync();

    private async void OnApplyPlan(object sender, RoutedEventArgs e)
    {
        var message = await VM.ApplyPlanAsync();
        VM.ShowStatus(message, message.Contains("failed") ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private async void OnApplyDns(object sender, RoutedEventArgs e)
    {
        var message = await VM.ApplyDnsAsync();
        VM.ShowStatus(message, message.StartsWith("DNS set") ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        var message = await VM.CreateRestorePointAsync();
        VM.ShowStatus(message, message.StartsWith("Restore point failed") ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private async void OnComponentCleanup(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Run component store cleanup?",
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "DISM removes superseded versions of Windows components. This shrinks the component store and can free several GB.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "It runs in the background and can take 10-30 minutes. Keep the app open until it reports completion.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 12,
                    },
                },
            },
            PrimaryButtonText = "Run cleanup",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.RunComponentCleanupAsync();
        VM.ShowStatus(message, message.Contains("failed") ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.ProgramData,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            VM.ShowStatus($"Could not open the folder: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Init();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.LogDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            VM.ShowStatus($"Could not open the logs folder: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnRevertAll(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Revert ALL tweaks?",
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Every tweak that is currently detected as applied will be returned to Windows defaults. This includes optimizations you enabled manually or with other tools.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "Debloated apps and cleaned files are not restored by this action.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 12,
                    },
                },
            },
            PrimaryButtonText = "Revert everything",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.RevertAllAsync();
        VM.ShowStatus(message, message.Contains("failed") ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }
}
