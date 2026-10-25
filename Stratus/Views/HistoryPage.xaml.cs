using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Stratus.Core.Services;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class HistoryPage : Page
{
    private bool _loaded;

    public HistoryPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnLoaded;
    }

    public HistoryViewModel VM => Vm.History;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        await VM.RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await VM.RefreshAsync();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        VM.SearchText = ((TextBox)sender).Text;

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row })
            await VM.UndoAsync(row);
    }

    private async void OnRestoreGroup(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BackupGroupRow group })
            await VM.RestoreGroupAsync(group);
    }

    private async void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear change history?",
            Content = "The timeline is cleared. Backups are kept, so every applied tweak can still be reverted.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await VM.ClearHistoryAsync();
    }

    private void OnOpenBackupFile(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppPaths.BackupFile}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            VM.SetStatus("Could not open the backups file: " + ex.Message, InfoBarSeverity.Error);
        }
    }
}
