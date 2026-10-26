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
        if (sender is not FrameworkElement { DataContext: HistoryRow row })
            return;

        // Undoing a change that created a value deletes it again. That is what Undo means, but
        // deleting registry values is worth a confirmation when there is no previous value.
        if (AppServices.Backup.LatestPerKey(row.Entry.TweakId).Any(b => !b.Existed))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Undo and remove the value?",
                Content = "Part of this change created a value that did not exist before. Undoing removes it again. Continue?",
                PrimaryButtonText = "Undo",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        await VM.UndoAsync(row);
    }

    private async void OnRestoreGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BackupGroupRow group })
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Restore {group.TweakName}?",
            Content = "Every value in this group is written back from the backup. Values changed since then are overwritten.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await VM.ClearHistoryAsync();

        // The cleared timeline is empty: drop the filter so the empty state is not confused
        // with "no changes match your search".
        SearchBox.Text = "";
        VM.SearchText = "";
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
