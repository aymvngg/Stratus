using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Stratus.Core.Services;
using Velopack;

namespace Stratus;

/// <summary>
/// Custom entry point. Required because Velopack needs its install/update hooks to run
/// before anything else: VelopackApp.Run() handles "app installed", "app updated" and
/// "app uninstalled" events, and it must be the first statement of Main, exactly once.
/// The WinUI XAML compiler's generated Main is disabled in the csproj for this reason.
///
/// Elevation happens here too, after the hooks: the exe is declared asInvoker so installers
/// and shortcuts can start it, then it relaunches itself with the runas verb. Demanding
/// elevation in app.manifest would break every non-elevated launcher (Velopack's Update.exe
/// and the Setup stub fail with ERROR_ELEVATION_REQUIRED, 740).
/// </summary>
public static class Program
{
    /// <summary>Marker passed to the elevated copy so a failed elevation cannot loop forever.</summary>
    private const string ElevatedFlag = "--elevated";

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        if (!EnsureElevated(args))
            return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));
            _ = new App();
        });
    }

    /// <summary>
    /// Makes sure the UI starts elevated. Returns false when the caller should stop: either
    /// because an elevated copy was started instead, or because the user declined the UAC prompt.
    /// </summary>
    private static bool EnsureElevated(string[] args)
    {
        if (SystemInfoService.IsElevated())
            return true;

        // The elevated relaunch is somehow still not elevated (policy, split token). Do not
        // spawn another process: run anyway and let the app report what it cannot do.
        if (args.Any(a => string.Equals(a, ElevatedFlag, StringComparison.OrdinalIgnoreCase)))
            return true;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return true;

        try
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = BuildElevatedArguments(args),
            };

            Process.Start(startInfo);
            return false;
        }
        catch (Win32Exception)
        {
            // ERROR_CANCELLED (1223): the user said no to the UAC prompt. Stratus cannot change
            // system settings without admin, so explain it instead of starting a broken window.
            NativeMessageBox(
                "Stratus needs administrator rights to change Windows settings.\n\n" +
                "Approve the User Account Control prompt to continue.",
                "Stratus");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Could not relaunch elevated", ex);
            return true;
        }
    }

    private static string BuildElevatedArguments(string[] args)
    {
        var builder = new StringBuilder();
        foreach (var arg in args)
        {
            if (string.Equals(arg, ElevatedFlag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(arg.Contains(' ') || arg.Contains('"')
                ? '"' + arg.Replace("\"", "\\\"") + '"'
                : arg);
        }

        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(ElevatedFlag);
        return builder.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void NativeMessageBox(string text, string caption) =>
        // MB_OK | MB_ICONWARNING | MB_SETFOREGROUND
        MessageBoxW(IntPtr.Zero, text, caption, 0x00000030 | 0x00010000);
}
