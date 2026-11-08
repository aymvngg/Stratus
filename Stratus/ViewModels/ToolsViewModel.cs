using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Coordination;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

public sealed class RestorePointRow : ObservableObject
{
    public RestorePointRow(RestorePointItem item) => Item = item;

    public RestorePointItem Item { get; }

    public string Description => string.IsNullOrWhiteSpace(Item.Description) ? "Restore point" : Item.Description;

    public string CreatedText =>
        Item.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.InvariantCulture);

    public string TypeText => Item.Type switch
    {
        "0" => "App install",
        "1" => "App uninstall",
        "10" => "Driver install",
        "12" => "Settings change",
        "13" => "Cancelled",
        _ => "System",
    };

    public string MetaText => $"{CreatedText} · {TypeText}";
}

public sealed class TcpRow : ObservableObject
{
    public TcpRow(TcpSettingItem item)
    {
        Item = item;
        _selectedValue = item.CurrentValue;
    }

    public TcpSettingItem Item { get; }

    public string Name => Item.Name;
    public string Description => Item.Description;
    public string[] Values => Item.Values;
    public string CurrentText => Item.CurrentValue;
    public string DefaultText => $"Default: {Item.DefaultValue}";
    public bool IsDefault => string.Equals(Item.CurrentValue, Item.DefaultValue, StringComparison.OrdinalIgnoreCase);

    private string _selectedValue;
    public string SelectedValue { get => _selectedValue; set => Set(ref _selectedValue, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanInteract));
        }
    }

    public bool CanInteract => !IsBusy;
}

public sealed class ToolsViewModel : ObservableObject
{
    public ObservableCollection<PowerPlan> Plans { get; } = new();
    public ObservableCollection<string> Adapters { get; } = new();

    private PowerPlan? _selectedPlan;
    private DnsProfile? _selectedDns;
    private DnsProfile? _currentDns;
    private string _activePlanText = "…";
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;

    public PowerPlan? SelectedPlan
    {
        get => _selectedPlan;
        set => Set(ref _selectedPlan, value);
    }

    public DnsProfile? SelectedDns
    {
        get => _selectedDns;
        set => Set(ref _selectedDns, value);
    }

    public DnsProfile? CurrentDns
    {
        get => _currentDns;
        private set
        {
            if (Set(ref _currentDns, value))
                Raise(nameof(CurrentDnsLabel));
        }
    }

    /// <summary>Text for the "Active:" line under the resolver picker.</summary>
    public string CurrentDnsLabel => CurrentDns?.Label ?? "Automatic (from router)";

    /// <summary>Presets plus the custom entry when the adapters use servers that are not a preset.</summary>
    public ObservableCollection<DnsProfile> DnsProfiles { get; } = new();

    public string ActivePlanText { get => _activePlanText; private set => Set(ref _activePlanText, value); }
    public string AdaptersText => Adapters.Count == 0 ? "no connected physical adapters found" : string.Join(", ", Adapters);

    // ---- R3: health scans, resets, DNS benchmark, report ----
    public ObservableCollection<DnsBenchmarkResult> DnsResults { get; } = new();

    private string _scanTitle = "";
    private string _scanSummary = "";
    private string _scanDetail = "";
    private bool _hasScan;

    public string ScanTitle { get => _scanTitle; private set => Set(ref _scanTitle, value); }
    public string ScanSummary { get => _scanSummary; private set => Set(ref _scanSummary, value); }
    public string ScanDetail { get => _scanDetail; private set => Set(ref _scanDetail, value); }
    public bool HasScan { get => _hasScan; private set => Set(ref _hasScan, value); }
    public bool HasDnsResults => DnsResults.Count > 0;

    // ---- restore points ----
    public ObservableCollection<RestorePointRow> RestorePoints { get; } = new();

    private string _restoreStorageText = "Reading restore points…";
    private bool _restoreAvailable = true;

    public string RestoreStorageText { get => _restoreStorageText; private set => Set(ref _restoreStorageText, value); }

    public bool RestoreAvailable
    {
        get => _restoreAvailable;
        private set
        {
            if (Set(ref _restoreAvailable, value))
                Raise(nameof(RestoreEmptyText), nameof(HasRestorePoints));
        }
    }

    public bool HasRestorePoints => RestorePoints.Count > 0;

    public string RestoreEmptyText => RestoreAvailable
        ? "No restore points yet. Create one before big changes."
        : "System Restore is turned off on this PC. Creating a point turns it on for the system drive.";

