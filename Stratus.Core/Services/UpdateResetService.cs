using System.ServiceProcess;

namespace Stratus.Core.Services;

/// <summary>
/// Resets the Windows Update pipeline: stops the update services, moves the download
/// cache and the cryptography catalogue aside, then starts the services again.
/// The renamed folders are kept (with a timestamp) so nothing is destroyed.
/// </summary>
public static class UpdateResetService
{
    private static readonly string[] Services = ["wuauserv", "bits", "cryptsvc", "msiserver"];

    private static readonly string[] Folders =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "catroot2"),
    ];

    public static (bool Ok, string Message) Reset()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var moved = new List<string>();
        var failures = new List<string>();

        var stopped = new List<string>();
        foreach (var name in Services)
        {
            if (TryStop(name))
                stopped.Add(name);
        }

        // Give the services a moment to release their file handles.
        Thread.Sleep(1500);

        foreach (var folder in Folders)
        {
            if (!Directory.Exists(folder))
                continue;

            var target = $"{folder}.old-{stamp}";
            if (TryMove(folder, target, attempts: 3))
            {
                moved.Add(Path.GetFileName(folder));
                Directory.CreateDirectory(folder);
            }
            else
            {
                failures.Add(Path.GetFileName(folder));
            }
        }

        foreach (var name in stopped)
            TryStart(name);

        Log.Info($"Windows Update reset: moved [{string.Join(", ", moved)}], failed [{string.Join(", ", failures)}]");

        if (failures.Count > 0)
        {
            return (false,
                $"Could not move {string.Join(" and ", failures)}. Close Windows Update and any installer, then try again. Services were restarted.");
        }

        if (moved.Count == 0)
            return (false, "Nothing to reset: the update cache folders were not found.");

        return (true,
            $"Update cache reset: moved {string.Join(" and ", moved)} aside and restarted the update services. Windows will rebuild them on the next check.");
    }

    private static bool TryStop(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                return false;

            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not stop service {name}: {ex.Message}");
            return false;
        }
    }

    private static void TryStart(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            if (service.Status == ServiceControllerStatus.Stopped)
                service.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start service {name}: {ex.Message}");
        }
    }

    private static bool TryMove(string source, string target, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                Directory.Move(source, target);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Move {source} failed (attempt {i + 1}): {ex.Message}");
                Thread.Sleep(1200);
            }
        }

        return false;
    }
}
