using System.Globalization;
using Microsoft.Win32;

namespace Stratus.Core.Services;

public sealed record UpdatePauseState(bool IsPaused, DateTime? UntilUtc);

/// <summary>
/// Pauses or resumes Windows Update through the same registry values the Settings app writes.
/// No service is stopped and nothing is uninstalled: Windows simply stops offering updates
/// until the expiry date, then resumes on its own.
/// </summary>
public static class UpdatePauseService
{
    private const string SettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string ExpiryValue = "PauseUpdatesExpiryTime";

    /// <summary>
    /// Expiry Stratus wrote when it paused. Its presence means Stratus owns the pause values;
    /// without it they came from Windows Settings and Resume must leave them alone.
    /// </summary>
    private const string OwnerValue = "StratusPauseExpiry";

    private static readonly string[] PauseValues =
    [
        "PauseUpdatesStartTime",
        "PauseUpdatesExpiryTime",
        "PauseFeatureUpdatesStartTime",
        "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime",
        "PauseQualityUpdatesEndTime",
    ];

    public static UpdatePauseState Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SettingsKey);
            if (key?.GetValue(ExpiryValue) is not string raw || raw.Length == 0)
                return new UpdatePauseState(false, null);

            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until))
                return new UpdatePauseState(false, null);

            return until > DateTime.UtcNow
                ? new UpdatePauseState(true, until)
                : new UpdatePauseState(false, null);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the Windows Update pause state: " + ex.Message);
            return new UpdatePauseState(false, null);
        }
    }

    /// <summary>Pauses feature, quality and driver updates for the given number of days.</summary>
    public static (bool Success, string Message) Pause(int days = 7)
    {
        if (days is < 1 or > 35)
            return (false, "A pause must be between 1 and 35 days.");

        try
        {
            var start = DateTime.UtcNow;
            var end = start.AddDays(days);
            var startText = start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var endText = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            using var key = Registry.LocalMachine.CreateSubKey(SettingsKey, writable: true);
            if (key is null)
                return (false, "The Windows Update settings key could not be opened.");

            key.SetValue("PauseUpdatesStartTime", startText, RegistryValueKind.String);
            key.SetValue("PauseUpdatesExpiryTime", endText, RegistryValueKind.String);
            key.SetValue("PauseFeatureUpdatesStartTime", startText, RegistryValueKind.String);
            key.SetValue("PauseFeatureUpdatesEndTime", endText, RegistryValueKind.String);
            key.SetValue("PauseQualityUpdatesStartTime", startText, RegistryValueKind.String);
            key.SetValue("PauseQualityUpdatesEndTime", endText, RegistryValueKind.String);
            // Record that these values are ours so Resume only clears a pause Stratus set.
            key.SetValue(OwnerValue, endText, RegistryValueKind.String);

            Log.Info($"Windows Update paused for {days} days.");
            return (true, $"Windows Update is paused until {end.ToLocalTime():MMM d, yyyy HH:mm}.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not pause Windows Update", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Clears the pause values so Windows Update checks again on its normal schedule, but only
    /// when Stratus wrote them. A pause set in Windows Settings is left untouched.
    /// </summary>
    public static (bool Success, string Message) Resume()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SettingsKey, writable: true);
            if (key is null)
                return (false, "The Windows Update settings key could not be opened.");

            var owner = key.GetValue(OwnerValue) as string;
            if (string.IsNullOrEmpty(owner))
            {
                Log.Info("Windows Update resume skipped: the pause was not set by Stratus.");
                return (true, "Windows Update was paused outside Stratus, so the pause was left alone. Unpause it in Settings > Windows Update.");
            }

            var current = key.GetValue(ExpiryValue) as string;
            if (!string.IsNullOrEmpty(current) &&
                !string.Equals(current, owner, StringComparison.OrdinalIgnoreCase))
            {
                // The user paused again in Windows Settings after Stratus wrote its values.
                key.DeleteValue(OwnerValue, throwOnMissingValue: false);
                Log.Info("Windows Update resume skipped: the pause changed outside Stratus.");
                return (true, "The pause changed outside Stratus, so it was left alone. Unpause it in Settings > Windows Update.");
            }

            foreach (var name in PauseValues)
                key.DeleteValue(name, throwOnMissingValue: false);
            key.DeleteValue(OwnerValue, throwOnMissingValue: false);

            Log.Info("Windows Update pause cleared.");
            return (true, "Windows Update is active again.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not resume Windows Update", ex);
            return (false, ex.Message);
        }
    }
}
