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

/// <summary>Disables a Windows service; reverts to the startup type it had before (backed up) or a sane default.</summary>
public sealed class ServiceTweak : Tweak
{
    public const string ScopeType = "service";

    private readonly string _serviceName;
    private readonly ServiceStartupType _defaultStartup;
    private readonly IBackupStore _backup;

    public ServiceTweak(
        string id, string name, string description,
        string serviceName, ServiceStartupType defaultStartup,
        IBackupStore backup,
        int weight = 1, RiskLevel risk = RiskLevel.Recommended)
        : base(id, name, description, TweakCategory.Services)
    {
        _serviceName = serviceName;
        _defaultStartup = defaultStartup;
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

    public override bool IsApplicable => ServiceExists;

    private bool ServiceExists => QueryStartMode(_serviceName) is not null;

    public override bool IsApplied => QueryStartMode(_serviceName) == ServiceStartMode.Disabled;

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
            if (current is { } mode)
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

            // Stop if running (best effort - some services refuse, disabling still works).
            try
            {
                using var sc = new ServiceController(_serviceName);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                {
                    if (sc.Status == ServiceControllerStatus.StartPending && sc.CanStop)
                        sc.Stop();
                    else if (sc.CanStop)
                    {
                        sc.Stop();
                        try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8)); } catch { /* timed out, continue */ }
                    }
                }
            }
            catch { /* stop is best-effort */ }

            var result = ProcessRunner.Run("sc.exe", $"config {_serviceName} start= {ScKeyword(ServiceStartupType.Disabled)}");
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
            var target = _defaultStartup;
            var saved = _backup.GetLatest(Id, ScopeType, _serviceName, "StartType");
            if (saved?.ValueJson is { } savedMode && TryMapFromString(savedMode, out var parsed))
            {
                // Never restore to Disabled - that would be a no-op revert.
                if (parsed != ServiceStartupType.Disabled)
                    target = parsed;
            }
            _backup.Remove(Id);

            var result = ProcessRunner.Run("sc.exe", $"config {_serviceName} start= {ScKeyword(target)}");
            return result.Success
                ? TweakResult.Ok
                : TweakResult.Fail($"sc.exe exited with {result.ExitCode}: {result.StdErr.Trim()}");
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
