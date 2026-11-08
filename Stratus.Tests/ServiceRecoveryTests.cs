using System.Globalization;
using System.ServiceProcess;
using Microsoft.Win32;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// In-memory registry that mirrors the real attempt semantics: the first original per key is
/// kept for a revert, while an attempt records its own pre-write state for rollback.
/// </summary>
internal sealed class FakeRegistryGateway : IRegistryGateway
{
    private static string Id(RegistryHive hive, string path, string name) => $"{hive}|{path}|{name}";

    public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (bool Existed, object? Value, string TweakId)> Originals { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool FailRestore { get; set; }
    public int Writes { get; private set; }
    public int RestoreCalls { get; private set; }
    public Func<int, bool>? FailOnWrite { get; set; }

    /// <summary>Makes strict reads throw while plain reads still succeed, as a masked failure would.</summary>
    public bool FailStrictRead { get; set; }

    public RegistryRead Read(RegistryHive hive, string path, string name)
    {
        var key = Id(hive, path, name);
        return Values.TryGetValue(key, out var value)
            ? new RegistryRead(true, RegistryValueKind.String, value)
            : new RegistryRead(false, RegistryValueKind.None, null);
    }

    public RegistryRead ReadRequired(RegistryHive hive, string path, string name)
    {
        if (FailStrictRead)
            throw new RegistryReadException(
                $"The registry value {hive}\\{path}\\{name} could not be read, so it was not changed.");

        return Read(hive, path, name);
    }

    public IRegistryAttempt BeginAttempt(string tweakId) => new Attempt(this, tweakId);

    public bool HasBackups(string tweakId) =>
        Originals.Values.Any(original => original.TweakId == tweakId);

    public bool RestoreAll(string tweakId)
    {
        RestoreCalls++;
        if (FailRestore)
            return false;

        foreach (var (key, original) in Originals.Where(o => o.Value.TweakId == tweakId).ToList())
        {
            if (original.Existed)
                Values[key] = original.Value;
            else
                Values.Remove(key);

            Originals.Remove(key);
        }

        return true;
    }

    private void WriteAttempt(RegistryHive hive, string path, string name,
        RegistryValueKind kind, object? value, string tweakId)
    {
        Writes++;
        if (FailOnWrite?.Invoke(Writes) == true)
        {
            // Record the original first, as the real attempt does, then fail: this is what
            // leaves an attempt half written and is exactly what Rollback has to undo.
            var failKey = Id(hive, path, name);
            if (!Originals.ContainsKey(failKey))
            {
                var failExisted = Values.TryGetValue(failKey, out var failCurrent);
                Originals[failKey] = (failExisted, failCurrent, tweakId);
            }

            throw new InvalidOperationException("Injected registry write failure.");
        }

        var key = Id(hive, path, name);
        if (!Originals.ContainsKey(key))
        {
            var existed = Values.TryGetValue(key, out var current);
            Originals[key] = (existed, current, tweakId);
        }

        Values[key] = value;
    }

    private sealed class Attempt : IRegistryAttempt
    {
        private readonly FakeRegistryGateway _owner;
        private readonly string _tweakId;
        private readonly List<(string Key, bool Existed, object? Value)> _before = [];
        private readonly List<string> _added = [];

        public Attempt(FakeRegistryGateway owner, string tweakId)
        {
            _owner = owner;
            _tweakId = tweakId;
        }

        public void Write(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value)
        {
            var key = Id(hive, path, name);
            _before.Add((key,
                _owner.Values.TryGetValue(key, out var current),
                current));

            if (!_owner.Originals.ContainsKey(key))
                _added.Add(key);

            _owner.WriteAttempt(hive, path, name, kind, value, _tweakId);
        }

        public bool Rollback()
        {
            if (_owner.FailRestore)
                return false;

            for (var i = _before.Count - 1; i >= 0; i--)
            {
                var (key, existed, value) = _before[i];
                if (existed)
                    _owner.Values[key] = value;
                else
                    _owner.Values.Remove(key);
            }

            // Same rule as the real attempt: an original first saved by this attempt is dropped
            // once the rollback worked, so a later revert does not revive the failed attempt.
            foreach (var key in _added)
                _owner.Originals.Remove(key);

            _before.Clear();
            _added.Clear();
            return true;
        }

        public void Commit()
        {
            _before.Clear();
            _added.Clear();
        }
    }
}

internal sealed class FakeUpdateResetControl : UpdateResetService.IUpdateResetControl
{
    public Dictionary<string, UpdateServiceStop> Stops { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StartFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Started { get; } = [];
    public List<string> Moved { get; } = [];
    public HashSet<string> ExistingFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnmovableFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? MoveThrows { get; set; }

    public UpdateServiceStop TryStop(string name) =>
        Stops.TryGetValue(name, out var outcome) ? outcome : UpdateServiceStop.NotRunning;

    public bool TryStart(string name)
    {
        Started.Add(name);
        return !StartFailures.Contains(name);
    }

    public void Sleep(TimeSpan duration) { }

    public bool DirectoryExists(string path) => ExistingFolders.Contains(path);

    public bool TryMove(string source, string target, int attempts)
    {
        if (MoveThrows is not null)
            throw MoveThrows;
        if (UnmovableFolders.Contains(source))
            return false;

        Moved.Add(source);
        return true;
    }

    public void CreateDirectory(string path) { }
}

internal sealed class FakeCleanupServiceControl : ICleanupServiceControl
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StartFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StopFails { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Started { get; } = [];

    public bool IsRunning(string name) => Running.Contains(name);

    public bool Stop(string name)
    {
        Running.Remove(name);
        return !StopFails.Contains(name);
    }

    public bool Start(string name)
    {
        Started.Add(name);
        return !StartFailures.Contains(name);
    }
}

/// <summary>Service control whose stop throws, as a disappearing service would.</summary>
internal sealed class ThrowingCleanupControl : ICleanupServiceControl
{
    public string? ThrowOnStop { get; set; }
    public List<string> Started { get; } = [];

    public bool IsRunning(string name) => true;

