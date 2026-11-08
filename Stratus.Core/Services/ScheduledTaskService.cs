using System.Runtime.InteropServices;
using System.Text.Json;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

public sealed record ScheduledTaskItem(
    string Id,
    string Name,
    string Folder,
    string Group,
    string Description,
    bool IsEnabled,
    DateTime? LastRunUtc = null,
    DateTime? NextRunUtc = null,
    string? Author = null);

/// <summary>State read from the Task Scheduler for one task.</summary>
public sealed record TaskInfo(bool Enabled, DateTime? LastRunUtc, DateTime? NextRunUtc, string? Author);

internal enum TaskReadState
{
    /// <summary>The task exists and its state was read.</summary>
    Ok,
    /// <summary>The task does not exist in its folder. Genuinely absent, safe to skip.</summary>
    NotFound,
    /// <summary>The task exists but its state could not be read. Skipping hides a real task.</summary>
    Failed,
}

/// <summary>Result of reading one task from the Task Scheduler.</summary>
internal sealed record TaskReadResult(TaskReadState State, TaskInfo? Info)
{
    public static TaskReadResult NotFound { get; } = new(TaskReadState.NotFound, null);
    public static TaskReadResult Failed { get; } = new(TaskReadState.Failed, null);
}

/// <summary>Seam over the Task Scheduler connection, so reset and persistence rules are testable.</summary>
internal interface ITaskSession : IDisposable
{
    bool Available { get; }
    TaskReadResult Read(string taskPath);
    bool Set(string taskPath, bool enabled);
}

internal interface ITaskSessionFactory
{
    ITaskSession Open();
}

/// <summary>
/// Enables or disables well-known telemetry and inventory scheduled tasks.
/// Talks to the Task Scheduler COM API directly so it never parses localized
/// schtasks output. Original states are persisted before a toggle and kept until a reset succeeds.
/// </summary>
public sealed class ScheduledTaskService
{
    private sealed record Definition(string Folder, string Name, string Group, string Description);

    private static readonly Definition[] Known =
    [
        new(@"\Microsoft\Windows\Application Experience", "Microsoft Compatibility Appraiser", "Compatibility",
            "Scans installed programs and uploads compatibility data to Microsoft."),
        new(@"\Microsoft\Windows\Application Experience", "ProgramDataUpdater", "Compatibility",
            "Uploads the application inventory used for compatibility telemetry."),
        new(@"\Microsoft\Windows\Application Experience", "PcaPatchDbTask", "Compatibility",
            "Refreshes the program compatibility database in the background."),
        new(@"\Microsoft\Windows\Customer Experience Improvement Program", "Consolidator", "Telemetry",
            "Collects usage data for the Customer Experience Improvement Program."),
        new(@"\Microsoft\Windows\Customer Experience Improvement Program", "UsbCeip", "Telemetry",
            "Sends USB device usage data to Microsoft."),
        new(@"\Microsoft\Windows\Feedback\Siuf", "DmClient", "Feedback",
            "Prompts for feedback and uploads diagnostic data."),
        new(@"\Microsoft\Windows\Feedback\Siuf", "DmClientOnScenarioDownload", "Feedback",
            "Downloads feedback scenarios in the background."),
        new(@"\Microsoft\Windows\Windows Error Reporting", "QueueReporting", "Diagnostics",
            "Queues crash reports for upload. Local crash dumps are unaffected."),
        new(@"\Microsoft\Windows\Autochk", "Proxy", "Diagnostics",
            "Reports disk check results to Microsoft."),
        new(@"\Microsoft\Windows\DiskDiagnostic", "Microsoft-Windows-DiskDiagnosticDataCollector", "Diagnostics",
            "Collects disk diagnostic data for telemetry."),
        new(@"\Microsoft\Windows\PI", "Sqm-Tasks", "Telemetry",
            "Software Quality Metrics collection task."),
        new(@"\Microsoft\Windows\Device Information", "Device", "Inventory",
            "Sends the device inventory to Microsoft."),
        new(@"\Microsoft\Windows\Device Information", "Device User", "Inventory",
            "Sends per-user device inventory to Microsoft."),
        new(@"\Microsoft\Windows\CloudExperienceHost", "CreateObjectTask", "Cloud content",
            "Background task that provisions cloud content experiences."),
        new(@"\Microsoft\Windows\Speech", "SpeechModelDownloadTask", "Cloud content",
            "Downloads speech recognition models in the background."),
        new(@"\Microsoft\Windows\Maintenance", "WinSAT", "Benchmark",
            "Windows Experience Index benchmark, runs weekly when the machine is idle."),
    ];

