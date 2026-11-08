using System.ServiceProcess;

namespace Stratus.Core.Services;

/// <summary>How a stop attempt ended. TimedOut still needs a restart owner: the service may finish stopping later.</summary>
internal enum UpdateServiceStop
{
    NotRunning,
    Stopped,
    TimedOut,
    Failed
}

/// <summary>Result of a reset: what was moved, what failed, and which services did not restart.</summary>
public sealed record UpdateResetResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> AttemptedStops,
    IReadOnlyList<string> TimedOutStops,
    IReadOnlyList<string> RestartFailures);

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

    public static (bool Ok, string Message) Reset() =>
        Reset(new WindowsUpdateResetControl());

    internal static (bool Ok, string Message) Reset(IUpdateResetControl control)
    {
        var result = ResetDetailed(control);
        return (result.Ok, result.Message);
    }

    /// <summary>Reset with the attempted stops and recovery failures exposed for callers that show them.</summary>
    public static UpdateResetResult ResetDetailed() => ResetDetailed(new WindowsUpdateResetControl());

    internal static UpdateResetResult ResetDetailed(IUpdateResetControl control)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var moved = new List<string>();
        var failures = new List<string>();
        var attempted = new List<string>();
        var timedOut = new List<string>();
        var restartFailures = new List<string>();
        var stops = new List<(string Name, UpdateServiceStop Outcome)>();

        foreach (var name in Services)
        {
            var outcome = control.TryStop(name);
            if (outcome == UpdateServiceStop.NotRunning)
                continue;

            // A requested stop recorded here keeps a restart owner even when the wait timed
            // out or the call threw: the stop can still take effect afterwards.
            attempted.Add(name);
            stops.Add((name, outcome));
            if (outcome == UpdateServiceStop.TimedOut)
                timedOut.Add(name);
        }

        string? error = null;
        try
        {
            // Give the services a moment to release their file handles.
            control.Sleep(TimeSpan.FromMilliseconds(1500));

            foreach (var folder in Folders)
            {
                if (!control.DirectoryExists(folder))
                    continue;

                var target = $"{folder}.old-{stamp}";
                if (control.TryMove(folder, target, attempts: 3))
                {
                    moved.Add(Path.GetFileName(folder));
                    control.CreateDirectory(folder);
                }
                else
                {
                    failures.Add(Path.GetFileName(folder));
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("Windows Update reset interrupted", ex);
        }
        finally
        {
            // Every service whose stop was requested keeps a restart owner: a timed out or
            // failed stop may still have taken effect after this method gave up.
            foreach (var (name, _) in stops)
            {
                if (!control.TryStart(name))
                    restartFailures.Add(name);
            }
        }

        Log.Info($"Windows Update reset: moved [{string.Join(", ", moved)}], failed [{string.Join(", ", failures)}],"
            + $" timed out [{string.Join(", ", timedOut)}], not restarted [{string.Join(", ", restartFailures)}]");

        if (restartFailures.Count > 0)
        {
            var message = failures.Count > 0
                ? $"Could not move {string.Join(" and ", failures)}. Close Windows Update and any installer, then try again."
                : "The update cache reset finished with a problem.";
            return new UpdateResetResult(false,
                $"{message} These services did not restart: {string.Join(", ", restartFailures)}. Start them from Services.",
                attempted, timedOut, restartFailures);
        }

        if (error is not null)
        {
            return new UpdateResetResult(false,
                $"The reset stopped before it finished: {error} Services were restarted.",
                attempted, timedOut, restartFailures);
        }

        if (failures.Count > 0)
        {
            return new UpdateResetResult(false,
                $"Could not move {string.Join(" and ", failures)}. Close Windows Update and any installer, then try again. Services were restarted.",
                attempted, timedOut, restartFailures);
        }

        if (moved.Count == 0)
        {
            return new UpdateResetResult(false,
                "Nothing to reset: the update cache folders were not found.",
                attempted, timedOut, restartFailures);
        }

        return new UpdateResetResult(true,
            $"Update cache reset: moved {string.Join(" and ", moved)} aside and restarted the update services. Windows will rebuild them on the next check.",
            attempted, timedOut, restartFailures);
    }

    /// <summary>Seam over the Windows service and file operations used by the reset.</summary>
    internal interface IUpdateResetControl
    {
        UpdateServiceStop TryStop(string name);
        bool TryStart(string name);
        void Sleep(TimeSpan duration);
        bool DirectoryExists(string path);
        bool TryMove(string source, string target, int attempts);
        void CreateDirectory(string path);
    }

    private sealed class WindowsUpdateResetControl : IUpdateResetControl
    {
        public UpdateServiceStop TryStop(string name)
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                    return UpdateServiceStop.NotRunning;

                service.Stop();
                try
                {
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    return UpdateServiceStop.Stopped;
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                    Log.Warn($"Service {name} did not stop within 20 seconds; it keeps a restart owner.");
                    return UpdateServiceStop.TimedOut;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stop service {name}: {ex.Message}");
                return UpdateServiceStop.Failed;
            }
        }

        public bool TryStart(string name)
        {
            try
            {
                using var service = new ServiceController(name);

                // A stop that timed out can still be in StopPending when the restart is due.
                // Waiting briefly lets the start succeed instead of reporting a false failure.
                if (service.Status == ServiceControllerStatus.StopPending)
                {
                    try
                    {
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                    catch (System.ServiceProcess.TimeoutException)
                    {
                        Log.Warn($"Service {name} is still stopping; it could not be restarted yet.");
                        return false;
                    }
                }

                if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                    return true;

                service.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not start service {name}: {ex.Message}");
                return false;
            }
        }

        public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool TryMove(string source, string target, int attempts)
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

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    }
}
