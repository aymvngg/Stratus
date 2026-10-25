using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Stratus.Core.Services;
using Stratus.ViewModels;

namespace Stratus;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Init();
        Log.Info("Stratus started (elevated: " + SystemInfoService.IsElevated() + ").");

        AppServices.Dispatcher = DispatcherQueue.GetForCurrentThread();

        // Any successful apply/revert (from any page or the engine) refreshes the live views.
        AppServices.Engine.StateChanged += () => AppServices.OnUi(() =>
        {
            Vm.Dashboard.RequestScoreRefresh();
            Vm.Tweaks.RequestStateRefresh();
            Vm.History.RequestRefresh();
        });

        Window = new MainWindow();
        Window.Activate();
    }
}