    private readonly string _stateFile;
    private readonly ITaskSessionFactory _sessions;
    private readonly object _gate = new();
    private Dictionary<string, bool> _original = new(StringComparer.OrdinalIgnoreCase);
    private string? _unavailableReason;
    private readonly TimeSpan _lockTimeout;

    public ScheduledTaskService(string stateFile) : this(stateFile, new ComTaskSessionFactory()) { }

    internal ScheduledTaskService(string stateFile, ITaskSessionFactory sessions, TimeSpan? lockTimeout = null)
    {
        _stateFile = stateFile;
        _sessions = sessions;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(15);
        try
        {
            Reload();
        }
        catch (InvalidOperationException)
        {
            // Reload already recorded the reason; the service now refuses every change.
        }
    }

    /// <summary>Stable across processes: string hashing is randomized per process, a file digest is not.</summary>
    private string StateMutexName()
    {
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(_stateFile.ToLowerInvariant())))[..16];
        return "Global\\Stratus-ScheduledTasks-" + digest;
    }

    /// <summary>
    /// Takes the cross-process lock for one whole read-modify-write operation, so the app and
    /// the CLI cannot overwrite each other's saved originals from stale in-memory copies.
    /// Null means the lock could not be taken and nothing may be changed.
    /// </summary>
    private IDisposable? TryBeginStateOperation()
    {
        Mutex? mutex;
        var name = StateMutexName();
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not create the scheduled task state lock '{name}'.", ex);
            return null;
        }

        try
        {
            if (!mutex.WaitOne(_lockTimeout))
            {
                mutex.Dispose();
                Log.Warn("Another Stratus process held the scheduled task state lock for too long.");
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died holding the lock. We own it now and reload from disk next.
        }
        catch (Exception ex)
        {
            Log.Error("Could not take the scheduled task state lock", ex);
            mutex.Dispose();
            return null;
        }

        return new StateLease(mutex);
    }

    private sealed class StateLease : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _released;

        public StateLease(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            try { _mutex.ReleaseMutex(); } catch { /* not owned any more */ }
            _mutex.Dispose();
        }
    }

    /// <summary>
    /// True when the saved state could not be read. Every change is refused until the file is
    /// fixed, because a toggle whose original was lost could never be reset again.
    /// </summary>
    public bool IsAvailable => Volatile.Read(ref _unavailableReason) is null;

    /// <summary>Why changes are refused, or null when the service is usable.</summary>
    public string? UnavailableReason => Volatile.Read(ref _unavailableReason);

    /// <summary>
    /// Only tasks that actually exist on this machine are returned. When
    /// <paramref name="requireComplete"/> is set, the scan refuses to return a list it cannot
    /// fully read: the Task Scheduler being unavailable, the saved state being unreadable, or a
    /// known task existing but failing to report its state all throw instead of silently
    /// shrinking the list. Without the flag, unreadable entries are skipped so the UI stays usable.
    /// </summary>
    public List<ScheduledTaskItem> Scan(bool requireComplete = false)
    {
        if (requireComplete && !IsAvailable)
            throw new InvalidOperationException(UnavailableReason!);

        var list = new List<ScheduledTaskItem>();
        var failed = new List<string>();
        using var session = _sessions.Open();

        if (!session.Available)
        {
            if (!requireComplete)
                return list;

            throw new InvalidOperationException(
                "The Task Scheduler service is not available, so the task list could not be read.");
        }

        foreach (var definition in Known)
        {
            var path = Path(definition);
            var read = session.Read(path);
            if (read.State == TaskReadState.NotFound)
                continue;

            if (read.State != TaskReadState.Ok || read.Info is null)
            {
                failed.Add(path);
                continue;
            }

            list.Add(new ScheduledTaskItem(
                "task:" + path, definition.Name, definition.Folder,
                definition.Group, definition.Description, read.Info.Enabled,
                read.Info.LastRunUtc, read.Info.NextRunUtc, read.Info.Author));
        }

        if (requireComplete && failed.Count > 0)
            throw new InvalidOperationException(
                "These scheduled tasks exist but could not be read: " + string.Join(", ", failed) + ".");

        return list;
    }

    public TweakResult SetEnabled(ScheduledTaskItem item, bool enabled)
    {
        var path = item.Folder + "\\" + item.Name;

        if (!IsAvailable)
            return TweakResult.Fail(UnavailableReason!);

        using var lease = TryBeginStateOperation();
        if (lease is null)
            return TweakResult.Fail("Another Stratus process is changing scheduled tasks. Try again in a moment.");

        try
        {
            Reload();

            using var session = _sessions.Open();
            if (!session.Available)
                return TweakResult.Fail("The Task Scheduler service is not available.");

            // Read the full state: a failed read is reported as unknown, never as a missing task.
            var read = session.Read(path);
            if (read.State == TaskReadState.Failed)
                return TweakResult.Fail("The task state could not be read, so it was left unchanged.");

            if (read.State == TaskReadState.NotFound || read.Info is null)
                return TweakResult.Fail("That task no longer exists on this machine.");

            var current = read.Info.Enabled;
            if (current == enabled)
                return TweakResult.Ok;

            // Persist the original before the toggle. If that save fails nothing is changed:
            // a toggle whose original state was never written could not be reset after a restart.
            bool recorded;
            lock (_gate)
            {
                recorded = _original.ContainsKey(path);
                if (!recorded)
                {
                    _original[path] = current;
                    if (!TrySave())
                    {
                        // The in-memory entry is rolled back with the file: publishing a state
                        // the disk does not have is how a reset silently disappears.
                        _original.Remove(path);
                        return TweakResult.Fail(
                            "The original task state could not be saved, so the task was left unchanged.");
                    }
                }
            }

            if (!session.Set(path, enabled))
            {
                // The outcome is unknown: the task may or may not have been toggled. Keeping the
                // recorded original is the safe choice, since a reset simply re-sets the value
                // the task already had. It stays as a retry marker until a reset clears it.
                return TweakResult.Fail("The Task Scheduler service is not available.");
            }

            Log.Info((enabled ? "Enabled" : "Disabled") + " scheduled task: " + path);
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Warn("Scheduled task toggle failed for " + path + ": " + ex.Message);
            return TweakResult.Fail(ex.Message);
        }
    }

    public bool IsModified(ScheduledTaskItem item)
    {
        lock (_gate)
            return _original.ContainsKey(item.Folder + "\\" + item.Name);
    }

    public TweakResult Reset(ScheduledTaskItem item)
    {
        var path = item.Folder + "\\" + item.Name;
        bool enabled;

        if (!IsAvailable)
            return TweakResult.Fail(UnavailableReason!);

        using var lease = TryBeginStateOperation();
        if (lease is null)
            return TweakResult.Fail("Another Stratus process is changing scheduled tasks. Try again in a moment.");

        try
        {
            Reload();

            lock (_gate)
            {
                if (!_original.TryGetValue(path, out enabled))
                    return TweakResult.Fail("No change to reset for this task.");
            }

            using var session = _sessions.Open();
            if (!session.Set(path, enabled))
                return TweakResult.Fail("The Task Scheduler service is not available.");

            lock (_gate)
            {
                _original.Remove(path);
                if (!TrySave())
                {
                    // The task itself is back to its original state, but the saved original
                    // could not be dropped. Keep it so a later reset can retry and report it.
                    _original[path] = enabled;
                    return TweakResult.Fail(
                        "The task was restored, but the saved state could not be updated. Reset it again to clear it.");
                }
            }

            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Warn("Resetting scheduled task failed for " + path + ": " + ex.Message);
            return TweakResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Restores every recorded task. Returns the number of tasks restored. Refuses with a
    /// negative result when the saved state is unreadable or another Stratus process holds the
    /// state lock, so callers must treat any negative value as "nothing was changed".
    /// </summary>
    public int ResetAll() => ResetAllDetailed().Restored;

    public (int Restored, int Failed) ResetAllDetailed()
    {
        if (!IsAvailable)
        {
            Log.Warn("Scheduled task reset refused: " + UnavailableReason);
            return (-1, 0);
        }

        using var lease = TryBeginStateOperation();
        if (lease is null)
        {
            Log.Warn("Scheduled task reset refused: another Stratus process holds the state lock.");
            return (-1, 0);
        }

        try
        {
            Reload();

            KeyValuePair<string, bool>[] snapshot;
            lock (_gate)
                snapshot = _original.ToArray();

            var restored = 0;
            var failed = 0;
            using var session = _sessions.Open();

            foreach (var (path, enabled) in snapshot)
            {
                try
                {
                    if (!session.Set(path, enabled))
                    {
                        Log.Warn("Reset skipped: the Task Scheduler service is not available for " + path);
                        failed++;
                        continue;
                    }

                    lock (_gate)
                    {
                        _original.Remove(path);
                        if (!TrySave())
                        {
                            _original[path] = enabled;
                            failed++;
                            continue;
                        }
                    }

                    restored++;
                }
                catch (Exception ex)
                {
                    // Keep going: one locked task should not block the rest, and its backup stays.
                    Log.Warn("Resetting scheduled task failed for " + path + ": " + ex.Message);
                    failed++;
                }
            }

            return (restored, failed);
        }
        catch (Exception ex)
        {
            Log.Error("Scheduled task reset refused: the saved state could not be reloaded.", ex);
            return (-1, 0);
        }
    }

    private static string Path(Definition definition) => definition.Folder + "\\" + definition.Name;

    /// <summary>
    /// Re-reads the saved state from disk while holding the state lock. A corrupt file marks
    /// the service unavailable; the file itself is left in place so nothing is lost.
    /// </summary>
    private void Reload()
    {
        Dictionary<string, bool> stored;
        try
        {
            // Required, so a corrupt tasks.json is left on disk and reported instead of being
            // replaced by an empty dictionary on the next save, which would erase every original.
            stored = JsonFile.Load(
                _stateFile, "scheduled task state file",
                () => new Dictionary<string, bool>(), required: true);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _unavailableReason,
                "The saved task state could not be read, so changes are disabled until it is fixed. " + ex.Message);
            Log.Error("Scheduled task state is unavailable", ex);
            throw new InvalidOperationException(_unavailableReason, ex);
        }

        // A read that works clears an older refusal: the file may have been repaired by hand.
        Volatile.Write(ref _unavailableReason, null);

        lock (_gate)
        {
            _original = new Dictionary<string, bool>(stored, StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool TrySave()
    {
        try
        {
            JsonFile.Save(_stateFile, _original);
            return true;
        }
        catch (Exception ex)
        {
            // A damaged state file only costs us the reset, never a crash.
            Log.Warn("Could not save the scheduled task state file: " + ex.Message);
            return false;
        }
    }

    /// <summary>Thin wrapper over the Task Scheduler COM object (Schedule.Service).</summary>
    private sealed class ComTaskSessionFactory : ITaskSessionFactory
    {
        public ITaskSession Open() => ComTaskSession.Open();
    }

    private sealed class ComTaskSession : ITaskSession
    {
        private readonly dynamic? _service;

        private ComTaskSession(dynamic? service) => _service = service;

        public bool Available => _service is not null;

        public static ComTaskSession Open()
        {
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type is null)
                    return new ComTaskSession(null);

                dynamic service = Activator.CreateInstance(type)!;
                service.Connect();
                return new ComTaskSession(service);
            }
            catch
            {
                return new ComTaskSession(null);
            }
        }

        /// <summary>
        /// Reads the full task state. GetTask failing means the task is genuinely absent from
        /// its folder; a task that exists but will not report its state is a read failure.
        /// </summary>
        public TaskReadResult Read(string taskPath)
        {
            if (_service is null)
                return TaskReadResult.Failed;

            dynamic task;
            try
            {
                var (folder, name) = Split(taskPath);
                task = _service.GetFolder(folder).GetTask(name);
            }
            catch
            {
                return TaskReadResult.NotFound;
            }

            try
            {
                var enabled = (bool)task.Enabled;
                return new TaskReadResult(TaskReadState.Ok, new TaskInfo(
                    enabled,
                    TryTime(() => (DateTime)task.LastRunTime),
                    TryTime(() => (DateTime)task.NextRunTime),
                    TryAuthor(task)));
            }
            catch
            {
                return TaskReadResult.Failed;
            }
        }

        private static DateTime? TryTime(Func<DateTime> read)
        {
            try
            {
                var value = read();
                return value == DateTime.MinValue ? null : value.ToUniversalTime();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryAuthor(dynamic task)
        {
            try
            {
                var author = (string?)task.Definition.RegistrationInfo.Author;
                return string.IsNullOrWhiteSpace(author) ? null : author.Trim();
            }
            catch
            {
                return null;
            }
        }

        public bool Set(string taskPath, bool enabled)
        {
            if (_service is null)
                return false;

            var (folder, name) = Split(taskPath);
            dynamic task = _service.GetFolder(folder).GetTask(name);
            task.Enabled = enabled;
            return true;
        }

        private static (string Folder, string Name) Split(string taskPath)
        {
            var index = taskPath.LastIndexOf('\\');
            return index <= 0
                ? (@"\", taskPath.TrimStart('\\'))
                : (taskPath[..index], taskPath[(index + 1)..]);
        }

        public void Dispose()
        {
            if (_service is null)
                return;

            try
            {
                Marshal.FinalReleaseComObject((object)_service);
            }
            catch
            {
                // COM teardown is best effort.
            }
        }
    }
}
