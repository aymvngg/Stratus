using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class ScheduledTasksPage : Page
{
    public ScheduledTasksViewModel VM => Vm.ScheduledTasks;

    private bool _loadedOnce;

    public ScheduledTasksPage()
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
        await VM.RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await VM.RefreshAsync();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        VM.SearchText = ((TextBox)sender).Text;

    private async void OnPresetDisable(object sender, RoutedEventArgs e) =>
        await ApplyPresetAsync(sender, enabled: false);

    private async void OnPresetEnable(object sender, RoutedEventArgs e) =>
        await ApplyPresetAsync(sender, enabled: true);

    private async Task ApplyPresetAsync(object sender, bool enabled)
    {
        if ((sender as FrameworkElement)?.Tag is not string group)
            return;

        VM.ShowStatus(await VM.ApplyGroupAsync(group, enabled), InfoBarSeverity.Success);
    }

    private async void OnEnableChecked(object sender, RoutedEventArgs e)
    {
        VM.ShowStatus(await VM.SetCheckedAsync(true), InfoBarSeverity.Success);
    }

    private async void OnDisableChecked(object sender, RoutedEventArgs e)
    {
        VM.ShowStatus(await VM.SetCheckedAsync(false), InfoBarSeverity.Success);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in VM.Filtered)
            row.IsChecked = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var row in VM.Filtered)
            row.IsChecked = false;
    }

    private async void OnResetAll(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset scheduled task changes?",
            Content = new TextBlock
            {
                Text = "Every task Stratus turned off goes back to its previous state. Tasks disabled by other tools are left alone.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.ResetAllAsync();
        VM.ShowStatus(message, InfoBarSeverity.Success);
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not ScheduledTaskRow row)
            return;

        var desired = toggle.IsOn;
        if (row.IsBusy || desired == row.IsEnabled)
            return;

        var ok = await VM.SetEnabledAsync(row, desired);
        if (!ok)
            toggle.IsOn = row.IsEnabled;
    }
}
