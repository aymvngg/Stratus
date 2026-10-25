using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class CleanupPage : Page
{
    public CleanupViewModel VM => Vm.Cleanup;

    private bool _includeRecycleBin;
    private bool _loadedOnce;

    public CleanupPage()
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
        if (!VM.HasScanned)
            await VM.ScanAsync();
    }

    private async void OnScan(object sender, RoutedEventArgs e) => await VM.ScanAsync();

    private void OnSelectAll(object sender, RoutedEventArgs e) => VM.SetAllChecked(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => VM.SetAllChecked(false);

    private void OnRecycleChanged(object sender, RoutedEventArgs e) =>
        _includeRecycleBin = (sender as CheckBox)?.IsChecked == true;

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var selected = VM.Rows.Count(r => r.IsChecked);
        if (selected == 0 && !_includeRecycleBin)
        {
            VM.ShowStatus("Select at least one location to clean.", InfoBarSeverity.Informational);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Clean now?",
            Content = new TextBlock
            {
                Text = _includeRecycleBin
                    ? $"The selected locations will be emptied and the Recycle Bin cleared. Files in use are skipped automatically."
                    : "The selected locations will be emptied. Files in use are skipped automatically.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Clean",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.CleanAsync(_includeRecycleBin);
        VM.ShowStatus(message, InfoBarSeverity.Success);
    }
}
