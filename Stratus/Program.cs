using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Stratus;

/// <summary>
/// Custom entry point. Required because Velopack needs its install/update hooks to run
/// before anything else: VelopackApp.Run() handles "app installed", "app updated" and
/// "app uninstalled" events, and it must be the first statement of Main, exactly once.
/// The WinUI XAML compiler's generated Main is disabled in the csproj for this reason.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));
            _ = new App();
        });
    }
}
