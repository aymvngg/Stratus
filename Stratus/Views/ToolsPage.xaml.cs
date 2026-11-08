using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Coordination;
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

        // The three cards below read their own state, so they load even when the main
        // plan/DNS load was skipped or failed.
        await VM.LoadAsync();
        await VM.LoadExtrasAsync();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedIndex < 0)
            return;
        ThemeHelper.SetMode(ThemeBox.SelectedIndex switch { 1 => "dark", 2 => "light", _ => "system" });
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e) =>
        ThemeHelper.SetCloseToTray(TrayToggle.IsOn);

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        // Independent of LoadAsync's result: the extra cards read their own state.
        await VM.LoadAsync();
        await VM.LoadExtrasAsync();
    }

    // ================= restore points, update pause, TCP =================

    private async void OnRefreshRestorePoints(object sender, RoutedEventArgs e) =>
        await VM.LoadRestorePointsAsync();

    private async void OnDeleteRestorePoint(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RestorePointRow row)
            return;

        var dialog = new ContentDialog
        {
            Title = "Delete this restore point?",
            Content = new TextBlock
            {
                Text = $"{row.Description}\n{row.MetaText}\n\nOnly this snapshot is removed. Newer restore points and your files stay as they are.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var outcome = await VM.DeleteRestorePointAsync(row);
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnToggleUpdatePause(object sender, RoutedEventArgs e)
    {
        var outcome = await VM.ToggleUpdatePauseAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnApplyTcp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TcpRow row)
            return;

        var outcome = await VM.ApplyTcpAsync(row);
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnResetTcp(object sender, RoutedEventArgs e)
    {
        var outcome = await VM.ResetTcpAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnApplyPlan(object sender, RoutedEventArgs e)
    {
        var outcome = await VM.ApplyPlanAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnApplyDns(object sender, RoutedEventArgs e)
    {
        var outcome = await VM.ApplyDnsAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        var outcome = await VM.CreateRestorePointAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
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

        var outcome = await VM.RunComponentCleanupAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
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
            Log.Warn("Could not open the data folder: " + ex.Message);
            VM.ShowStatus($"Could not open the folder: {ex.Message}", InfoBarSeverity.Error);
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

        var outcome = await VM.RevertAllAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    // ================= R3: health scans =================

    private async void OnSfc(object sender, RoutedEventArgs e) => await RunScan(VM.RunSfcAsync());

    private async void OnDismScan(object sender, RoutedEventArgs e) => await RunScan(VM.RunDismScanAsync());

    private async void OnDismRestore(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Repair the component store?",
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "DISM will download replacement components from Windows Update and repair the store SFC restores from. Use it when SFC reports corruption it cannot fix.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "This can take 30-60 minutes and needs a working internet connection. Keep the app open until it reports completion.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 12,
                    },
                },
            },
            PrimaryButtonText = "Start repair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await RunScan(VM.RunDismRestoreAsync());
    }

    private async Task RunScan(Task<OperationOutcome> scan)
    {
        var outcome = await scan;
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    // ================= R3: resets =================

    private async void OnResetWindowsUpdate(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset the Windows Update cache?",
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Stratus stops the update services, moves SoftwareDistribution and catroot2 aside, then restarts the services. Windows rebuilds both folders on the next update check.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "Nothing is deleted: the old folders stay on disk with a timestamp. Close Windows Update and any running installer first.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 12,
                    },
                },
            },
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var outcome = await VM.ResetWindowsUpdateAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnResetNetwork(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset the network stack?",
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Stratus runs winsock reset, TCP/IP reset, a DNS cache flush and DNS re-registration. Network adapters drop for a moment and a restart is needed to finish.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "Static IP settings and VPN adapters can be affected. Note them down first if you configured them by hand.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 12,
                    },
                },
            },
            PrimaryButtonText = "Reset network",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var outcome = await VM.ResetNetworkAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    // ================= R3: DNS benchmark and report =================

    private async void OnBenchmarkDns(object sender, RoutedEventArgs e)
    {
        var outcome = await VM.BenchmarkDnsAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnCreateReport(object sender, RoutedEventArgs e)
    {
        var outcome = await VM.CreateReportAsync();
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    // ================= v1.5: fixes and Windows tools =================

    private async void OnFixNtp(object sender, RoutedEventArgs e)
    {
        var (ok, message) = await VM.FixNtpAsync();
        VM.ShowStatus(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void OnRestoreNtp(object sender, RoutedEventArgs e)
    {
        var (ok, message) = await VM.RestoreNtpAsync();
        VM.ShowStatus(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void OnRepairWinget(object sender, RoutedEventArgs e)
    {
        var (ok, message) = await VM.RepairWingetAsync();
        VM.ShowStatus(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void OnOpenWindowsTool(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string target)
            return;

        var (ok, message) = VM.OpenWindowsTool(target);
        VM.ShowStatus(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    // ================= v1.4: updates =================

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (!VM.IsUpdateReady)
        {
            var outcome = await VM.CheckForUpdatesAsync();
            VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restart to finish the update?",
            Content = new TextBlock
            {
                Text = "Stratus closes, replaces its own files and starts the new version. Your tweaks and history are untouched.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Restart",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (VM.RestartToUpdate(out var status))
        {
            VM.ShowStatus(status, InfoBarSeverity.Success);
            App.Window?.ForceClose();
            return;
        }

        VM.ShowStatus(status, InfoBarSeverity.Error);
    }
}
