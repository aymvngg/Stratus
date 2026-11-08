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

    private async void OnRefreshStates(object sender, RoutedEventArgs e) => await VM.RefreshStatesAsync();

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

    private async void OnApplyCategory(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TweakGroup group } || !group.CanApplySafe)
            return;

        // Same rule the run uses: unknown rows are never applied, so the count the dialog
        // shows is the count ApplyCategoryAsync will attempt.
        var pending = group.PendingSafeIds().Count;
        if (pending == 0)
            return;

        var restorePointCheck = new CheckBox
        {
            Content = "Create a restore point first (recommended)",
            IsChecked = true,
        };

        var dialog = new ContentDialog
        {
            Title = $"Apply {pending} safe tweak{(pending == 1 ? "" : "s")} in {group.Title}?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Only Safe and Recommended tweaks in this category are applied. Advanced ones stay opt-in, one by one.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    restorePointCheck,
                },
            },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var outcome = await VM.ApplyCategoryAsync(group, restorePointCheck.IsChecked == true);
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    private async void OnToggleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not TweakItemViewModel item)
            return;

        // The switch mirrors IsApplied. A refresh writes IsApplied and the binding moves the
        // switch, which raises Toggled: that value already matches the render and is not intent.
        // A row whose state could not be read is never a user intent either.
        if (item.IsBusy || item.IsUnknown || VM.IsBulkApplying)
        {
            RenderToggle(toggle, item);
            return;
        }

        var desired = toggle.IsOn;
        if (desired == item.IsApplied)
            return;

        if (desired && item.T.Risk == RiskLevel.Advanced && !await ConfirmAdvancedAsync(item))
        {
            RenderToggle(toggle, item);
            return;
        }

        // The state may have moved while the dialog was open. Apply only real intent.
        if (item.IsBusy || item.IsUnknown || desired == item.IsApplied)
        {
            RenderToggle(toggle, item);
            return;
        }

        var outcome = await VM.SetAppliedAsync(item, desired);
        RenderToggle(toggle, item);
        VM.ShowStatus(outcome.Message, outcome.Severity.ToInfoBarSeverity());
    }

    /// <summary>Writes the rendered state back. The toggle event this raises matches the
    /// render, so the handler treats it as a no-op rather than new intent.</summary>
    private static void RenderToggle(ToggleSwitch toggle, TweakItemViewModel item)
    {
        if (toggle.IsOn != item.IsApplied)
            toggle.IsOn = item.IsApplied;
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
                        Text = "Advanced tweaks have real trade-offs - things you may rely on can be turned off. You can revert this at any time from the same switch.",
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
