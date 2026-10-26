using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

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
        private set => Set(ref _currentDns, value);
    }

    public IReadOnlyList<DnsProfile> DnsProfiles => DnsService.Profiles;

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

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
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

    public async Task LoadAsync()
    {
        IsBusy = true;
        BusyText = "Reading system state…";
        try
        {
            var plans = await Task.Run(() => AppServices.Power.ListPlans());
            var adapters = await Task.Run(() => AppServices.Dns.GetConnectedAdapters());

            Plans.Clear();
            foreach (var plan in plans)
                Plans.Add(plan);

            Adapters.Clear();
            foreach (var adapter in adapters)
                Adapters.Add(adapter);

            var active = plans.FirstOrDefault(p => p.IsActive);
            SelectedPlan = active ?? plans.FirstOrDefault();
            ActivePlanText = active is null ? "Unknown" : $"{active.Name} ({active.Guid:B})";
            Raise(nameof(AdaptersText));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> ApplyPlanAsync()
    {
        if (SelectedPlan is null)
            return "Pick a plan first.";

        IsBusy = true;
        BusyText = "Switching power plan…";
        try
        {
            var ok = await Task.Run(() => AppServices.Power.SetActive(SelectedPlan.Guid));
            if (!ok)
                return "powercfg failed to switch the plan.";

            ActivePlanText = $"{SelectedPlan.Name} ({SelectedPlan.Guid:B})";
            // The power-ultimate tweak's state depends on the active plan: rescore through the shared hub.
            await AppServices.RefreshFromEngineAsync();
            return $"Active plan: {SelectedPlan.Name}.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> ApplyDnsAsync()
    {
        if (SelectedDns is null)
            return "Pick a DNS provider first.";

        IsBusy = true;
        BusyText = "Applying DNS…";
        try
        {
            var (ok, message) = await Task.Run(() => AppServices.Dns.Apply(SelectedDns));
            if (ok)
                CurrentDns = SelectedDns;
            return message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> CreateRestorePointAsync()
    {
        IsBusy = true;
        BusyText = "Creating restore point (can take a minute)…";
        try
        {
            var (ok, message) = await Task.Run(() => RestorePointService.Create());
            return ok ? "Restore point created." : $"Restore point failed: {message}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>Runs DISM StartComponentCleanup: removes superseded component versions. Can take 10-30 minutes.</summary>
    public async Task<string> RunComponentCleanupAsync()
    {
        IsBusy = true;
        BusyText = "Running component store cleanup (can take 10-30 minutes)…";
        try
        {
            var result = await Task.Run(() =>
                ProcessRunner.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 30 * 60_000));

            if (result.Success)
                return "Component store cleanup finished.";

            var detail = (result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
            detail = detail.Replace("\r", "").Replace("\n", " ").Trim();
            if (detail.Length > 220)
                detail = detail[..220] + "…";
            return $"Component cleanup failed (exit {result.ExitCode}): {detail}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>Reverts every applied tweak back to its default (Windows) state.</summary>
    public async Task<string> RevertAllAsync()
    {
        IsBusy = true;
        BusyText = "Reverting tweaks…";
        try
        {
            var toRevert = AppServices.Engine.Tweaks
                .Where(t => Safe(() => t.IsApplicable, false) && Safe(() => t.IsApplied, false))
                .ToList();

            var reverted = 0;
            var failed = new List<string>();
            foreach (var tweak in toRevert)
            {
                // Coalesced: one rescoring pass for the whole batch, not one per tweak.
                using var _bulk = AppServices.Engine.BeginBulk();
                var result = await AppServices.Engine.RevertAsync(tweak);
                if (result.Success)
                    reverted++;
                else
                    failed.Add($"{tweak.Id}: {result.Error}");
            }

            await AppServices.RefreshFromEngineAsync();

            return failed.Count == 0
                ? $"Reverted {Format.Count(reverted, "tweak")} to default settings."
                : $"Reverted {reverted}, {failed.Count} failed - {string.Join("; ", failed.Take(3))}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    // ================= R3: health scans =================

    public Task<string> RunSfcAsync() => RunScanAsync(HealthScanService.RunSfc, "Scanning system files with SFC (can take 10-30 minutes)…");

    public Task<string> RunDismScanAsync() => RunScanAsync(HealthScanService.RunDismScan, "Scanning the component store with DISM (can take 10-30 minutes)…");

    public Task<string> RunDismRestoreAsync() => RunScanAsync(HealthScanService.RunDismRestore, "Repairing the component store with DISM (can take 30-60 minutes)…");

    private async Task<string> RunScanAsync(Func<HealthScanResult> scan, string busyText)
    {
        IsBusy = true;
        BusyText = busyText;
        try
        {
            var result = await Task.Run(scan);
            ScanTitle = result.Title;
            ScanSummary = result.Summary;
            var detail = result.Detail;
            if (detail.Length > 12_000)
                detail = detail[..12_000] + Environment.NewLine + "… output truncated, full text is in the Stratus log.";
            ScanDetail = detail;
            HasScan = true;

            var minutes = result.Duration.TotalMinutes >= 1
                ? $"{result.Duration.TotalMinutes:F0} min"
                : $"{result.Duration.TotalSeconds:F0} s";
            return $"{result.Title}: {result.Summary} ({minutes})";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    // ================= R3: resets =================

    public async Task<string> ResetWindowsUpdateAsync()
    {
        IsBusy = true;
        BusyText = "Resetting the Windows Update cache…";
        try
        {
            var (ok, message) = await Task.Run(UpdateResetService.Reset);
            return ok ? message : message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> ResetNetworkAsync()
    {
        IsBusy = true;
        BusyText = "Resetting the network stack…";
        try
        {
            var (ok, message) = await Task.Run(NetworkResetService.Reset);
            return message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    // ================= R3: DNS benchmark =================

    public async Task<string> BenchmarkDnsAsync()
    {
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
                return "No resolvers to test.";

            var fastest = DnsResults.FirstOrDefault(r => r.Ok);
            return fastest is null
                ? "Every resolver timed out. Check the network connection."
                : $"Fastest resolver: {fastest.Label} at {fastest.LatencyText}.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    // ================= R3: system report =================

    public async Task<string> CreateReportAsync()
    {
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

            return $"System report saved to {path}.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
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
    public bool IsDownloading { get => _isDownloading; private set => Set(ref _isDownloading, value); }
    public bool IsUpdateReady { get => _isUpdateReady; private set { if (Set(ref _isUpdateReady, value)) Raise(nameof(UpdateButtonText), nameof(CanCheckForUpdates)); } }
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }
    public string UpdateNotes { get => _updateNotes; private set { if (Set(ref _updateNotes, value)) Raise(nameof(HasUpdateNotes)); } }
    public bool HasUpdateNotes => !string.IsNullOrWhiteSpace(_updateNotes);

    public string UpdateTitleText =>
        _updateInfo is null ? "Updates" : $"Stratus {_updateInfo.Tag} is available";

    public string UpdateButtonText =>
        IsUpdateReady ? "Restart to update" : "Check for updates";

    public bool CanCheckForUpdates => !IsBusy && !IsDownloading && !IsUpdateReady;

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
        if (!AutoCheck || UpdateService.IsDevBuild || HasUpdate || IsUpdateReady)
            return;

        if (AppServices.Settings.Get(LastCheckKey) is { } stamp &&
            long.TryParse(stamp, out var ticks) &&
            DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromHours(24))
            return;

        AppServices.Settings.Set(LastCheckKey, DateTime.UtcNow.Ticks.ToString());

        var (info, _) = await UpdateService.CheckAsync();
        if (info is null || !UpdateService.IsNewer(info.Version))
            return;

        _updateInfo = info;
        UpdateNotes = info.Notes;
        HasUpdate = true;
        UpdateStatusText = $"Downloading {info.Tag}…";
        await DownloadUpdateAsync(info, silent: true);
    }

    /// <summary>Manual check. Returns a status line for the InfoBar.</summary>
    public async Task<string> CheckForUpdatesAsync()
    {
        IsBusy = true;
        BusyText = "Checking GitHub for a newer release…";
        try
        {
            AppServices.Settings.Set(LastCheckKey, DateTime.UtcNow.Ticks.ToString());
            var (info, error) = await UpdateService.CheckAsync();

            if (info is null)
            {
                UpdateStatusText = error ?? "Could not reach GitHub.";
                return $"Update check failed: {UpdateStatusText}";
            }

            if (!UpdateService.IsNewer(info.Version))
            {
                HasUpdate = false;
                _updateInfo = null;
                UpdateNotes = "";
                UpdateStatusText = $"You are on the latest version ({info.Tag}).";
                return $"Stratus is up to date ({info.Tag}).";
            }

            _updateInfo = info;
            UpdateNotes = info.Notes;
            HasUpdate = true;

            if (UpdateService.IsDevBuild)
            {
                UpdateStatusText = UpdateService.SelfUpdateBlockReason;
                return $"Development build: {info.Tag} is available, updates are disabled.";
            }

            UpdateStatusText = $"Downloading {info.Tag}…";
            await DownloadUpdateAsync(info, silent: true);

            return IsUpdateReady
                ? $"{info.Tag} is downloaded and ready to install."
                : $"Found {info.Tag} but the download failed: {UpdateStatusText}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private async Task DownloadUpdateAsync(UpdateCheck info, bool silent)
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
                if (!silent)
                    ShowStatus(UpdateStatusText, InfoBarSeverity.Warning);
                return;
            }

            IsUpdateReady = true;
            UpdateProgress = 100;
            UpdateStatusText = $"{info.Tag} is downloaded. Restart to finish the update.";
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
