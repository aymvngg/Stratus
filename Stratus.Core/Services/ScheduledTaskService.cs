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
    bool IsEnabled);

/// <summary>
/// Enables or disables well-known telemetry and inventory scheduled tasks.
/// Talks to the Task Scheduler COM API directly so it never parses localized
/// schtasks output. Original states are kept in tasks.json for an exact reset.
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
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _original = new(StringComparer.OrdinalIgnoreCase);

    public ScheduledTaskService(string stateFile)
    {
        _stateFile = stateFile;
        Load();
    }

    /// <summary>Only tasks that actually exist on this machine are returned.</summary>
    public List<ScheduledTaskItem> Scan()
    {
        var list = new List<ScheduledTaskItem>();
        using var session = Session.Open();

        foreach (var definition in Known)
        {
            var path = Path(definition);
            var enabled = session.Enabled(path);
            if (enabled is null)
                continue;

            list.Add(new ScheduledTaskItem(
                "task:" + path, definition.Name, definition.Folder,
                definition.Group, definition.Description, enabled.Value));
        }

        return list;
    }

    public TweakResult SetEnabled(ScheduledTaskItem item, bool enabled)
    {
        var path = item.Folder + "\\" + item.Name;

        try
        {
            using var session = Session.Open();
            if (!session.Available)
                return TweakResult.Fail("The Task Scheduler service is not available.");

            var current = session.Enabled(path);
            if (current is null)
                return TweakResult.Fail("That task no longer exists on this machine.");

            lock (_gate)
            {
                if (!_original.ContainsKey(path))
                {
                    _original[path] = current.Value;
                    Save();
                }
            }

            session.Set(path, enabled);
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
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

        lock (_gate)
        {
            if (!_original.TryGetValue(path, out enabled))
                return TweakResult.Fail("No change to reset for this task.");
        }

        try
        {
            using var session = Session.Open();
            if (!session.Set(path, enabled))
                return TweakResult.Fail("The Task Scheduler service is not available.");

            lock (_gate)
            {
                _original.Remove(path);
                Save();
            }

            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }

    public int ResetAll()
    {
        KeyValuePair<string, bool>[] snapshot;
        lock (_gate)
            snapshot = _original.ToArray();

        var restored = 0;
        using var session = Session.Open();

        foreach (var (path, enabled) in snapshot)
        {
            try
            {
                if (!session.Set(path, enabled))
                    continue;

                restored++;
                lock (_gate)
                    _original.Remove(path);
            }
            catch
            {
                // Keep going: one locked task should not block the rest.
            }
        }

        lock (_gate)
            Save();

        return restored;
    }

    private static string Path(Definition definition) => definition.Folder + "\\" + definition.Name;

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFile))
                return;

            var json = File.ReadAllText(_stateFile);
            var stored = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (stored is null)
                return;

            lock (_gate)
            {
                foreach (var (key, value) in stored)
                    _original[key] = value;
            }
        }
        catch
        {
            // A damaged state file only costs us the reset, never a crash.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(_original, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Thin wrapper over the Task Scheduler COM object (Schedule.Service).</summary>
    private sealed class Session : IDisposable
    {
        private readonly dynamic? _service;

        private Session(dynamic? service) => _service = service;

        public bool Available => _service is not null;

        public static Session Open()
        {
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type is null)
                    return new Session(null);

                dynamic service = Activator.CreateInstance(type)!;
                service.Connect();
                return new Session(service);
            }
            catch
            {
                return new Session(null);
            }
        }

        public bool? Enabled(string taskPath)
        {
            if (_service is null)
                return null;

            try
            {
                var (folder, name) = Split(taskPath);
                dynamic task = _service.GetFolder(folder).GetTask(name);
                return (bool)task.Enabled;
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