    public bool Stop(string name)
    {
        if (string.Equals(name, ThrowOnStop, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("injected stop failure");

        return true;
    }

    public bool Start(string name)
    {
        Started.Add(name);
        return true;
    }
}

internal sealed class FakeUpdatePauseGate : UpdatePauseService.IUpdatePauseGate
{
    public bool FailGate { get; set; }
    public int Transactions { get; private set; }
    public bool Open { get; private set; }

    public IDisposable? TryBegin()
    {
        Transactions++;
        if (FailGate)
            return null;

        Open = true;
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private readonly FakeUpdatePauseGate _owner;

        public Scope(FakeUpdatePauseGate owner) => _owner = owner;

        public void Dispose() => _owner.Open = false;
    }
}

internal sealed class FakeRestorePointScanner : RestorePointService.IRestorePointScanner
{
    public ProcessResult? Result { get; set; }
    public Exception? Throws { get; set; }

    public ProcessResult? Run(string script) => Throws is null ? Result : throw Throws;
}

internal sealed class FakeRestorePointCheckpoint : IRestorePointCheckpoint
{
    public RestorePointCheckpoint Result { get; set; } = new(true, "Restore point created.", "");
    public Exception? Throws { get; set; }
    public int Calls { get; private set; }

    public RestorePointCheckpoint Create()
    {
        Calls++;
        if (Throws is not null)
            throw Throws;
        return Result;
    }
}

internal sealed class FakeTaskSessionFactory : ITaskSessionFactory
{
    public Dictionary<string, bool> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SetCalls { get; } = [];
    public string? StateAtSet { get; private set; }
    public bool FailSet { get; set; }
    public bool FailRead { get; set; }
    public bool Unavailable { get; set; }

    public ITaskSession Open() => new Session(this);

    private sealed class Session : ITaskSession
    {
        private readonly FakeTaskSessionFactory _owner;

        public Session(FakeTaskSessionFactory owner) => _owner = owner;

        public bool Available => !_owner.Unavailable;

        public TaskReadResult Read(string taskPath)
        {
            if (_owner.FailRead)
                return TaskReadResult.Failed;
            if (!_owner.Enabled.ContainsKey(taskPath))
                return TaskReadResult.NotFound;

            return new TaskReadResult(TaskReadState.Ok,
                new TaskInfo(_owner.Enabled[taskPath], null, null, "test"));
        }

        public bool Set(string taskPath, bool enabled)
        {
            _owner.SetCalls.Add(taskPath);
            _owner.StateAtSet ??= ReadStateFile(_owner);
            if (_owner.FailSet)
                return false;

            _owner.Enabled[taskPath] = enabled;
            return true;
        }

        private static string? ReadStateFile(FakeTaskSessionFactory owner)
        {
            try { return owner.StateFile is { } file && File.Exists(file) ? File.ReadAllText(file) : null; }
            catch { return null; }
        }

        public void Dispose() { }
    }

    public string? StateFile { get; init; }
}

internal sealed class FakeDnsCommands : IDnsCommandRunner
{
    public List<string> Calls { get; } = [];
    public List<ProcessResult> Results { get; } = [];
    public Func<string, ProcessResult?>? Override { get; set; }
    public Action<string>? OnRun { get; set; }

    /// <summary>Arguments fragment that makes the next matching Run throw, as a start failure would.</summary>
    public string? ThrowOn { get; set; }
    public Exception ThrowException { get; set; } = new InvalidOperationException("injected start failure");

    public ProcessResult? Run(string fileName, string arguments)
    {
        var line = fileName + " " + arguments;
        Calls.Add(line);
        OnRun?.Invoke(line);

        if (ThrowOn is not null && line.Contains(ThrowOn, StringComparison.Ordinal))
        {
            ThrowOn = null;
            throw ThrowException;
        }

        if (Override?.Invoke(line) is { } custom)
            return custom;
        if (Results.Count > 0)
        {
            var result = Results[0];
            Results.RemoveAt(0);
            return result;
        }
        return new ProcessResult(0, "", "");
    }
}

internal sealed class FakeDnsAdapters : IDnsAdapterSource
{
    public List<DnsAdapterOriginal> Adapters { get; } = [];
    public Exception? Throw { get; set; }

    public List<DnsAdapterOriginal> GetConnectedAdapters() =>
        Throw is null ? Adapters : throw Throw;
}

internal sealed class InMemoryDnsOriginals : IDnsOriginalsStore
{
    public Dictionary<string, DnsAdapterOriginal> Data { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailSave { get; set; }
    public bool FailLoad { get; set; }
    public bool FailTransaction { get; set; }
    public int Transactions { get; private set; }
    public bool TransactionOpen { get; private set; }

    public Dictionary<string, DnsAdapterOriginal> Load()
    {
        if (FailLoad)
            throw new InvalidOperationException("injected unreadable DNS originals.");
        return new(Data, StringComparer.OrdinalIgnoreCase);
    }

    public void Save(IReadOnlyDictionary<string, DnsAdapterOriginal> originals)
    {
        if (FailSave)
            throw new IOException("Injected DNS originals save failure.");

        Data.Clear();
        foreach (var (key, value) in originals)
            Data[key] = value;
    }

    public IDisposable? TryBeginTransaction()
    {
        Transactions++;
        if (FailTransaction)
            return null;

        TransactionOpen = true;
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private readonly InMemoryDnsOriginals _owner;

        public Scope(InMemoryDnsOriginals owner) => _owner = owner;

        public void Dispose() => _owner.TransactionOpen = false;
    }
}

public sealed class ServiceRecoveryTests : IDisposable
{
    private const string TaskPath =
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser";

    private readonly TempTree _tree = new();

    private ScheduledTaskItem Task() =>
        new("task:" + TaskPath, "Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience",
            "Compatibility", "test", true);

    // ---------------- restore point frequency ----------------
    [Fact]
    public void RestorePoint_ChildFailure_StillRestoresFrequency()
    {
        var registry = new FakeRegistryGateway();
        registry.Values["LocalMachine|" + RestoreKey() + "|" + Frequency()] = 24;
        var checkpoint = new FakeRestorePointCheckpoint { Throws = new System.TimeoutException("child killed") };

        var (ok, _) = RestorePointService.Create(registry, checkpoint);

        Assert.False(ok);
        Assert.Equal(1, checkpoint.Calls);
        Assert.Equal(24, registry.Values["LocalMachine|" + RestoreKey() + "|" + Frequency()]);
        Assert.False(registry.HasBackups(RestorePointService.FrequencyTweakId));
    }

    [Fact]
    public void RestorePoint_Success_RestoresOriginalFrequency()
    {
        var registry = new FakeRegistryGateway();
        registry.Values["LocalMachine|" + RestoreKey() + "|" + Frequency()] = 1440;

        var (ok, _) = RestorePointService.Create(registry, new FakeRestorePointCheckpoint());

        Assert.True(ok);
        Assert.Equal(1440, registry.Values["LocalMachine|" + RestoreKey() + "|" + Frequency()]);
        Assert.Empty(registry.Originals);
    }

    [Fact]
    public void RestorePoint_FailedRestore_KeepsBackupAndReportsIt()
    {
        var registry = new FakeRegistryGateway { FailRestore = true };
        registry.Values["LocalMachine|" + RestoreKey() + "|" + Frequency()] = 24;

        var (ok, message) = RestorePointService.Create(registry, new FakeRestorePointCheckpoint());

        Assert.False(ok);
        Assert.Contains("limit", message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(registry.Originals);
    }

    [Fact]
    public void RestorePoint_FrequencyWriteFails_DoesNotRunCheckpoint()
    {
        var registry = new FakeRegistryGateway { FailOnWrite = _ => true };
        var checkpoint = new FakeRestorePointCheckpoint();

        var (ok, _) = RestorePointService.Create(registry, checkpoint);

        Assert.False(ok);
        Assert.Equal(0, checkpoint.Calls);
    }

    [Fact]
    public void RestorePoint_LeftoverLiftedValue_IsRestoredBeforeLiftingAgain()
    {
        var registry = new FakeRegistryGateway();
        var frequency = "LocalMachine|" + RestoreKey() + "|" + Frequency();

        // Simulate a killed run: the original is saved and the live value was left lifted.
        registry.Values[frequency] = 24;
        registry.BeginAttempt(RestorePointService.FrequencyTweakId)
            .Write(Microsoft.Win32.RegistryHive.LocalMachine,
                RestoreKey(), Frequency(), Microsoft.Win32.RegistryValueKind.DWord, 0);
        registry.Originals[frequency] = (true, 24, RestorePointService.FrequencyTweakId);

        var checkpoint = new FakeRestorePointCheckpoint();
        var (ok, _) = RestorePointService.Create(registry, checkpoint);

        Assert.True(ok);
        Assert.Equal(1, checkpoint.Calls);
        Assert.Equal(24, registry.Values[frequency]);
        Assert.False(registry.HasBackups(RestorePointService.FrequencyTweakId));
    }

    [Fact]
    public void RestorePoint_FinishesAtStartingValue_NotOlderSavedOne()
    {
        var registry = new FakeRegistryGateway();
        var frequency = "LocalMachine|" + RestoreKey() + "|" + Frequency();
        registry.Values[frequency] = 1440;
        registry.Originals[frequency] = (true, 60, RestorePointService.FrequencyTweakId);

        var (ok, _) = RestorePointService.Create(registry, new FakeRestorePointCheckpoint());

        Assert.True(ok);
        Assert.Equal(1440, registry.Values[frequency]);
    }

    [Fact]
    public void RestorePoint_ChildFailure_ReturnsToStartingValue()
    {
        var registry = new FakeRegistryGateway();
        var frequency = "LocalMachine|" + RestoreKey() + "|" + Frequency();
        registry.Values[frequency] = 1440;
        var checkpoint = new FakeRestorePointCheckpoint { Throws = new System.TimeoutException("killed") };

        var (ok, _) = RestorePointService.Create(registry, checkpoint);

        Assert.False(ok);
        Assert.Equal(1440, registry.Values[frequency]);
    }

    // ---------------- update pause ----------------

    [Fact]
    public void Pause_ThenResume_RestoresMissingValues()
    {
        var registry = new FakeRegistryGateway();

        var (pauseOk, _) = UpdatePauseService.Pause(7, registry);
        Assert.True(pauseOk);
        Assert.True(registry.HasBackups(UpdatePauseService.TweakId));

        var (resumeOk, _) = UpdatePauseService.Resume(registry);

        Assert.True(resumeOk);
        Assert.Empty(registry.Values);
        Assert.False(registry.HasBackups(UpdatePauseService.TweakId));
    }

    [Fact]
    public void Pause_ThenResume_RestoresExistingValues()
    {
        var registry = new FakeRegistryGateway();
        var id = "LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime";
        registry.Values[id] = "2030-01-01T00:00:00Z";

        UpdatePauseService.Pause(7, registry);
        UpdatePauseService.Resume(registry);

        Assert.Equal("2030-01-01T00:00:00Z", registry.Values[id]);
    }

    [Fact]
    public void Pause_PartialWrite_RollsBackWrittenValues()
    {
        var registry = new FakeRegistryGateway { FailOnWrite = n => n == 3 };

        var (ok, message) = UpdatePauseService.Pause(7, registry);

        Assert.False(ok);
        Assert.DoesNotContain("backed up", message);
        Assert.Empty(registry.Values);
        Assert.False(registry.HasBackups(UpdatePauseService.TweakId));
    }

    [Fact]
    public void Resume_ExternalPauseChange_LeavesValuesAlone()
    {
        var registry = new FakeRegistryGateway();
        UpdatePauseService.Pause(7, registry);
        var expiry = "LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime";
        registry.Values[expiry] = "2031-05-05T00:00:00Z";

        var (ok, message) = UpdatePauseService.Resume(registry);

        Assert.True(ok);
        Assert.Contains("outside Stratus", message);
        Assert.Equal("2031-05-05T00:00:00Z", registry.Values[expiry]);
        Assert.True(registry.HasBackups(UpdatePauseService.TweakId));
    }

    [Fact]
    public void Resume_NoOwner_DoesNotTouchValues()
    {
        var registry = new FakeRegistryGateway();
        var expiry = "LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime";
        registry.Values[expiry] = "2031-05-05T00:00:00Z";

        var (ok, _) = UpdatePauseService.Resume(registry);

        Assert.True(ok);
        Assert.Equal("2031-05-05T00:00:00Z", registry.Values[expiry]);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public void Resume_FailedRestore_KeepsBackupsAndFails()
    {
        var registry = new FakeRegistryGateway();
        UpdatePauseService.Pause(7, registry);
        registry.FailRestore = true;

        var (ok, _) = UpdatePauseService.Resume(registry);

        Assert.False(ok);
        Assert.True(registry.HasBackups(UpdatePauseService.TweakId));
    }

    [Fact]
    public void Pause_ReapplyFailure_RestoresAttemptStartNotFirstOriginal()
    {
        var registry = new FakeRegistryGateway();

        // First pause succeeds: the durable baseline is "absent".
        Assert.True(UpdatePauseService.Pause(7, registry).Success);
        var expiry = "LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime";
        var firstExpiry = Assert.IsType<string>(registry.Values[expiry]);

        // Reapply fails on the fourth value: the attempt must return to the first pause, not
        // to the state before it, because the first pause is what the user currently has.
        registry.FailOnWrite = n => n == 11;

        var (ok, _) = UpdatePauseService.Pause(14, registry);

        Assert.False(ok);
        Assert.Equal(firstExpiry, registry.Values[expiry]);
        Assert.True(registry.HasBackups(UpdatePauseService.TweakId));

        registry.FailOnWrite = null;
        var (resumeOk, _) = UpdatePauseService.Resume(registry);
        Assert.True(resumeOk);
        Assert.Empty(registry.Values);
    }

    // ---------------- update reset ----------------

    [Fact]
    public void UpdateReset_RestartsTimedOutStop()
    {
        var control = new FakeUpdateResetControl
        {
            MoveThrows = new IOException("folder locked")
        };
        control.Stops["wuauserv"] = UpdateServiceStop.TimedOut;
        control.Stops["bits"] = UpdateServiceStop.Stopped;
        control.ExistingFolders.Add(UpdateResetServiceFolders()[0]);

        var result = UpdateResetService.ResetDetailed(control);

        Assert.False(result.Ok);
        Assert.Contains("wuauserv", result.TimedOutStops);
        Assert.Contains("wuauserv", result.AttemptedStops);
        Assert.Contains("wuauserv", control.Started);
        Assert.Contains("bits", control.Started);
    }

    [Fact]
    public void UpdateReset_RestartFailure_IsReported()
    {
        var control = new FakeUpdateResetControl();
        control.Stops["wuauserv"] = UpdateServiceStop.Stopped;
        control.StartFailures.Add("wuauserv");
        control.ExistingFolders.Add(UpdateResetServiceFolders()[0]);
        control.ExistingFolders.Add(UpdateResetServiceFolders()[1]);

        var result = UpdateResetService.ResetDetailed(control);

        Assert.False(result.Ok);
        Assert.Contains("wuauserv", result.RestartFailures);
        Assert.Contains("did not restart", result.Message);
    }

    [Fact]
    public void UpdateReset_SuccessReportsMovedFolders()
    {
        var control = new FakeUpdateResetControl();
        control.Stops["wuauserv"] = UpdateServiceStop.Stopped;
        control.ExistingFolders.Add(UpdateResetServiceFolders()[0]);

        var result = UpdateResetService.ResetDetailed(control);

        Assert.True(result.Ok);
        Assert.Single(result.AttemptedStops);
        Assert.Empty(result.RestartFailures);
        Assert.Single(control.Moved);
    }

    [Fact]
    public void UpdateReset_FailedStop_IsTrackedAsAttempt()
    {
        var control = new FakeUpdateResetControl();
        control.Stops["bits"] = UpdateServiceStop.Failed;
        control.ExistingFolders.Add(UpdateResetServiceFolders()[0]);

        var result = UpdateResetService.ResetDetailed(control);

        Assert.Contains("bits", result.AttemptedStops);
        Assert.Empty(result.TimedOutStops);
    }

    // ---------------- cleanup restart ----------------

    [Fact]
    public void Clean_ReportsServiceRestartFailure()
    {
        var target = _tree.Dir("target");
        _tree.File("target/entry.tmp");
        var control = new FakeCleanupServiceControl();
        control.Running.Add("wuauserv");
        control.StartFailures.Add("wuauserv");

        var result = new CleanupService(control).Clean(
            [new CleanupTarget("test", "Test", target, true, StopServices: ["wuauserv"])],
            includeRecycleBin: false);

        Assert.NotNull(result.RestartFailures);
        Assert.Contains("wuauserv", result.RestartFailures!);
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void Clean_NoRestartFailures_ReturnsEmptyList()
    {
        var target = _tree.Dir("target");
        _tree.File("target/entry.tmp");
        var control = new FakeCleanupServiceControl();
        control.Running.Add("wuauserv");

        var result = new CleanupService(control).Clean(
            [new CleanupTarget("test", "Test", target, true, StopServices: ["wuauserv"])],
            includeRecycleBin: false);

        Assert.NotNull(result.RestartFailures);
        Assert.Empty(result.RestartFailures!);
        Assert.Contains("wuauserv", control.Started);
    }

    // ---------------- scheduled tasks ----------------

    [Fact]
    public void SetEnabled_PersistsOriginalBeforeToggle()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);

        var result = service.SetEnabled(Task(), enabled: false);

        Assert.True(result.Success);
        Assert.NotNull(sessions.StateAtSet);
        Assert.Contains("Microsoft Compatibility Appraiser", sessions.StateAtSet);
    }

    [Fact]
    public void SetEnabled_UnpersistableState_LeavesTaskUnchanged()
    {
        // A directory as the state path makes every save fail.
        var state = _tree.Dir("state");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);

        var result = service.SetEnabled(Task(), enabled: false);

        Assert.False(result.Success);
        Assert.Empty(sessions.SetCalls);
        Assert.True(sessions.Enabled[TaskPath]);
    }

    [Fact]
    public void Reset_SetFails_KeepsOriginal()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);
        service.SetEnabled(Task(), enabled: false);
        sessions.FailSet = true;

        var result = service.Reset(Task());

        Assert.False(result.Success);
        Assert.True(service.IsModified(Task()));
        var reloaded = new ScheduledTaskService(state, sessions);
        Assert.True(reloaded.IsModified(Task()));
    }

    [Fact]
    public void Reset_SaveFails_KeepsOriginalAndReportsFailure()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);
        service.SetEnabled(Task(), enabled: false);

