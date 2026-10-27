using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Services;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class ProfilePage : Page
{
    public ProfileViewModel VM => Vm.Profile;
    private bool _loadedOnce;

    public ProfilePage()
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
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            await VM.ExportAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the profile", ex);
            VM.ShowStatus("Could not save the profile: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.Window is null)
            {
                VM.ShowStatus("The main window is not ready yet.", InfoBarSeverity.Error);
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            VM.ProfilePath = file.Path;
            await VM.ImportAsync(file.Path);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the profile picker", ex);
            VM.ShowStatus("Could not open the profile picker: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            await VM.ImportAsync(VM.ProfilePath);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the profile", ex);
            VM.ShowStatus("Could not load the profile: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            await VM.ApplyAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the profile", ex);
            VM.ShowStatus("Could not apply the profile: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => VM.ClearPreview();
}
