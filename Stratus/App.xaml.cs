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
        // Bulk operations coalesce into a single refresh (see TweakEngine.BeginBulk).
        AppServices.Engine.StateChanged += () => _ = AppServices.RefreshFromEngineAsync();

        Window = new MainWindow();
        Window.Activate();
    }
}
