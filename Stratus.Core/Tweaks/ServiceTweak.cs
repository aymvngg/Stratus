using System.ServiceProcess;
using Microsoft.Win32;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.Core.Tweaks;

public enum ServiceStartupType
{
    Disabled,
    Manual,
    Automatic,
    AutomaticDelayed
}

/// <summary>
/// Sets a Windows service startup type (disable, manual, automatic); reverts to the startup
/// type it had before (backed up) or a sane fallback. The saved original keeps the delayed-auto
/// flag, and a revert with no saved original only undoes a service still at this tweak's
/// target, so a fallback is never written over an unrelated start type.
/// </summary>
public sealed class ServiceTweak : Tweak
{
    public const string ScopeType = "service";
    public const string StartTypeKey = "StartType";

    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services\";

    private readonly string _serviceName;
    private readonly ServiceStartupType _target;
    private readonly ServiceStartupType _revertFallback;
    private readonly IBackupStore _backup;
    private readonly Func<string, ServiceStartMode?> _query;
    private readonly Func<string, ServiceStartupType, (bool Success, string Error)> _configure;
    private readonly Func<string, bool> _stop;
    private readonly Func<string, bool> _start;
    private readonly Func<string, ServiceControllerStatus?> _status;

    /// <summary>
    /// True when the service exists, false when it is absent, null when the check itself
    /// failed. Null means the seam is not wired and the plain query result decides.
    /// </summary>
    private readonly Func<string, bool?>? _serviceExists;

    /// <summary>
    /// True when the service starts with the delayed-auto flag, false when not, null when the
    /// flag could not be read. Null means the seam is not wired and the flag is not checked.
    /// </summary>
    private readonly Func<string, bool?>? _delayedAuto;

    public ServiceTweak(
        string id, string name, string description,
        string serviceName, ServiceStartupType target, ServiceStartupType revertFallback,
        IBackupStore backup,
        int weight = 1, RiskLevel risk = RiskLevel.Recommended)
        : this(id, name, description, serviceName, target, revertFallback, backup,
               QueryStartMode, ConfigureStartType, StopService, StartService, weight, risk,
               status: null,
               serviceExists: DefaultServiceExists(backup),
               delayedAuto: DefaultDelayedAuto(backup))
    {
    }

    internal ServiceTweak(
        string id, string name, string description,
        string serviceName, ServiceStartupType target, ServiceStartupType revertFallback,
        IBackupStore backup,
        Func<string, ServiceStartMode?> query,
        Func<string, ServiceStartupType, (bool Success, string Error)> configure,
        Func<string, bool> stop,
        Func<string, bool> start,
        int weight = 1, RiskLevel risk = RiskLevel.Recommended,
        Func<string, ServiceControllerStatus?>? status = null,
        Func<string, bool?>? serviceExists = null,
        Func<string, bool?>? delayedAuto = null)
        : base(id, name, description, TweakCategory.Services)
    {
        _serviceName = serviceName;
        _target = target;
        _revertFallback = revertFallback;
        _backup = backup;
        _query = query;
        _configure = configure;
        _stop = stop;
        _start = start;
        _status = status ?? QueryStatus;
        _serviceExists = serviceExists;
        _delayedAuto = delayedAuto;
        Weight = weight;
        Risk = risk;
    }

    /// <summary>Reads the service key through RegistryService: failed read unknown, absent key absent.</summary>
    private static Func<string, bool?> DefaultServiceExists(IBackupStore backup)
    {
        var registry = new RegistryService(backup);
        return serviceName =>
        {
            var read = registry.Read(RegistryHive.LocalMachine, ServicesRoot + serviceName, "Start");
            return read.ReadFailed ? null : read.Exists;
        };
    }

    /// <summary>Reads the DelayedAutoStart flag: absent value means not delayed, failed read means unknown.</summary>
    private static Func<string, bool?> DefaultDelayedAuto(IBackupStore backup)
    {
        var registry = new RegistryService(backup);
        return serviceName =>
        {
            var read = registry.Read(RegistryHive.LocalMachine, ServicesRoot + serviceName, "DelayedAutoStart");
            if (read.ReadFailed)
                return null;

            return read.Exists
                && Convert.ToInt64(read.Value, System.Globalization.CultureInfo.InvariantCulture) == 1;
        };
    }

