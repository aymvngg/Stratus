using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class InstallPage : Page
{
    public InstallViewModel VM => Vm.Install;
    private bool _loadedOnce;

    public InstallPage()
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

    private async void OnUpgradeAll(object sender, RoutedEventArgs e) => await VM.UpgradeAllAsync();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            VM.SearchText = box.Text;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InstallRow row })
            return;

        await VM.InstallAsync(row);
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InstallRow row })
            return;

        await VM.UninstallAsync(row);
    }
}
