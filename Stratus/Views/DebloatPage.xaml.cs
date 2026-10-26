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

    private async void OnUninstallWin32(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Win32Row row })
            return;

        var message = await VM.UninstallWin32Async(row, silent: false);
        VM.ShowStatus(message, Failed(message) ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private async void OnSilentUninstall(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Win32Row row })
            return;

        var dialog = new ContentDialog
        {
            Title = $"Uninstall {row.DisplayName}?",
            Content = new TextBlock
            {
                Text = "Stratus runs the program's own uninstaller without prompts and waits for it to finish. Save your work in that app first.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.UninstallWin32Async(row, silent: true);
        VM.ShowStatus(message, Failed(message) ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private async void OnScanFeatures(object sender, RoutedEventArgs e) => await VM.ScanFeaturesAsync();

    private async void OnRemoveFeature(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeatureRow row })
            return;

        var dialog = new ContentDialog
        {
            Title = $"Remove {row.Name}?",
            Content = new TextBlock
            {
                Text = "Windows disables the feature and frees its files. A restart may be needed, and you can turn it back on from Windows Features.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.RemoveFeatureAsync(row);
        VM.ShowStatus(message, Failed(message) ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private async void OnRemoveProvisioned(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProvisionedRow row })
            return;

        var dialog = new ContentDialog
        {
            Title = $"Stop provisioning {row.DisplayName}?",
            Content = new TextBlock
            {
                Text = "New user accounts will no longer receive this package. Existing accounts are not touched.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var message = await VM.RemoveProvisionedAsync(row);
        VM.ShowStatus(message, Failed(message) ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private async void OnPreviewRecommended(object sender, RoutedEventArgs e)
    {
        var names = VM.Recommended.Select(r => r.Name).ToList();
        if (names.Count == 0)
        {
            VM.ShowStatus("No recommended apps are installed.", InfoBarSeverity.Informational);
            return;
        }

        var list = string.Join(Environment.NewLine, names.Select(n => "• " + n));
        var dialog = new ContentDialog
        {
            Title = $"Recommended set ({names.Count})",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = list + Environment.NewLine + Environment.NewLine +
                           "Selecting them only ticks the boxes. Nothing is removed until you press Remove selected.",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "Select them",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        VM.SetAllSelected(VM.Recommended, true);
        VM.ShowStatus($"{names.Count} recommended apps selected. Review the list, then remove them.", InfoBarSeverity.Informational);
    }

    private static bool Failed(string message) =>
        message.StartsWith("Could not", StringComparison.OrdinalIgnoreCase);

    private void OnSelectAllRecommended(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.Recommended, true);
    private void OnClearRecommended(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.Recommended, false);
    private void OnSelectAllOther(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.OtherMicrosoft, true);
    private void OnClearOther(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.OtherMicrosoft, false);
    private void OnSelectAllThird(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.ThirdParty, true);
    private void OnClearThird(object sender, RoutedEventArgs e) => VM.SetAllSelected(VM.ThirdParty, false);
}
