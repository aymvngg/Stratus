using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
        AppServices.Dispatcher = DispatcherQueue.GetForCurrentThread();

        // Any successful apply/revert (from any page or the engine) refreshes the live views.
        AppServices.Engine.StateChanged += () => AppServices.OnUi(() =>
        {
            Vm.Dashboard.RequestScoreRefresh();
            Vm.Tweaks.RequestStateRefresh();
        });

        Window = new MainWindow();
        Window.Activate();
    }
}
