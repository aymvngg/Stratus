using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Services;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class DebloatPage : Page
{
    public DebloatViewModel VM => Vm.Debloat;

    public DebloatPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnFirstLoad;
    }

    private bool _loadedOnce;

    private async void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;
        if (!VM.HasScanned)
            await VM.ScanAsync();
    }

    private async void OnScan(object sender, RoutedEventArgs e) => await VM.ScanAsync();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        VM.SearchText = ((TextBox)sender).Text;

    private async void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        var count = VM.TotalSelected;
        if (count == 0)
        {
            VM.ShowStatus("Select at least one app to remove.", InfoBarSeverity.Informational);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"Remove {count} app(s)?",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Store apps are removed for every user account and deprovisioned (new users won't get them back). Some can be reinstalled from the Microsoft Store.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.RemoveSelectedAsync();
        VM.ShowStatus(message, message.Contains("failed") ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private void OnUninstallWin32(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Win32AppItem app })
        {
            if (!AppServices.Debloat.LaunchUninstaller(app))
                VM.ShowStatus($"Could not start the uninstaller for {app.DisplayName}.", InfoBarSeverity.Error);
        }
    }

    private void OnSelectAllRecommended(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.Recommended, true);
    private void OnClearRecommended(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.Recommended, false);
    private void OnSelectAllOther(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.OtherMicrosoft, true);
    private void OnClearOther(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.OtherMicrosoft, false);
    private void OnSelectAllThird(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.ThirdParty, true);
    private void OnClearThird(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.ThirdParty, false);
}