    // ---- Windows Update pause ----
    private bool _updatePaused;
    private string _updatePauseText = "Reading the update pause state…";

    public bool UpdatePaused
    {
        get => _updatePaused;
        private set
        {
            if (Set(ref _updatePaused, value))
                Raise(nameof(UpdatePauseButtonText), nameof(UpdatePauseHint));
        }
    }

    public string UpdatePauseText { get => _updatePauseText; private set => Set(ref _updatePauseText, value); }

    public string UpdatePauseButtonText => UpdatePaused ? "Resume updates" : "Pause for 7 days";

    public string UpdatePauseHint => UpdatePaused
        ? "Windows Update installs nothing until the pause expires. Resume to let it check again right away."
        : "Pausing holds feature, quality and driver updates for a week. Security updates resume automatically when the pause ends.";

    // ---- TCP settings ----
    public ObservableCollection<TcpRow> TcpSettings { get; } = new();

    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) Raise(nameof(CanCheckForUpdates)); } }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    public async Task<bool> LoadAsync()
    {
        if (!TryBeginLoad())
        {
            ShowStatus("Another action is still running. Try again when it finishes.", InfoBarSeverity.Warning);
            return false;
        }

        BusyText = "Reading system state…";
        try
        {
            var plans = await Task.Run(() => AppServices.Power.ListPlans());
            var adapters = await Task.Run(() => AppServices.Dns.GetConnectedAdapters());
            var activeDns = await Task.Run(() => AppServices.Dns.GetActiveProfile());

            Plans.Clear();
            foreach (var plan in plans)
                Plans.Add(plan);

            Adapters.Clear();
            foreach (var adapter in adapters)
                Adapters.Add(adapter);

            // Restore the resolver picker from the DNS the adapters actually use. Without this
            // the ComboBox stayed blank after every restart (SelectedDns was never set on load).
            DnsProfiles.Clear();
            foreach (var profile in DnsService.Profiles)
                DnsProfiles.Add(profile);
            if (activeDns is not null && !DnsProfiles.Contains(activeDns))
                DnsProfiles.Insert(1, activeDns);

            SelectedDns = activeDns ?? DnsProfiles[0];
            CurrentDns = activeDns;

            var active = plans.FirstOrDefault(p => p.IsActive);
            SelectedPlan = active ?? plans.FirstOrDefault();
            ActivePlanText = active is null ? "Unknown" : $"{active.Name} ({active.Guid:B})";
            Raise(nameof(AdaptersText));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Tools page load failed: " + ex.Message);
            ShowStatus($"Could not read the system state: {ex.Message}", InfoBarSeverity.Warning);
            return false;
        }
        finally
        {
            EndLoad();
        }
    }

    /// <summary>
    /// A reload must never clear the busy indicator of a long action (DNS benchmark, scan,
    /// revert all) that owns the page. The reload is skipped while IsBusy is set.
    /// </summary>
    private bool TryBeginLoad()
    {
        if (IsBusy)
            return false;

        IsBusy = true;
        return true;
    }

    private void EndLoad()
    {
        IsBusy = false;
        BusyText = "";
    }

    public async Task<OperationOutcome> ApplyPlanAsync()
    {
        // Read the picker before the await: the user may change the selection while powercfg runs.
        var plan = SelectedPlan;
        if (plan is null)
            return OperationOutcome.Info("Pick a plan first.");

        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Switching power plan…";
        try
        {
            var ok = await Task.Run(() => AppServices.Power.SetActive(plan.Guid));
            if (!ok)
                return OperationOutcome.Error("powercfg failed to switch the plan.");

            ActivePlanText = $"{plan.Name} ({plan.Guid:B})";
            // The power-ultimate tweak's state depends on the active plan: rescore through the shared hub.
            await AppServices.RefreshFromEngineAsync();
            return OperationOutcome.Success($"Active plan: {plan.Name}.");
        }
        catch (Exception ex)
        {
            Log.Error("Switching the power plan failed", ex);
            return OperationOutcome.Error($"Could not switch the plan: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    public async Task<OperationOutcome> ApplyDnsAsync()
    {
        // Same reason as the plan: capture the chosen resolver so a later selection cannot
        // change what this call reports as applied.
        var profile = SelectedDns;
        if (profile is null)
            return OperationOutcome.Info("Pick a DNS provider first.");

        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Applying DNS…";
        try
        {
            var (ok, message) = await Task.Run(() => AppServices.Dns.Apply(profile));
            if (ok)
                CurrentDns = profile;

            return ok ? OperationOutcome.Success(message) : OperationOutcome.Error(message);
        }
        catch (Exception ex)
        {
            Log.Error("Applying DNS failed", ex);
            return OperationOutcome.Error($"Could not apply DNS: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    public async Task<OperationOutcome> CreateRestorePointAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Creating restore point (can take a minute)…";
        try
        {
            var (ok, message) = await Task.Run(() => RestorePointService.Create(AppServices.Registry));
            if (!ok)
                return OperationOutcome.Error($"Restore point failed: {message}");

            await LoadRestorePointsAsync();
            return OperationOutcome.Success("Restore point created.");
        }
        catch (Exception ex)
        {
            Log.Error("Creating a restore point failed", ex);
            return OperationOutcome.Error($"Restore point failed: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    // ================= restore points, update pause, TCP =================

    /// <summary>Loads the three cards that read their own state. Each one catches its own failure.</summary>
    public async Task LoadExtrasAsync()
    {
        await Task.WhenAll(LoadRestorePointsAsync(), LoadUpdatePauseAsync(), LoadTcpAsync());
    }

    public async Task LoadRestorePointsAsync()
    {
        try
        {
            var list = await Task.Run(RestorePointService.Scan);

            RestorePoints.Clear();
            foreach (var point in list.Points)
                RestorePoints.Add(new RestorePointRow(point));

            RestoreAvailable = list.Available;
            RestoreStorageText = list.UsedBytes > 0
                ? $"{Format.Bytes(list.UsedBytes)} used by System Restore ({Format.Bytes(list.AllocatedBytes)} allocated)"
                : list.Available
                    ? "No space used by System Restore yet"
                    : "System Restore is turned off";

            Raise(nameof(HasRestorePoints), nameof(RestoreEmptyText));
        }
        catch (Exception ex)
        {
            Log.Warn("Restore point scan failed: " + ex.Message);
            RestoreStorageText = "Could not read the restore points.";
        }
    }

    public async Task<OperationOutcome> DeleteRestorePointAsync(RestorePointRow row)
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Deleting the restore point…";
        try
        {
            var (ok, message) = await Task.Run(() => RestorePointService.Delete(row.Item.Sequence));
            if (!ok)
                return OperationOutcome.Error($"Could not delete the restore point: {message}");

            await LoadRestorePointsAsync();
            return OperationOutcome.Success("Restore point deleted.");
        }
        catch (Exception ex)
        {
            Log.Error("Deleting a restore point failed", ex);
            return OperationOutcome.Error($"Could not delete the restore point: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    public async Task LoadUpdatePauseAsync()
    {
        try
        {
            ApplyPauseState(await Task.Run(UpdatePauseService.Read));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the Windows Update pause state: " + ex.Message);
            UpdatePauseText = "Could not read the update pause state.";
        }
    }

    private void ApplyPauseState(UpdatePauseState state)
    {
        UpdatePaused = state.IsPaused;
        UpdatePauseText = state.IsPaused && state.UntilUtc is { } until
            ? $"Paused until {until.ToLocalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.InvariantCulture)}."
            : "Updates are active. Windows checks on its own schedule.";
    }

    public async Task<OperationOutcome> ToggleUpdatePauseAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = UpdatePaused ? "Resuming Windows Update…" : "Pausing Windows Update…";
        try
        {
            var paused = UpdatePaused;
            var (ok, message) = await Task.Run(() =>
                paused
                    ? UpdatePauseService.Resume(AppServices.Registry)
                    : UpdatePauseService.Pause(7, AppServices.Registry));

            if (!ok)
                return OperationOutcome.Error($"Could not change the update pause: {message}");

            // The mutation is committed here. A failed state readback must not turn it into a
            // failure, so it gets its own catch and only degrades the outcome to a warning.
            try
            {
                ApplyPauseState(await Task.Run(UpdatePauseService.Read));
                return OperationOutcome.Success(message);
            }
            catch (Exception readEx)
            {
                Log.Warn("The update pause state could not be re-read after the change: " + readEx.Message);
                return OperationOutcome.Warning(
                    message + " The change was applied, but the new state could not be confirmed.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Changing the Windows Update pause failed", ex);
            return OperationOutcome.Error($"Could not change the update pause: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    public async Task LoadTcpAsync()
    {
        try
        {
            var items = await Task.Run(TcpSettingsService.Scan);

            TcpSettings.Clear();
            foreach (var item in items)
                TcpSettings.Add(new TcpRow(item));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the TCP settings: " + ex.Message);
        }
    }

    public async Task<OperationOutcome> ApplyTcpAsync(TcpRow row)
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        row.IsBusy = true;
        var item = row.Item;
        var selectedValue = row.SelectedValue;
        BusyText = $"Applying {row.Name.ToLowerInvariant()}...";
        try
        {
            var (ok, message) = await Task.Run(() => TcpSettingsService.Apply(item, selectedValue));
            if (!ok)
                return OperationOutcome.Error($"Could not apply the TCP setting: {message}");

            await LoadTcpAsync();
            return OperationOutcome.Success(message);
        }
        catch (Exception ex)
        {
            Log.Error("Applying a TCP setting failed", ex);
            return OperationOutcome.Error($"Could not apply the TCP setting: {ex.Message}");
        }
        finally
        {
            row.IsBusy = false;
            EndLoad();
        }
    }

    public async Task<OperationOutcome> ResetTcpAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Restoring the default TCP settings…";
        try
        {
            var (ok, message) = await Task.Run(TcpSettingsService.ResetAll);
            await LoadTcpAsync();
            return ok
                ? OperationOutcome.Success(message)
                : OperationOutcome.Error($"Could not reset every TCP setting: {message}");
        }
        catch (Exception ex)
        {
            Log.Error("Resetting the TCP settings failed", ex);
            return OperationOutcome.Error($"Could not reset the TCP settings: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    /// <summary>Runs DISM StartComponentCleanup: removes superseded component versions. Can take 10-30 minutes.</summary>
    public async Task<OperationOutcome> RunComponentCleanupAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Running component store cleanup (can take 10-30 minutes)…";
        try
        {
            var result = await Task.Run(() =>
                ProcessRunner.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 30 * 60_000));

            if (result.Success)
                return OperationOutcome.Success("Component store cleanup finished.");

            var detail = (result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
            detail = detail.Replace("\r", "").Replace("\n", " ").Trim();
            if (detail.Length > 220)
                detail = detail[..220] + "…";
            return OperationOutcome.Error($"Component cleanup failed (exit {result.ExitCode}): {detail}");
        }
        catch (Exception ex)
        {
            Log.Error("Component store cleanup failed", ex);
            return OperationOutcome.Error($"Component cleanup failed: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    /// <summary>Reverts every applied tweak back to its default (Windows) state.</summary>
    public async Task<OperationOutcome> RevertAllAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        if (!AppServices.TweakWrites.TryEnter())
            return OperationOutcome.Warning("The tweak list is being changed on another page.");

        IsBusy = true;
        BusyText = "Reverting tweaks…";
        AppServices.StateRevision.Advance();
        try
        {
            // Probing IsApplied hits the registry and services: keep it off the UI thread.
            var toRevert = await Task.Run(() => AppServices.Engine.Tweaks
                .Where(t => Safe(() => t.IsApplicable, false) && Safe(() => t.IsApplied, false))
                .ToList());

            var reverted = 0;
            var failed = new List<string>();

            // One bulk scope for the whole batch: the engine coalesces StateChanged, so the
            // score is recomputed once at the end instead of once per tweak.
            using (AppServices.Engine.BeginBulk())
            {
                foreach (var tweak in toRevert)
                {
                    var result = await AppServices.Engine.RevertAsync(tweak);
                    if (result.Success)
                        reverted++;
                    else
                        failed.Add($"{tweak.Id}: {result.Error}");
                }
            }

            await AppServices.RefreshFromEngineAsync();

            if (failed.Count == 0)
                return OperationOutcome.Success($"Reverted {Format.Count(reverted, "tweak")} to default settings.");

            return OperationOutcome.Warning($"Reverted {reverted}, {failed.Count} failed - {string.Join("; ", failed.Take(3))}");
        }
        catch (Exception ex)
        {
            Log.Error("Revert all failed", ex);
            return OperationOutcome.Error($"Could not revert the tweaks: {ex.Message}");
        }
        finally
        {
            EndLoad();
            AppServices.TweakWrites.Exit();
        }
    }

    // ================= R3: health scans =================

    public Task<OperationOutcome> RunSfcAsync() => RunScanAsync(HealthScanService.RunSfc, "Scanning system files with SFC (can take 10-30 minutes)...");

    public Task<OperationOutcome> RunDismScanAsync() => RunScanAsync(HealthScanService.RunDismScan, "Scanning the component store with DISM (can take 10-30 minutes)...");

    public Task<OperationOutcome> RunDismRestoreAsync() => RunScanAsync(HealthScanService.RunDismRestore, "Repairing the component store with DISM (can take 30-60 minutes)...");

    private async Task<OperationOutcome> RunScanAsync(Func<HealthScanResult> scan, string busyText)
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = busyText;
        try
        {
            var result = await Task.Run(scan);
            ScanTitle = result.Title;
            ScanSummary = result.Summary;
            var detail = result.Detail;
            if (detail.Length > 12_000)
                detail = "… earlier output truncated, the full text is in the Stratus log." +
                         Environment.NewLine + detail[^12_000..];
            ScanDetail = detail;
            HasScan = true;

            var minutes = result.Duration.TotalMinutes >= 1
                ? $"{result.Duration.TotalMinutes:F0} min"
                : $"{result.Duration.TotalSeconds:F0} s";
            // The scan ran to completion: findings live in the scan card, so the status is a success.
            return OperationOutcome.Success($"{result.Title}: {result.Summary} ({minutes})");
        }
        catch (Exception ex)
        {
            Log.Error("Health scan failed", ex);
            return OperationOutcome.Error($"Scan failed: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    // ================= R3: resets =================

    public async Task<OperationOutcome> ResetWindowsUpdateAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Resetting the Windows Update cache…";
        try
        {
            var (ok, message) = await Task.Run(UpdateResetService.Reset);
            if (ok)
                return OperationOutcome.Success(message);

            return message.StartsWith("Nothing to reset", StringComparison.OrdinalIgnoreCase)
                ? OperationOutcome.Warning(message)
                : OperationOutcome.Error(message);
        }
        catch (Exception ex)
        {
            Log.Error("Windows Update reset failed", ex);
            return OperationOutcome.Error($"Could not reset the Windows Update cache: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    public async Task<OperationOutcome> ResetNetworkAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Resetting the network stack…";
        try
        {
            var (ok, message) = await Task.Run(NetworkResetService.Reset);
            return ok ? OperationOutcome.Success(message) : OperationOutcome.Warning(message);
        }
        catch (Exception ex)
        {
            Log.Error("Network reset failed", ex);
            return OperationOutcome.Error($"Could not reset the network stack: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    // ================= v1.5: fixes and Windows tools =================

    /// <summary>
    /// Points the Windows time service at pool.ntp.org and forces a resync.
    /// The resync is allowed to fail softly: a freshly restarted w32time sometimes needs a moment.
    /// </summary>
    public async Task<(bool Ok, string Message)> FixNtpAsync()
    {
        if (IsBusy)
            return (false, "Another action is already running.");

        IsBusy = true;
        BusyText = "Pointing the time service at pool.ntp.org...";
        try
        {
            var config = await Task.Run(() => ProcessRunner.Run("w32tm.exe",
                "/config /syncfromflags:manual /manualpeerlist:\"pool.ntp.org,0x8\" /update", 30000));
            if (!config.Success)
                return (false, "Could not point Windows time at pool.ntp.org: " + ProcessError(config));

            var restart = await Task.Run(() =>
                ProcessRunner.PowerShell("Restart-Service w32time -Force -ErrorAction Stop", 60000));
            if (!restart.Success)
                return (false, "The time service could not be restarted: " + ProcessError(restart));

            var message = "Time sync now uses pool.ntp.org.";
            var resync = await Task.Run(() => ProcessRunner.Run("w32tm.exe", "/resync /force", 30000));
            if (!resync.Success)
                message += " The first resync failed, Windows will retry on its own schedule: " + ProcessError(resync);

            Log.Info("Time sync switched to pool.ntp.org.");
            return (true, message);
        }
        catch (Exception ex)
        {
            Log.Error("Fixing the time sync failed", ex);
            return (false, $"Could not fix the time sync: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    /// <summary>Points the Windows time service back at its default server (time.windows.com).</summary>
    public async Task<(bool Ok, string Message)> RestoreNtpAsync()
    {
        if (IsBusy)
            return (false, "Another action is already running.");

        IsBusy = true;
        BusyText = "Restoring the Windows default time server...";
        try
        {
            var config = await Task.Run(() => ProcessRunner.Run("w32tm.exe",
                "/config /syncfromflags:manual /manualpeerlist:\"time.windows.com,0x9\" /update", 30000));
            if (!config.Success)
                return (false, "Could not point Windows time at time.windows.com: " + ProcessError(config));

            var restart = await Task.Run(() =>
                ProcessRunner.PowerShell("Restart-Service w32time -Force -ErrorAction Stop", 60000));
            if (!restart.Success)
                return (false, "The time service could not be restarted: " + ProcessError(restart));

            Log.Info("Time sync restored to time.windows.com.");
            return (true, "Windows time sync is back on the Windows default server.");
        }
        catch (Exception ex)
        {
            Log.Error("Restoring the time sync failed", ex);
            return (false, $"Could not restore the time sync: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    /// <summary>Resets and updates the winget sources (fixes most "winget is broken" states).</summary>
    public async Task<(bool Ok, string Message)> RepairWingetAsync()
    {
        if (IsBusy)
            return (false, "Another action is already running.");

        IsBusy = true;
        BusyText = "Repairing the winget sources...";
        try
        {
            var version = await Task.Run(() => ProcessRunner.Run("winget.exe", "--version", 30000));
            if (!version.Success)
                return (false, "winget is not installed on this machine. Install App Installer from the Microsoft Store first.");

            var reset = await Task.Run(() => ProcessRunner.Run("winget.exe", "source reset --force", 120000));
            if (!reset.Success)
                return (false, "Resetting the winget sources failed: " + ProcessError(reset));

            var update = await Task.Run(() => ProcessRunner.Run("winget.exe", "source update", 120000));
            if (!update.Success)
                return (false, "Updating the winget sources failed: " + ProcessError(update));

            Log.Info("Winget sources reset and updated.");
            return (true, "The winget sources were reset and updated.");
        }
        catch (Exception ex)
        {
            Log.Error("Repairing winget failed", ex);
            return (false, $"Could not repair winget: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    private static string ProcessError(ProcessResult result)
    {
        var detail = result.StdErr.Length > 0 ? result.StdErr : result.StdOut;
        detail = detail.Replace("\r", "").Replace("\n", " ").Trim();
        return detail.Length > 220 ? detail[..220] + "..." : detail;
    }

    /// <summary>Static whitelist: the only Windows tools this page may launch by name.</summary>
    private static readonly string[] WindowsToolTargets =
    [
        "compmgmt.msc", "appwiz.cpl", "ncpa.cpl", "powercfg.cpl", "devmgmt.msc",
        "services.msc", "taskschd.msc", "control.exe", "optionalfeatures.exe",
        "firewall.cpl", "sysdm.cpl", "diskmgmt.msc", "eventvwr.msc", "msinfo32.exe",
        "timedate.cpl", "main.cpl", "joy.cpl", "desk.cpl",
    ];

    /// <summary>Opens a whitelisted Windows management console. Synchronous: Process.Start is instant.</summary>
    public (bool Ok, string Message) OpenWindowsTool(string target)
    {
        if (!WindowsToolTargets.Contains(target))
            return (false, "That Windows tool is not on the allowed list.");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return (true, "Opened " + target + ".");
        }
        catch (Exception ex)
        {
            Log.Warn("Opening " + target + " failed: " + ex.Message);
            return (false, "Could not open " + target + ": " + ex.Message);
        }
    }

    // ================= R3: DNS benchmark =================

    public async Task<OperationOutcome> BenchmarkDnsAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Measuring resolver latency…";
        try
        {
            var results = await Task.Run(() => DnsBenchmarkService.Run());
            DnsResults.Clear();
            foreach (var result in results)
                DnsResults.Add(result);
            Raise(nameof(HasDnsResults));

            if (DnsResults.Count == 0)
                return OperationOutcome.Warning("No resolvers to test.");

            var fastest = DnsResults.FirstOrDefault(r => r.Ok);
            return fastest is null
                ? OperationOutcome.Warning("Every resolver timed out. Check the network connection.")
                : OperationOutcome.Success($"Fastest resolver: {fastest.Label} at {fastest.LatencyText}.");
        }
        catch (Exception ex)
        {
            Log.Error("DNS benchmark failed", ex);
            return OperationOutcome.Error($"Benchmark failed: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    // ================= R3: system report =================

    public async Task<OperationOutcome> CreateReportAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        IsBusy = true;
        BusyText = "Writing the system report…";
        try
        {
            var path = await Task.Run(() =>
            {
                var report = AppServices.Engine.Rescore();
                var summary = new SystemSummary(
                    SystemInfoService.GetCpu(),
                    SystemInfoService.GetGpu(),
                    SystemInfoService.GetRam(),
                    SystemInfoService.GetOsDisplayName(),
                    SystemInfoService.GetOsBuild(),
                    SystemInfoService.IsElevated());
                return ReportService.Create(report, summary);
            });

            return OperationOutcome.Success($"System report saved to {path}.");
        }
        catch (Exception ex)
        {
            Log.Error("Writing the system report failed", ex);
            return OperationOutcome.Error($"Could not write the report: {ex.Message}");
        }
        finally
        {
            EndLoad();
        }
    }

    // ================= v1.4: updates =================

    public const string AutoCheckKey = "update.autoCheck";
    public const string LastCheckKey = "update.lastCheck";

    private bool _autoCheck = true;
    private UpdateCheck? _updateInfo;
    private bool _hasUpdate;
    private bool _isDownloading;
    private double _updateProgress;
    private bool _isUpdateReady;
    private string _updateStatusText = "";
    private string _updateNotes = "";
    private readonly OperationGate _updateCheck = new();

    /// <summary>Version of the running build, or "development build" when the CI tag was not stamped.</summary>
    public string CurrentVersionText =>
        UpdateService.CurrentVersion == "0.0.0" ? "development build"
        : !UpdateService.IsVelopackInstalled ? $"v{UpdateService.CurrentVersion} (portable)"
        : $"v{UpdateService.CurrentVersion}";

    public bool AutoCheck
    {
        get => _autoCheck;
        set
        {
            if (!Set(ref _autoCheck, value))
                return;
            AppServices.Settings.Set(AutoCheckKey, value ? "true" : "false");
            Raise(nameof(AutoCheckHint));
        }
    }

    public string AutoCheckHint => AutoCheck
        ? "Stratus checks GitHub once a day and downloads a newer build in the background."
        : "Automatic checks are off. Use the button to check by hand.";

    public bool HasUpdate { get => _hasUpdate; private set { if (Set(ref _hasUpdate, value)) Raise(nameof(UpdateTitleText)); } }
    public bool IsDownloading { get => _isDownloading; private set { if (Set(ref _isDownloading, value)) Raise(nameof(CanCheckForUpdates)); } }
    public bool IsUpdateReady { get => _isUpdateReady; private set { if (Set(ref _isUpdateReady, value)) Raise(nameof(UpdateButtonText), nameof(CanCheckForUpdates)); } }
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }
    public string UpdateNotes { get => _updateNotes; private set { if (Set(ref _updateNotes, value)) Raise(nameof(HasUpdateNotes), nameof(UpdateNotesText)); } }
    public bool HasUpdateNotes => !string.IsNullOrWhiteSpace(_updateNotes);

    /// <summary>Release notes as plain text: markdown markers read badly inside a TextBox.</summary>
    public string UpdateNotesText => FormatNotes(_updateNotes);

    private static string FormatNotes(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var builder = new System.Text.StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Headers: drop the hashes, keep the title and give it breathing room.
            if (line.StartsWith("### ") || line.StartsWith("## ") || line.StartsWith("# "))
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.AppendLine(line.TrimStart('#').Trim());
                continue;
            }

            if (line.StartsWith("- "))
                line = "\u2022 " + line[2..];

            // Links keep their text, emphasis and code markers go away.
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            line = line.Replace("**", "").Replace("`", "");

            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }

    public string UpdateTitleText =>
        _updateInfo is null ? "Updates" : $"Stratus {_updateInfo.Tag} is available";

    public string UpdateButtonText =>
        IsUpdateReady ? "Restart to update" : "Check for updates";

    /// <summary>
    /// The button stays enabled once an update is ready: it then reads "Restart to update".
    /// Disabling it on IsUpdateReady made the restart (and the pending-update recovery) unreachable.
    /// </summary>
    public bool CanCheckForUpdates => !IsBusy && !IsDownloading;

    /// <summary>Restores the persisted auto-check preference and any downloaded-but-unapplied update.</summary>
    public void LoadUpdateState()
    {
        AutoCheck = AppServices.Settings.Get(AutoCheckKey) is not { } value || value != "false";

        if (UpdateService.IsDevBuild)
        {
            UpdateStatusText = UpdateService.SelfUpdateBlockReason;
            return;
        }

        // Surface an update that was downloaded earlier but never applied (app was closed instead of restarting).
        if (UpdateService.PendingRestart is { } pending)
        {
            var version = pending.Version.ToString();
            _updateInfo = new UpdateCheck($"v{version}", version, "");
            HasUpdate = true;
            IsUpdateReady = true;
            UpdateStatusText = $"Stratus v{version} is downloaded. Restart to finish the update.";
        }
    }

    /// <summary>Silent startup check, throttled to once a day, followed by a background download.</summary>
    public async Task StartupCheckAsync()
    {
        if (!AutoCheck || UpdateService.IsDevBuild || HasUpdate || IsUpdateReady || IsBusy || IsDownloading)
            return;

        // The startup check and a manual check share one lifecycle gate: the two must never
        // run overlapping HTTP requests or two downloads.
        if (!_updateCheck.TryEnter())
            return;

        try
        {
            if (AppServices.Settings.Get(LastCheckKey) is { } stamp &&
                long.TryParse(stamp, out var ticks) &&
                DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromHours(24))
                return;

            var (info, _) = await UpdateService.CheckAsync();
            if (info is null)
                return; // Offline: do not start the 24 hour window, so the next launch retries.

            AppServices.Settings.Set(LastCheckKey, DateTime.UtcNow.Ticks.ToString());
            if (!UpdateService.IsNewer(info.Version))
                return;

            _updateInfo = info;
            UpdateNotes = info.Notes;
            HasUpdate = true;
            UpdateStatusText = $"Downloading {info.Tag}...";
            await DownloadUpdateAsync(info);
        }
        catch (Exception ex)
        {
            Log.Error("The startup update check failed", ex);
            UpdateStatusText = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            _updateCheck.Exit();
        }
    }

    /// <summary>Manual check. Returns the outcome for the InfoBar.</summary>
    public async Task<OperationOutcome> CheckForUpdatesAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is already running.");

        if (!TryBeginUpdateCheck())
            return OperationOutcome.Warning("An update check is already running.");

        BusyText = "Checking GitHub for a newer release...";
        try
        {
            var (info, error) = await UpdateService.CheckAsync();

            if (info is null)
            {
                UpdateStatusText = error ?? "Could not reach GitHub.";
                return OperationOutcome.Warning($"Update check failed: {UpdateStatusText}");
            }

            AppServices.Settings.Set(LastCheckKey, DateTime.UtcNow.Ticks.ToString());
            if (!UpdateService.IsNewer(info.Version))
            {
                HasUpdate = false;
                _updateInfo = null;
                UpdateNotes = "";
                UpdateStatusText = $"You are on the latest version ({info.Tag}).";
                return OperationOutcome.Success($"Stratus is up to date ({info.Tag}).");
            }

            _updateInfo = info;
            UpdateNotes = info.Notes;
            HasUpdate = true;

            if (UpdateService.IsDevBuild)
            {
                UpdateStatusText = UpdateService.SelfUpdateBlockReason;
                return OperationOutcome.Info($"Development build: {info.Tag} is available, updates are disabled.");
            }

            UpdateStatusText = $"Downloading {info.Tag}…";
            await DownloadUpdateAsync(info);

            return IsUpdateReady
                ? OperationOutcome.Success($"{info.Tag} is downloaded and ready to install.")
                : OperationOutcome.Warning($"Found {info.Tag} but the download failed: {UpdateStatusText}");
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            UpdateStatusText = $"Update check failed: {ex.Message}";
            return OperationOutcome.Error(UpdateStatusText);
        }
        finally
        {
            EndUpdateCheck();
        }
    }

    /// <summary>Claims the single update-check slot for a manual check, which also shows busy state.</summary>
    private bool TryBeginUpdateCheck()
    {
        if (!_updateCheck.TryEnter())
            return false;

        IsBusy = true;
        return true;
    }

    private void EndUpdateCheck()
    {
        IsBusy = false;
        BusyText = "";
        _updateCheck.Exit();
    }

    private async Task DownloadUpdateAsync(UpdateCheck info)
    {
        IsDownloading = true;
        UpdateProgress = 0;
        var progress = new Progress<double>(fraction => UpdateProgress = Math.Round(fraction * 100));

        try
        {
            var (ok, _, error) = await UpdateService.DownloadAsync(progress);
            if (!ok)
            {
                UpdateStatusText = error is null
                    ? "No update is available."
                    : $"Download failed: {error}";
                return;
            }

            IsUpdateReady = true;
            UpdateProgress = 100;
            UpdateStatusText = $"{info.Tag} is downloaded. Restart to finish the update.";
        }
        catch (Exception ex)
        {
            Log.Error("Update download failed", ex);
            UpdateStatusText = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Hands the swap to Velopack's Update.exe. The caller closes the app when this returns true.</summary>
    public bool RestartToUpdate(out string message)
    {
        if (!UpdateService.ApplyAndRestart(out var error))
        {
            message = $"Could not start the update: {error}";
            return false;
        }

        message = _updateInfo is null
            ? "Restarting to finish the update."
            : $"Restarting into {_updateInfo.Tag}.";
        return true;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
