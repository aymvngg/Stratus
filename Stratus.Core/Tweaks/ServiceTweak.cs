using System.ServiceProcess;
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

/// <summary>Sets a Windows service startup type (disable, manual, automatic); reverts to the startup type it had before (backed up) or a sane fallback.</summary>
public sealed class ServiceTweak : Tweak
{
    public const string ScopeType = "service";

    private readonly string _serviceName;
    private readonly ServiceStartupType _target;
    private readonly ServiceStartupType _revertFallback;
    private readonly IBackupStore _backup;

    public ServiceTweak(
        string id, string name, string description,
        string serviceName, ServiceStartupType target, ServiceStartupType revertFallback,
        IBackupStore backup,
        int weight = 1, RiskLevel risk = RiskLevel.Recommended)
        : base(id, name, description, TweakCategory.Services)
    {
        _serviceName = serviceName;
        _target = target;
        _revertFallback = revertFallback;
        _backup = backup;
        Weight = weight;
        Risk = risk;
    }

    private static ServiceStartMode? QueryStartMode(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.StartType;
        }
        catch
        {
            return null;
        }
    }

    public override bool IsApplicable => QueryStartMode(_serviceName) is not null;

    // Automatic (Delayed Start) reports as plain Automatic through ServiceController.
    private bool MatchesTarget(ServiceStartMode mode) => _target switch
    {
        ServiceStartupType.Disabled => mode == ServiceStartMode.Disabled,
        ServiceStartupType.Manual => mode == ServiceStartMode.Manual,
        ServiceStartupType.Automatic or ServiceStartupType.AutomaticDelayed => mode == ServiceStartMode.Automatic,
        _ => false
    };

    public override bool IsApplied => QueryStartMode(_serviceName) is { } mode && MatchesTarget(mode);

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
            var current = QueryStartMode(_serviceName);
            // First writer wins: if a backup for this service already exists (previous apply,
            // or a failed revert that kept its entries), it still holds the untouched original.
            // Overwriting it here would replace the original with already-tweaked state.
            if (current is { } mode
                && _backup.GetLatest(Id, ScopeType, _serviceName, "StartType") is null)
            {
                _backup.Append(new BackupEntry
                {
                    TweakId = Id,
                    ScopeType = ScopeType,
                    ScopeId = _serviceName,
                    Key = "StartType",
                    ValueKind = null,
                    ValueJson = MapToString(mode),
                    Existed = true
                });
            }

            // Stopping only matters when disabling; a manual or automatic service may keep running.
            if (_target == ServiceStartupType.Disabled)
            {
                try
                {
                    using var sc = new ServiceController(_serviceName);
                    if (sc.CanStop && sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                    {
                        sc.Stop();
                        try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8)); } catch { /* timed out, continue */ }
                    }
                }
                catch { /* stop is best-effort */ }
            }

            var result = ProcessRunner.Run("sc.exe", $"config {_serviceName} start= {ScKeyword(_target)}");
            return result.Success
                ? TweakResult.Ok
                : TweakResult.Fail($"sc.exe exited with {result.ExitCode}: {result.StdErr.Trim()}");
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }

    public override TweakResult Revert()
    {
        try
        {
            var target = _revertFallback;
            var saved = _backup.GetLatest(Id, ScopeType, _serviceName, "StartType");
            if (saved?.ValueJson is { } savedMode && TryMapFromString(savedMode, out var parsed))
            {
                // Restoring the value the tweak itself sets would be a no-op revert.
                if (parsed != _target)
                    target = parsed;
            }
            var result = ProcessRunner.Run("sc.exe", $"config {_serviceName} start= {ScKeyword(target)}");
            if (!result.Success)
            {
                // Keep the backup entries so a later revert can try the original again.
                return TweakResult.Fail($"sc.exe exited with {result.ExitCode}: {result.StdErr.Trim()}. The original start type stays backed up.");
            }

            _backup.Remove(Id);
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }

    private static string MapToString(ServiceStartMode m) => m switch
    {
        ServiceStartMode.Automatic => nameof(ServiceStartupType.Automatic),
        ServiceStartMode.Manual => nameof(ServiceStartupType.Manual),
        ServiceStartMode.Disabled => nameof(ServiceStartupType.Disabled),
        _ => nameof(ServiceStartupType.Manual)
    };

    // Automatic (Delayed Start) reports as Automatic via ServiceController; "delayed-auto"
    // restores are handled by the sc.exe keyword - close enough for our set of services.
    private static bool TryMapFromString(string s, out ServiceStartupType t) =>
        Enum.TryParse(s, ignoreCase: true, out t);
}
