using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Models;
using Stratus.ViewModels;

namespace Stratus.Views;

public sealed partial class TweaksPage : Page
{
    public TweaksViewModel VM => Vm.Tweaks;

    private bool _loadedOnce;

    public TweaksPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        CategoryBox.SelectedIndex = 0;
        RiskBox.SelectedIndex = 0;
        VM.ApplyFilter();
        Loaded += OnFirstLoad;
    }

    private async void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;
        await VM.RefreshStatesAsync();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        VM.SearchText = ((TextBox)sender).Text;

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox?.SelectedItem is not ComboBoxItem item)
            return;

        VM.CategoryFilter = item.Content switch
        {
            "Gaming" => TweakCategory.Gaming,
            "Visuals" => TweakCategory.Visuals,
            "Network" => TweakCategory.Network,
            "Services" => TweakCategory.Services,
            "Privacy" => TweakCategory.Privacy,
            "Power" => TweakCategory.Power,
            _ => null,
        };
    }

    private void OnRiskChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RiskBox?.SelectedItem is not ComboBoxItem item)
            return;

        VM.RiskFilter = item.Content switch
        {
            "Safe" => RiskLevel.Safe,
            "Recommended" => RiskLevel.Recommended,
            "Advanced" => RiskLevel.Advanced,
            _ => null,
        };
    }

    private async void OnToggleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not TweakItemViewModel item)
            return;
        if (item.IsBusy)
            return;

        var desired = toggle.IsOn;
        var actual = SafeReadApplied(item);

        // Programmatic refresh snap — desired already matches reality.
        if (desired == actual)
            return;

        if (desired && item.T.Risk == RiskLevel.Advanced && !await ConfirmAdvancedAsync(item))
        {
            toggle.IsOn = actual; // fires Toggled again; now desired == actual → no-op
            return;
        }

        item.IsBusy = true;
        try
        {
            var result = desired
                ? await AppServices.Engine.ApplyAsync(item.T)
                : await AppServices.Engine.RevertAsync(item.T);

            if (!result.Success)
            {
                VM.ShowStatus($"{item.Name}: {result.Error}", InfoBarSeverity.Error);
            }
            else if (desired && item.RequiresReboot)
            {
                VM.ShowStatus($"{item.Name} applied — restart Windows to take full effect.", InfoBarSeverity.Success);
            }

            var nowApplied = SafeReadApplied(item);
            item.SetState(item.NotApplicable, nowApplied);
            toggle.IsOn = nowApplied;
            VM.NotifyItemChanged();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static bool SafeReadApplied(TweakItemViewModel item)
    {
        try { return item.T.IsApplied; }
        catch { return false; }
    }

    private async Task<bool> ConfirmAdvancedAsync(TweakItemViewModel item)
    {
        var dialog = new ContentDialog
        {
            Title = $"Advanced tweak: {item.Name}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = "Advanced tweaks have real trade-offs — things you may rely on can be turned off. You can revert this at any time from the same switch.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 12,
                    },
                },
            },
            PrimaryButtonText = "Apply anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