    private static ServiceStartMode? QueryStartMode(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.StartType;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not query service {serviceName}: {ex.Message}");
            return null;
        }
    }

    private static (bool Success, string Error) ConfigureStartType(string serviceName, ServiceStartupType target)
    {
        var result = ProcessRunner.Run("sc.exe", $"config {serviceName} start= {ScKeyword(target)}");
        return result.Success
            ? (true, "")
            : (false, $"sc.exe exited with {result.ExitCode}: {result.StdErr.Trim()}");
    }

    private static bool StopService(string serviceName) => ChangeRunningState(serviceName, stop: true);
    private static bool StartService(string serviceName) => ChangeRunningState(serviceName, stop: false);

    private static ServiceControllerStatus? QueryStatus(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            return service.Status;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read service status for {serviceName}: {ex.Message}");
            return null;
        }
    }

    private static bool ChangeRunningState(string serviceName, bool stop)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (stop)
            {
                if (!sc.CanStop || sc.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
                    return true;
                sc.Stop();
            }
            else
            {
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                    return true;
                sc.Start();
            }

            sc.WaitForStatus(stop ? ServiceControllerStatus.Stopped : ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not {(stop ? "stop" : "restart")} service {serviceName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// An absent service is genuinely not applicable; a service whose presence or start type
    /// cannot be read is unknown, and the score reports it as a read error instead of counting
    /// it as unsupported or disabled.
    /// </summary>
    public override bool IsApplicable
    {
        get
        {
            if (_serviceExists is null)
                return _query(_serviceName) is not null;

            var exists = _serviceExists(_serviceName);
            if (exists is false)
                return false;
            if (exists is null)
                throw new InvalidOperationException($"The presence of service {_serviceName} could not be verified.");

            return true;
        }
    }

    // Automatic (Delayed Start) reports as plain Automatic through ServiceController.
    private bool MatchesTarget(ServiceStartMode mode) => _target switch
    {
        ServiceStartupType.Disabled => mode == ServiceStartMode.Disabled,
        ServiceStartupType.Manual => mode == ServiceStartMode.Manual,
        ServiceStartupType.Automatic or ServiceStartupType.AutomaticDelayed => mode == ServiceStartMode.Automatic,
        _ => false
    };

    public override bool IsApplied
    {
        get
        {
            if (_serviceExists is not null)
            {
                var exists = _serviceExists(_serviceName);
                if (exists is false)
                    return false;
                if (exists is null)
                    throw new InvalidOperationException($"The presence of service {_serviceName} could not be verified.");
            }

            var mode = _query(_serviceName);
            if (mode is null)
            {
                if (_serviceExists is null)
                    return false; // seam not wired: keep the plain query answer

                throw new InvalidOperationException($"The start type of {_serviceName} could not be read.");
            }

            return MatchesTarget(mode.Value);
        }
    }

    private static string ScKeyword(ServiceStartupType t) => t switch
    {
        ServiceStartupType.Disabled => "disabled",
        ServiceStartupType.Manual => "demand",
        ServiceStartupType.Automatic => "auto",
        ServiceStartupType.AutomaticDelayed => "delayed-auto",
        _ => "demand"
    };

    public override TweakResult Apply()
    {
        try
        {
            // An unknown presence must stop the change before anything is written or saved.
            if (_serviceExists is not null)
            {
                var exists = _serviceExists(_serviceName);
                if (exists is null)
                    return TweakResult.Fail($"The presence of {_serviceName} could not be verified. Nothing was changed.");
                if (exists is false)
                    return TweakResult.Fail($"The service {_serviceName} is not present. Nothing was changed.");
            }

            // An unknown start type must stop the change before anything is written or saved.
            var before = _query(_serviceName);
            if (before is null)
                return TweakResult.Fail($"The service {_serviceName} could not be found or queried. Nothing was changed.");

            // Boot and system start types belong to drivers. Changing one can break the boot;
            // refuse instead of saving a wrong "Manual" original.
            if (before.Value is ServiceStartMode.Boot or ServiceStartMode.System)
                return TweakResult.Fail($"{_serviceName} has a boot or system start type. Stratus does not change driver services.");

            // Automatic (Delayed Start) reports as plain Automatic through ServiceController, so
            // the delayed flag is read from the registry while the original is saved. Storing it
            // in the original ("AutomaticDelayed") lets revert map back to "delayed-auto"; older
            // backup files hold "Automatic" and keep restoring as plain automatic.
            string savedValue;
            if (before.Value == ServiceStartMode.Automatic && _delayedAuto is not null)
            {
                var delayed = _delayedAuto(_serviceName);
                if (delayed is null)
                    return TweakResult.Fail($"The delayed-auto flag of {_serviceName} could not be read. Nothing was changed.");

                savedValue = delayed is true
                    ? nameof(ServiceStartupType.AutomaticDelayed)
                    : nameof(ServiceStartupType.Automatic);
            }
            else
            {
                savedValue = MapToString(before.Value);
            }

            var status = _target == ServiceStartupType.Disabled ? _status(_serviceName) : ServiceControllerStatus.Stopped;
            if (status is null)
                return TweakResult.Fail($"The running state of {_serviceName} could not be read. Nothing was changed.");

            // First writer wins: a backup already stored (an earlier apply, or a failed revert
            // that kept its entries) still holds the untouched original.
            var added = _backup.TryAddOriginal(new BackupEntry
            {
                TweakId = Id,
                ScopeType = ScopeType,
                ScopeId = _serviceName,
                Key = StartTypeKey,
                ValueJson = savedValue,
                Existed = true
            });

            // Stopping only matters when disabling a running service; a manual or automatic
            // service may keep running.
            var wasRunning = _target == ServiceStartupType.Disabled
                && status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
            if (wasRunning)
                _stop(_serviceName);

            (bool Success, string Error) result;
            try
            {
                result = _configure(_serviceName, _target);
            }
            catch (Exception ex)
            {
                Log.Error($"Configuring service '{_serviceName}' failed", ex);
                result = (false, ex.Message);
            }
            if (result.Success)
                return TweakResult.Ok;

            // The start type did not change. If we stopped a running service, put it back; a
            // restart we cannot perform is reported together with the config failure, and the
            // saved original stays so a later revert can still fix the start type.
            if (wasRunning)
            {
                if (_start(_serviceName))
                {
                    if (added && _query(_serviceName) == before)
                        _backup.RemoveOne(Id, ScopeType, _serviceName, StartTypeKey);

                    return TweakResult.Fail(result.Error);
                }

                return TweakResult.Fail($"{result.Error} The service could not be restarted. The original start type stays backed up.");
            }

            if (added && _query(_serviceName) == before)
                _backup.RemoveOne(Id, ScopeType, _serviceName, StartTypeKey);

            return TweakResult.Fail(result.Error);
        }
        catch (Exception ex)
        {
            Log.Error($"Apply of service tweak '{Id}' failed", ex);
            return TweakResult.Fail(ex.Message);
        }
    }

    public override TweakResult Revert()
    {
        try
        {
            // The saved original is authoritative, even when it equals the value the tweak sets
            // (some services ship disabled). A hardcoded fallback is only used when nothing was
            // saved, and then only for a service still sitting at this tweak's target: anything
            // else is not Stratus's change, and a fallback written without a backup could
            // replace an unrelated start type with no record.
            var saved = _backup.GetLatest(Id, ScopeType, _serviceName, StartTypeKey);
            if (!TryChooseRevertTarget(saved, _revertFallback, out var target))
                return TweakResult.Fail($"The saved start type for {_serviceName} could not be read. Nothing was changed and the backup was kept.");

            if (saved is null)
            {
                var current = _query(_serviceName);
                if (current is null)
                    return TweakResult.Fail($"The start type of {_serviceName} could not be read. Nothing was changed.");

                if (!MatchesTarget(current.Value))
                    return TweakResult.Ok;
            }

            var result = _configure(_serviceName, target);
            if (!result.Success)
            {
                // Keep the backup entries so a later revert can try the original again.
                return TweakResult.Fail($"{result.Error}. The original start type stays backed up.");
            }

            _backup.Remove(Id);
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Error($"Revert of service tweak '{Id}' failed", ex);
            return TweakResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Picks the start type a revert writes. A saved original always wins, including one that
    /// equals the target; the factory fallback only covers a service with no saved original.
    /// False means the saved value exists but cannot be read, and the caller must fail.
    /// </summary>
    internal static bool TryChooseRevertTarget(BackupEntry? saved, ServiceStartupType fallback, out ServiceStartupType target)
    {
        if (saved is null)
        {
            target = fallback;
            return true;
        }

        target = fallback;
        return saved.ValueJson is { } text && TryMapFromString(text, out target);
    }

    private static string MapToString(ServiceStartMode m) => m switch
    {
        ServiceStartMode.Automatic => nameof(ServiceStartupType.Automatic),
        ServiceStartMode.Manual => nameof(ServiceStartupType.Manual),
        ServiceStartMode.Disabled => nameof(ServiceStartupType.Disabled),
        _ => nameof(ServiceStartupType.Manual)
    };

    private static bool TryMapFromString(string s, out ServiceStartupType t) =>
        Enum.TryParse(s, ignoreCase: true, out t);
}
