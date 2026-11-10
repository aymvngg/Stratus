using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Services;
using Stratus.Services;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class SettingsPage : Page
{
    public Stratus.ViewModels.ProfileViewModel Profile => Vm.Profile;
    public Stratus.ViewModels.ToolsViewModel Tools => Vm.Tools;

    private bool _loadedOnce;

    public SettingsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnFirstLoad;
    }

    private void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;

        // Reflect the persisted preferences (set after InitializeComponent so no mid-parse events fire).
        ThemeBox.SelectedIndex = ThemeHelper.GetMode() switch { "dark" => 1, "light" => 2, _ => 0 };
        TrayToggle.IsOn = ThemeHelper.GetCloseToTray();
        Tools.LoadUpdateState();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
        => Profile.ShowStatus(message, severity);

    // Profile ------------------------------------------------------------

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var outcome = await Profile.ExportAsync();
            ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the profile", ex);
            ShowStatus("Could not save the profile: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.Window is null)
            {
                ShowStatus("The main window is not ready yet.", InfoBarSeverity.Error);
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            Profile.ProfilePath = file.Path;
            await ImportAsync(Profile.ProfilePath);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the profile picker", ex);
            ShowStatus("Could not open the profile picker: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            await ImportAsync(Profile.ProfilePath);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the profile", ex);
            ShowStatus("Could not load the profile: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ImportAsync(string path)
    {
        var outcome = await Profile.ImportAsync(path);
        ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            var outcome = await Profile.ApplyAsync();
            ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the profile", ex);
            ShowStatus("Could not apply the profile: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Profile.ClearPreview();

    // Preferences ----------------------------------------------------------

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedIndex < 0)
            return;

        ThemeHelper.SetMode(ThemeBox.SelectedIndex switch { 1 => "dark", 2 => "light", _ => "system" });
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
        => ThemeHelper.SetCloseToTray(TrayToggle.IsOn);

    // Data -----------------------------------------------------------------

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
            Log.Warn("Could not open the data folder: " + ex.Message);
            ShowStatus($"Could not open the folder: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.LogDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the logs folder: " + ex.Message);
            ShowStatus($"Could not open the logs folder: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // Danger zone ------------------------------------------------------------

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

        var outcome = await Tools.RevertAllAsync();
        ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    // Updates ------------------------------------------------------------------

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (!Tools.IsUpdateReady)
        {
            var outcome = await Tools.CheckForUpdatesAsync();
            ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restart to finish the update?",
            Content = new TextBlock
            {
                Text = "Stratus closes, replaces its own files and starts the new version. Your tweaks and history are untouched.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = "Restart",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (Tools.RestartToUpdate(out var status))
        {
            ShowStatus(status, InfoBarSeverity.Success);
            App.Window?.ForceClose();
            return;
        }

        ShowStatus(status, InfoBarSeverity.Error);
    }
}