        // After the original is dropped the save fails; the in-memory entry must come back.
        File.Delete(state);
        Directory.CreateDirectory(state);
        var result = service.Reset(Task());

        Assert.False(result.Success);
        Assert.True(service.IsModified(Task()));
    }

    [Fact]
    public void SetEnabled_AlreadyInTargetState_DoesNotRecordChange()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);

        var result = service.SetEnabled(Task(), enabled: true);

        Assert.True(result.Success);
        Assert.False(service.IsModified(Task()));
        Assert.Empty(sessions.SetCalls);
    }

    [Fact]
    public void SetEnabled_UnreadableTaskState_FailsInsteadOfClaimingMissing()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state, FailRead = true };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);

        var result = service.SetEnabled(Task(), enabled: false);

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.Error);
        Assert.Empty(sessions.SetCalls);
    }

    // ---------------- DNS ----------------

    [Fact]
    public void Dns_SecondAdapterFails_RollsBackEveryTouchedAdapter()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        adapters.Adapters.Add(new DnsAdapterOriginal("Wi-Fi", ["192.168.1.2"], []));
        var commands = new FakeDnsCommands
        {
            Override = line => line.Contains("name=\"Wi-Fi\"", StringComparison.Ordinal)
                       && line.Contains("static 1.1.1.1", StringComparison.Ordinal)
                ? new ProcessResult(1, "", "The parameter is incorrect.")
                : null
        };
        var originals = new InMemoryDnsOriginals();

        var (ok, message) = new DnsService(commands, adapters, originals).Apply(
            DnsService.Profiles.Single(p => p.Id == "cloudflare"));

        Assert.False(ok);
        Assert.Contains("The parameter is incorrect.", message);
        Assert.Contains(commands.Calls, c => c.Contains("name=\"Ethernet\"", StringComparison.Ordinal) && c.Contains("static 192.168.1.1"));
        Assert.Contains(commands.Calls, c => c.Contains("name=\"Wi-Fi\"", StringComparison.Ordinal) && c.Contains("static 192.168.1.2"));
        Assert.Contains(commands.Calls, c => c.Contains("flushdns", StringComparison.Ordinal));
    }

    [Fact]
    public void Dns_SavesOriginalsBeforeFirstChange()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        var commands = new FakeDnsCommands();
        var originals = new InMemoryDnsOriginals();
        var savedBeforeChange = false;

        commands.OnRun = _ =>
        {
            if (originals.Data.ContainsKey("Ethernet"))
                savedBeforeChange = true;
        };

        var (ok, _) = new DnsService(commands, adapters, originals).Apply(
            DnsService.Profiles.Single(p => p.Id == "google"));

        Assert.True(ok);
        Assert.True(savedBeforeChange);
        Assert.Single(originals.Data);
    }

    [Fact]
    public void Dns_UnpersistableOriginals_ChangesNothing()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        var commands = new FakeDnsCommands();
        var originals = new InMemoryDnsOriginals { FailSave = true };

        var (ok, message) = new DnsService(commands, adapters, originals).Apply(
            DnsService.Profiles.Single(p => p.Id == "google"));

        Assert.False(ok);
        Assert.Contains("could not be saved", message);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public void Dns_ReapplyFailure_RestoresAttemptStartNotFirstOriginal()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        var commands = new FakeDnsCommands();
        var originals = new InMemoryDnsOriginals();
        var service = new DnsService(commands, adapters, originals);

        // Initial state A -> successful apply B.
        var (firstOk, _) = service.Apply(DnsService.Profiles.Single(p => p.Id == "cloudflare"));
        Assert.True(firstOk);

        // The adapter now reports B; a second apply C fails midway.
        adapters.Adapters.Clear();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["1.1.1.1", "1.0.0.1"], []));
        commands.Calls.Clear();
        commands.Override = line => line.Contains("static 8.8.8.8", StringComparison.Ordinal)
            ? new ProcessResult(1, "", "failure")
            : null;

        var (secondOk, _) = service.Apply(DnsService.Profiles.Single(p => p.Id == "google"));

        Assert.False(secondOk);
        // Attempt rollback returns to B, the state this attempt started from.
        Assert.Contains(commands.Calls, c =>
            c.Contains("name=\"Ethernet\"", StringComparison.Ordinal) && c.Contains("static 1.1.1.1", StringComparison.Ordinal));
        Assert.Contains(commands.Calls, c => c.Contains("1.0.0.1", StringComparison.Ordinal) && c.Contains("index=2", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Calls, c => c.Contains("static 192.168.1.1", StringComparison.Ordinal));

        // The durable first original is untouched; explicit revert still returns to A.
        Assert.Equal(["192.168.1.1"], originals.Data["Ethernet"].Ipv4);
        commands.Calls.Clear();
        commands.Override = null;
        var (revertOk, _) = service.Revert();
        Assert.True(revertOk);
        Assert.Contains(commands.Calls, c => c.Contains("static 192.168.1.1", StringComparison.Ordinal));
        Assert.Empty(originals.Data);
    }

    [Fact]
    public void Dns_Revert_RestoresSavedServers()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1", "192.168.1.2"], ["fe80::1"]));
        var commands = new FakeDnsCommands();
        var originals = new InMemoryDnsOriginals();
        var service = new DnsService(commands, adapters, originals);
        service.Apply(DnsService.Profiles.Single(p => p.Id == "google"));
        commands.Calls.Clear();

        var (ok, _) = service.Revert();

        Assert.True(ok);
        Assert.Contains(commands.Calls, c => c.Contains("name=\"Ethernet\"", StringComparison.Ordinal) && c.Contains("static 192.168.1.1"));
        Assert.Contains(commands.Calls, c => c.Contains("index=2", StringComparison.Ordinal) && c.Contains("192.168.1.2"));
        Assert.Contains(commands.Calls, c => c.Contains("ipv6", StringComparison.Ordinal) && c.Contains("fe80::1"));
        Assert.Empty(originals.Data);
    }

    [Fact]
    public void Dns_Revert_PartialFailure_KeepsFailedOriginal()
    {
        var commands = new FakeDnsCommands();
        var originals = new InMemoryDnsOriginals();
        originals.Data["Ethernet"] = new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []);
        originals.Data["Wi-Fi"] = new DnsAdapterOriginal("Wi-Fi", ["192.168.1.2"], []);
        commands.Override = line =>
            line.Contains("name=\"Wi-Fi\"", StringComparison.Ordinal)
                ? new ProcessResult(1, "", "Access is denied.")
                : null;

        var (ok, message) = new DnsService(commands, new FakeDnsAdapters(), originals).Revert();

        Assert.False(ok);
        Assert.Contains("Wi-Fi", message);
        Assert.Single(originals.Data);
        Assert.True(originals.Data.ContainsKey("Wi-Fi"));
    }

    [Fact]
    public void Dns_ApplyFailure_RollsBackIsolatedFromLocalizedText()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", [], []));
        var commands = new FakeDnsCommands
        {
            Override = line => line.Contains("static 1.1.1.1", StringComparison.Ordinal)
                ? new ProcessResult(1, "", "Le parametre est incorrect.")
                : null
        };

        var (ok, message) = new DnsService(commands, adapters, new InMemoryDnsOriginals()).Apply(
            DnsService.Profiles.Single(p => p.Id == "cloudflare"));

        Assert.False(ok);
        Assert.Contains("Le parametre est incorrect.", message);
        Assert.Contains(commands.Calls, c => c.Contains("source=dhcp", StringComparison.Ordinal));
    }

    // ---------------- DNS: process start failure, unreadable state, transaction ----------------

    [Fact]
    public void Dns_RunThrows_RollsBackInsteadOfEscaping()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        var commands = new FakeDnsCommands { ThrowOn = "name=\"Ethernet\" static 8.8.8.8" };
        var originals = new InMemoryDnsOriginals();

        var (ok, message) = new DnsService(commands, adapters, originals).Apply(
            DnsService.Profiles.Single(p => p.Id == "google"));

        Assert.False(ok);
        Assert.Contains("could not be started", message);
        Assert.Contains(commands.Calls, c =>
            c.Contains("static 192.168.1.1", StringComparison.Ordinal));
        Assert.True(originals.TransactionOpen == false);
    }

    [Fact]
    public void Dns_AdapterReadFailure_FailsInsteadOfClaimingNoAdapters()
    {
        var adapters = new FakeDnsAdapters { Throw = new InvalidOperationException("WMI is down") };
        var commands = new FakeDnsCommands();

        var (ok, message) = new DnsService(commands, adapters, new InMemoryDnsOriginals()).Apply(
            DnsService.Profiles.Single(p => p.Id == "cloudflare"));

        Assert.False(ok);
        Assert.Contains("could not be read", message);
        Assert.DoesNotContain("No connected physical adapters", message);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public void Dns_UnreadableOriginals_ChangesNothing()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        var commands = new FakeDnsCommands();
        var originals = new InMemoryDnsOriginals { FailLoad = true };

        var (ok, message) = new DnsService(commands, adapters, originals).Apply(
            DnsService.Profiles.Single(p => p.Id == "google"));

        Assert.False(ok);
        Assert.Contains("No adapter was changed", message);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public void Dns_TransactionUnavailable_ChangesNothing()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        var commands = new FakeDnsCommands();
        var originals = new InMemoryDnsOriginals { FailTransaction = true };

        var (ok, message) = new DnsService(commands, adapters, originals).Apply(
            DnsService.Profiles.Single(p => p.Id == "google"));

        Assert.False(ok);
        Assert.Contains("Another Stratus process", message);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public void Dns_ApplyAndRevert_TakeTheTransactionLock()
    {
        var adapters = new FakeDnsAdapters();
        adapters.Adapters.Add(new DnsAdapterOriginal("Ethernet", ["192.168.1.1"], []));
        var originals = new InMemoryDnsOriginals();
        var service = new DnsService(new FakeDnsCommands(), adapters, originals);

        service.Apply(DnsService.Profiles.Single(p => p.Id == "google"));
        Assert.Equal(1, originals.Transactions);
        Assert.False(originals.TransactionOpen);

        service.Revert();
        Assert.Equal(2, originals.Transactions);
        Assert.False(originals.TransactionOpen);
    }

    [Fact]
    public void DnsOriginalsStore_CorruptFile_FailsInsteadOfReturningEmpty()
    {
        var path = Path.Combine(_tree.Dir("dns"), "dns-originals.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new DnsOriginalsStore(path);

        Assert.Throws<InvalidOperationException>(() => store.Load());
        Assert.True(File.Exists(path), "the unreadable file must be left in place");
    }

    [Fact]
    public void DnsOriginalsStore_TransactionSerialisesAcrossStoreInstances()
    {
        var path = Path.Combine(_tree.Dir("dns"), "dns-originals.json");
        var first = new DnsOriginalsStore(path);

        using var held = first.TryBeginTransaction();
        Assert.NotNull(held);

        // A named mutex is owned per thread, and the app and CLI are separate processes. Another
        // thread is the closest a unit test gets to that second process.
        var second = new DnsOriginalsStore(path, TimeSpan.FromMilliseconds(50));
        IDisposable? fromOtherThread = null;
        var thread = new Thread(() => fromOtherThread = second.TryBeginTransaction());
        thread.Start();
        thread.Join();

        Assert.Null(fromOtherThread);
    }

    [Fact]
    public void DnsOriginalsStore_TransactionIsReusableAfterRelease()
    {
        var path = Path.Combine(_tree.Dir("dns"), "dns-originals.json");
        var store = new DnsOriginalsStore(path);

        using (var first = store.TryBeginTransaction())
            Assert.NotNull(first);

        using var second = store.TryBeginTransaction();
        Assert.NotNull(second);
    }

    // ---------------- scheduled tasks: unreadable state ----------------

    [Fact]
    public void ScheduledTasks_CorruptStateFile_IsKeptAndChangesRefused()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        File.WriteAllText(state, "{ broken json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;

        var service = new ScheduledTaskService(state, sessions);

        Assert.False(service.IsAvailable);
        Assert.True(File.Exists(state), "the unreadable state file must be kept");
        Assert.False(File.Exists(state + ".corrupt"), "it must not be quarantined and replaced");

        var result = service.SetEnabled(Task(), enabled: false);
        Assert.False(result.Success);
        Assert.Empty(sessions.SetCalls);
    }

    [Fact]
    public void ScheduledTasks_CorruptStateFile_ResetIsRefused()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        File.WriteAllText(state, "not json at all");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        var service = new ScheduledTaskService(state, sessions);

        Assert.False(service.Reset(Task()).Success);
        Assert.Equal(-1, service.ResetAll());
    }

    [Fact]
    public void ScheduledTasks_ResetAllReportsFailuresAndKeepsOriginals()
    {
        var state = Path.Combine(_tree.Dir("reset-failure"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);
        Assert.True(service.SetEnabled(Task(), enabled: false).Success);
        sessions.FailSet = true;

        var result = service.ResetAllDetailed();

        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.True(service.IsModified(Task()));
        sessions.FailSet = false;
        Assert.Equal((1, 0), service.ResetAllDetailed());
    }

    // ---------------- restore points: scan failure ----------------

    [Fact]
    public void RestorePointScan_FailedProcess_IsNotAnEmptySuccess()
    {
        var result = RestorePointService.Scan(new FakeRestorePointScanner
        {
            Throws = new InvalidOperationException("powershell missing")
        });

        Assert.False(result.Available);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Points);
    }

    [Fact]
    public void RestorePointScan_NonZeroExit_IsNotAnEmptySuccess()
    {
        var result = RestorePointService.Scan(new FakeRestorePointScanner
        {
            Result = new ProcessResult(1, "", "the service cannot be started")
        });

        Assert.False(result.Available);
        Assert.Contains("could not be read", result.Error!);
    }

    [Fact]
    public void RestorePointScan_SuccessfulProcess_ReadsPoints()
    {
        var result = RestorePointService.Scan(new FakeRestorePointScanner
        {
            Result = new ProcessResult(0,
                "POINT|7|2030-01-01T00:00:00.0000000Z|0|Before cleanup\nSTORAGE|10|20\n", "")
        });

        Assert.True(result.Available);
        Assert.Single(result.Points);
        Assert.Equal(7, result.Points[0].Sequence);
        Assert.Equal(10, result.UsedBytes);
    }

    [Fact]
    public void RestorePoint_FrequencyWriteThrows_RollsBackAttempt()
    {
        var registry = new FakeRegistryGateway();
        registry.Values["LocalMachine|" + RestoreKey() + "|" + Frequency()] = 24;
        registry.FailOnWrite = _ => true;
        var checkpoint = new FakeRestorePointCheckpoint();

        var (ok, _) = RestorePointService.Create(registry, checkpoint);

        Assert.False(ok);
        Assert.Equal(0, checkpoint.Calls);
        Assert.Equal(24, registry.Values["LocalMachine|" + RestoreKey() + "|" + Frequency()]);
        // The failed attempt must not leave its backup behind, or a later run would treat a
        // write that never happened as a real original.
        Assert.False(registry.HasBackups(RestorePointService.FrequencyTweakId));
    }

    // ---------------- cleanup: stop ownership ----------------

    [Fact]
    public void Clean_StopThrows_ServiceIsStillRestarted()
    {
        var target = _tree.Dir("target");
        _tree.File("target/entry.tmp");
        var control = new ThrowingCleanupControl { ThrowOnStop = "wuauserv" };

        var result = new CleanupService(control).Clean(
            [new CleanupTarget("test", "Test", target, true, StopServices: ["wuauserv", "bits"])],
            includeRecycleBin: false);

        Assert.Contains("wuauserv", control.Started);
        Assert.Contains("bits", control.Started);
        Assert.Empty(result.RestartFailures!);
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void Clean_FailedStop_IsStillRestartedAndReportedWhenRestartFails()
    {
        var target = _tree.Dir("target");
        _tree.File("target/entry.tmp");
        var control = new FakeCleanupServiceControl();
        control.Running.Add("wuauserv");
        control.StopFails.Add("wuauserv");
        control.StartFailures.Add("wuauserv");

        var result = new CleanupService(control).Clean(
            [new CleanupTarget("test", "Test", target, true, StopServices: ["wuauserv"])],
            includeRecycleBin: false);

        Assert.Contains("wuauserv", control.Started);
        Assert.Contains("wuauserv", result.RestartFailures!);
    }

    [Fact]
    public void Clean_FailingTarget_IsCountedAndDoesNotAbortTheRest()
    {
        var failing = _tree.Dir("failing");
        _tree.File("failing/entry.tmp");
        var healthy = _tree.Dir("healthy");
        _tree.File("healthy/entry.tmp");
        var control = new FakeCleanupServiceControl();
        control.Running.Add("dosvc");

        var service = new CleanupService(control, (_, _) => new ProcessResult(0, "", ""),
            target => string.Equals(target.Id, "failing", StringComparison.Ordinal)
                ? new IOException("injected mid-clean failure")
                : null);

        var result = service.Clean(
        [
            new CleanupTarget("failing", "Failing", failing, true, StopServices: ["dosvc"]),
            new CleanupTarget("healthy", "Healthy", healthy, true),
        ], includeRecycleBin: false);

        Assert.Equal(1, result.FailedTargets);
        Assert.Equal(1, result.DeletedFiles);
        Assert.True(System.IO.File.Exists(Path.Combine(failing, "entry.tmp")),
            "the failing target must contribute no deletions");
        Assert.False(System.IO.File.Exists(Path.Combine(healthy, "entry.tmp")));
        Assert.Contains("dosvc", control.Started);
    }

    // ---------------- update pause: interrupted pause recovery ----------------

    [Fact]
    public void Pause_InterruptedBeforeOwnerValue_ResumeRestoresBackups()
    {
        var registry = new FakeRegistryGateway();
        var settings = "LocalMachine|" + PauseKey();

        // Simulate a crash: originals were saved for every value the attempt wrote, but the
        // owner value never landed. The saved originals are the explicit evidence of that attempt.
        var expiryKey = settings + "|PauseUpdatesExpiryTime";
        var startKey = settings + "|PauseUpdatesStartTime";
        registry.Originals[expiryKey] = (false, null, UpdatePauseService.TweakId);
        registry.Originals[startKey] = (false, null, UpdatePauseService.TweakId);
        registry.Values[expiryKey] = "2030-01-01T00:00:00Z";
        registry.Values[startKey] = "2029-01-01T00:00:00Z";

        var (ok, message) = UpdatePauseService.Resume(registry);

        Assert.True(ok);
        Assert.Contains("interrupted", message);
        Assert.DoesNotContain(expiryKey, registry.Values.Keys);
        Assert.DoesNotContain(startKey, registry.Values.Keys);
        Assert.False(registry.HasBackups(UpdatePauseService.TweakId));
    }

    [Fact]
    public void Pause_ExternalPauseWithoutBackups_IsStillLeftAlone()
    {
        var registry = new FakeRegistryGateway();
        var expiry = "LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime";
        registry.Values[expiry] = "2030-01-01T00:00:00Z";

        var (ok, message) = UpdatePauseService.Resume(registry);

        Assert.True(ok);
        Assert.Contains("outside Stratus", message);
        Assert.Equal("2030-01-01T00:00:00Z", registry.Values[expiry]);
    }

    [Fact]
    public void Resume_StrictReadFailure_FailsInsteadOfClaimingExternalPause()
    {
        var registry = new FakeRegistryGateway { FailStrictRead = true };
        var expiry = "LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime";
        registry.Values[expiry] = "2030-01-01T00:00:00Z";

        var (ok, message) = UpdatePauseService.Resume(registry);

        Assert.False(ok);
        Assert.Contains("could not be read", message);
        Assert.Equal("2030-01-01T00:00:00Z", registry.Values[expiry]);
    }

    [Fact]
    public void PauseRead_UnreadableRegistry_ThrowsInsteadOfClaimingNotPaused()
    {
        var registry = new FakeRegistryGateway { FailStrictRead = true };

        Assert.Throws<RegistryReadException>(() => UpdatePauseService.Read(registry));
    }

    [Fact]
    public void PauseRead_UnparseableExpiry_ThrowsInsteadOfClaimingNotPaused()
    {
        var registry = new FakeRegistryGateway();
        registry.Values["LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime"] = "not a date";

        Assert.Throws<RegistryReadException>(() => UpdatePauseService.Read(registry));
    }

    [Fact]
    public void PauseRead_AbsentValue_IsNotPaused()
    {
        Assert.False(UpdatePauseService.Read(new FakeRegistryGateway()).IsPaused);
    }

    [Fact]
    public void PauseRead_FutureExpiry_IsPausedWithUntilUtc()
    {
        var registry = new FakeRegistryGateway();
        registry.Values["LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime"] = "2030-01-01T00:00:00Z";

        var state = UpdatePauseService.Read(registry);

        Assert.True(state.IsPaused);
        Assert.Equal(
            DateTime.Parse("2030-01-01T00:00:00Z", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            state.UntilUtc);
    }

    [Fact]
    public void Pause_GateUnavailable_RefusesWithoutTouchingRegistry()
    {
        var registry = new FakeRegistryGateway();

        var (ok, message) = UpdatePauseService.Pause(7, registry, new FakeUpdatePauseGate { FailGate = true });

        Assert.False(ok);
        Assert.Contains("Another Stratus process", message);
        Assert.Equal(0, registry.Writes);
        Assert.False(registry.HasBackups(UpdatePauseService.TweakId));
    }

    [Fact]
    public void Resume_GateUnavailable_RefusesWithoutTouchingValues()
    {
        var registry = new FakeRegistryGateway();
        var expiry = "LocalMachine|" + PauseKey() + "|PauseUpdatesExpiryTime";
        registry.Values[expiry] = "2030-01-01T00:00:00Z";

        var (ok, _) = UpdatePauseService.Resume(registry, new FakeUpdatePauseGate { FailGate = true });

        Assert.False(ok);
        Assert.Equal("2030-01-01T00:00:00Z", registry.Values[expiry]);
    }

    [Fact]
    public void Pause_AndResume_TakeThePauseGate()
    {
        var registry = new FakeRegistryGateway();
        var gate = new FakeUpdatePauseGate();

        Assert.True(UpdatePauseService.Pause(7, registry, gate).Success);
        Assert.Equal(1, gate.Transactions);
        Assert.False(gate.Open);

        Assert.True(UpdatePauseService.Resume(registry, gate).Success);
        Assert.Equal(2, gate.Transactions);
        Assert.False(gate.Open);
    }

    [Fact]
    public void RestorePoint_StrictReadFailure_DoesNotLiftAndDoesNotRestore()
    {
        var registry = new FakeRegistryGateway { FailStrictRead = true };
        var frequency = "LocalMachine|" + RestoreKey() + "|" + Frequency();
        registry.Values[frequency] = 1440;
        var checkpoint = new FakeRestorePointCheckpoint();

        var (ok, _) = RestorePointService.Create(registry, checkpoint);

        Assert.False(ok);
        Assert.Equal(0, checkpoint.Calls);
        // Neither the machine value nor the saved original was touched by a failed read.
        Assert.Equal(1440, registry.Values[frequency]);
        Assert.Empty(registry.Originals);
    }

    // ---------------- scheduled tasks: scan strictness ----------------

    private static ScheduledTaskItem TaskItem(string name, bool enabled) =>
        new("task:\\Microsoft\\Windows\\Application Experience\\" + name,
            name, @"\Microsoft\Windows\Application Experience",
            "Compatibility", "test", enabled);

    [Fact]
    public void ScheduledTasks_Scan_StrictThrowsWhenSessionUnavailable()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state, Unavailable = true };
        var service = new ScheduledTaskService(state, sessions);

        Assert.Empty(service.Scan());
        Assert.Throws<InvalidOperationException>(() => service.Scan(requireComplete: true));
    }

    [Fact]
    public void ScheduledTasks_Scan_StrictThrowsWhenAKnownTaskIsUnreadable()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state, FailRead = true };
        var service = new ScheduledTaskService(state, sessions);

        Assert.Empty(service.Scan());
        var thrown = Assert.Throws<InvalidOperationException>(
            () => service.Scan(requireComplete: true));
        Assert.Contains("could not be read", thrown.Message);
    }

    [Fact]
    public void ScheduledTasks_Scan_StrictAllowsKnownEmpty()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        var service = new ScheduledTaskService(state, sessions);

        Assert.Empty(service.Scan(requireComplete: true));
    }

    // ---------------- scheduled tasks: two instances, stale originals ----------------

    [Fact]
    public void ScheduledTasks_TwoInstances_SecondOperationKeepsFirstOriginal()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        const string otherPath = @"\Microsoft\Windows\Autochk\Proxy";
        sessions.Enabled[otherPath] = true;

        var first = new ScheduledTaskService(state, sessions);
        var second = new ScheduledTaskService(state, sessions);

        var firstItem = new ScheduledTaskItem("task:" + TaskPath,
            "Microsoft Compatibility Appraiser", @"\Microsoft\Windows\Application Experience",
            "Compatibility", "test", true);
        var secondItem = new ScheduledTaskItem("task:" + otherPath,
            "Proxy", @"\Microsoft\Windows\Autochk", "Diagnostics", "test", true);

        Assert.True(first.SetEnabled(firstItem, enabled: false).Success);
        Assert.True(second.SetEnabled(secondItem, enabled: false).Success);

        // A third instance reads the file fresh: both originals must be there. Without a
        // reload-and-merge, the second instance's save would have erased the first one.
        var reloaded = new ScheduledTaskService(state, sessions);
        Assert.True(reloaded.IsModified(firstItem));
        Assert.True(reloaded.IsModified(secondItem));

        Assert.True(reloaded.Reset(firstItem).Success);
        Assert.True(reloaded.IsModified(secondItem));
        Assert.True(sessions.Enabled[TaskPath]);
    }

    // ---------------- scheduled tasks: refusal contract ----------------

    [Fact]
    public void ScheduledTasks_Unavailable_ResetAllRefusesWithNegativeResult()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        File.WriteAllText(state, "{ broken json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        var service = new ScheduledTaskService(state, sessions);

        Assert.False(service.IsAvailable);
        Assert.Equal(-1, service.ResetAll());
        Assert.True(File.Exists(state), "the unreadable state file must be kept");
    }

    // ---------------- scheduled tasks: unknown outcome keeps the original ----------------

    [Fact]
    public void ScheduledTasks_UnknownToggleOutcome_KeepsOriginalForRecovery()
    {
        var state = Path.Combine(_tree.Dir("state"), "tasks.json");
        var sessions = new FakeTaskSessionFactory { StateFile = state };
        sessions.Enabled[TaskPath] = true;
        var service = new ScheduledTaskService(state, sessions);

        Assert.True(service.SetEnabled(Task(), enabled: false).Success);
        sessions.FailSet = true;

        // The toggle outcome is unknown: the recorded original must survive as the retry
        // marker, so a reset can still put the task back.
        Assert.False(service.SetEnabled(Task(), enabled: true).Success);
        Assert.True(service.IsModified(Task()));

        var reloaded = new ScheduledTaskService(state, sessions);
        Assert.True(reloaded.IsModified(Task()));

        sessions.FailSet = false;
        Assert.True(service.Reset(Task()).Success);
        Assert.True(sessions.Enabled[TaskPath]);
        Assert.False(service.IsModified(Task()));
    }

    // ---------------- cleanup: recycle bin partial result ----------------

    [Fact]
    public void Clean_RecycleBinRunnerThrows_ResultReportsPartial()
    {
        var target = _tree.Dir("target");
        _tree.File("target/entry.tmp");
        var control = new FakeCleanupServiceControl();

        var result = new CleanupService(control, (_, _) => throw new InvalidOperationException("powershell gone"))
            .Clean([new CleanupTarget("test", "Test", target, true)], includeRecycleBin: true);

        Assert.True(result.RecycleBinFailed);
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void Clean_RecycleBinRunnerFails_ResultReportsPartial()
    {
        var target = _tree.Dir("target");
        _tree.File("target/entry.tmp");
        var control = new FakeCleanupServiceControl();

        var result = new CleanupService(control, (_, _) => new ProcessResult(1, "", "access denied"))
            .Clean([new CleanupTarget("test", "Test", target, true)], includeRecycleBin: true);

        Assert.True(result.RecycleBinFailed);
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void Clean_RecycleBinEmptied_ResultIsClean()
    {
        var target = _tree.Dir("target");
        _tree.File("target/entry.tmp");
        var control = new FakeCleanupServiceControl();

        var result = new CleanupService(control, (_, _) => new ProcessResult(0, "", ""))
            .Clean([new CleanupTarget("test", "Test", target, true)], includeRecycleBin: true);

        Assert.False(result.RecycleBinFailed);
        Assert.Equal(1, result.DeletedFiles);
    }

    // ---------------- helpers ----------------
    private static string RestoreKey() => @"SOFTWARE\Microsoft\Windows NT\SystemRestore";

    private static string Frequency() => "SystemRestorePointCreationFrequency";

    private static string PauseKey() => @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    private static string[] UpdateResetServiceFolders()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return
        [
            Path.Combine(windows, "SoftwareDistribution"),
            Path.Combine(windows, "System32", "catroot2"),
        ];
    }

    public void Dispose() => _tree.Dispose();
}
