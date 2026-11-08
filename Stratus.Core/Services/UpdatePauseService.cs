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

    /// <summary>Tweak id used for the backed-up pause values.</summary>
    internal const string TweakId = "update-pause";

    /// <summary>
    /// Expiry Stratus wrote when it paused. Its presence means Stratus owns the pause values;
    /// without it they came from Windows Settings and Resume must leave them alone.
    /// </summary>
    private const string OwnerValue = "StratusPauseExpiry";

    /// <summary>One cross-process lock shared by every Pause and Resume caller without an explicit gate.</summary>
    private static readonly NamedMutexGate DefaultGate = new(MutexName());

    /// <summary>Stable across processes: string hashing is randomized per process, a file digest is not.</summary>
    private static string MutexName() =>
        "Global\\Stratus-UpdatePause-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(SettingsKey.ToLowerInvariant())))[..16];

    /// <summary>
    /// Cross-process lock seam so Pause and Resume are serialised. Without it a Resume running
    /// while another process is mid-Pause can consume the in-flight pause's saved originals.
    /// </summary>
    internal interface IUpdatePauseGate
    {
        /// <summary>Null means the lock could not be taken and the operation must refuse.</summary>
        IDisposable? TryBegin();
    }

    /// <summary>Named mutex held for one whole Pause or Resume, created per call like the DNS originals lock.</summary>
    private sealed class NamedMutexGate : IUpdatePauseGate
    {
        private readonly string _name;
        private readonly TimeSpan _timeout;

        public NamedMutexGate(string name, TimeSpan? timeout = null)
        {
            _name = name;
            _timeout = timeout ?? TimeSpan.FromSeconds(15);
        }

        public IDisposable? TryBegin()
        {
            Mutex? mutex;
            try
            {
                mutex = new Mutex(initiallyOwned: false, _name);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not create the update pause lock '{_name}'.", ex);
                return null;
            }

            try
            {
                if (!mutex.WaitOne(_timeout))
                {
                    mutex.Dispose();
                    Log.Warn("Another Stratus process held the update pause lock for too long.");
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died while holding the lock. We own it now, and the saved
                // originals are still validated by the reads before anything is changed.
            }
            catch (Exception ex)
            {
                Log.Error("Could not take the update pause lock", ex);
                mutex.Dispose();
                return null;
            }

            return new Lease(mutex);
        }

        private sealed class Lease : IDisposable
        {
            private readonly Mutex _mutex;
            private bool _released;

            public Lease(Mutex mutex) => _mutex = mutex;

            public void Dispose()
            {
                if (_released)
                    return;

                _released = true;
                try { _mutex.ReleaseMutex(); } catch { /* not owned any more */ }
                _mutex.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads the pause state. Throws when the registry cannot be read or the expiry cannot be
    /// interpreted: an unknown pause must never be shown as "not paused", which reads as an off
    /// switch. A value that is genuinely absent is the normal not-paused state.
    /// </summary>
    public static UpdatePauseState Read() => Read(ServiceRegistryAccess.Create(null));

    internal static UpdatePauseState Read(IRegistryGateway registry)
    {
        var raw = registry.ReadRequired(RegistryHive.LocalMachine, SettingsKey, ExpiryValue);
        if (!raw.Exists)
            return new UpdatePauseState(false, null);

        if (raw.Value is not string text)
            throw new RegistryReadException(
                "The update pause expiry value could not be interpreted, so the pause state is unknown.");

        if (text.Length == 0)
            return new UpdatePauseState(false, null);

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until))
            throw new RegistryReadException(
                "The update pause expiry value could not be parsed, so the pause state is unknown.");

        return until > DateTime.UtcNow
            ? new UpdatePauseState(true, until)
            : new UpdatePauseState(false, null);
    }

    /// <summary>Pauses feature, quality and driver updates for the given number of days.</summary>
    public static (bool Success, string Message) Pause(int days = 7, RegistryService? registry = null) =>
        Pause(days, ServiceRegistryAccess.Create(registry));

    internal static (bool Success, string Message) Pause(
        int days, IRegistryGateway registry, IUpdatePauseGate? gate = null)
    {
        if (days is < 1 or > 35)
            return (false, "A pause must be between 1 and 35 days.");

        // One gate for the whole value sequence: a Resume running in another process mid-Pause
        // would otherwise read the half-written state as an interrupted pause and restore it.
        using var operation = (gate ?? DefaultGate).TryBegin();
        if (operation is null)
            return (false, "Another Stratus process is changing the update pause right now. Try again in a moment.");

        try
        {
            var start = DateTime.UtcNow;
            var end = start.AddDays(days);
            var startText = start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var endText = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var values = new (string Name, string Value)[]
            {
                ("PauseUpdatesStartTime", startText),
                ("PauseUpdatesExpiryTime", endText),
                ("PauseFeatureUpdatesStartTime", startText),
                ("PauseFeatureUpdatesEndTime", endText),
                ("PauseQualityUpdatesStartTime", startText),
                ("PauseQualityUpdatesEndTime", endText),
                (OwnerValue, endText),
            };

            // Attempt-scoped writes: a failed reapply returns to the pause the user had, while
            // the first pause stays saved for Resume.
            var attempt = registry.BeginAttempt(TweakId);
            try
            {
                foreach (var (name, value) in values)
                    attempt.Write(RegistryHive.LocalMachine, SettingsKey, name, RegistryValueKind.String, value);
            }
            catch (Exception ex)
            {
                var rolledBack = attempt.Rollback();
                Log.Error("Could not pause Windows Update", ex);
                return (false, rolledBack
                    ? ex.Message
                    : ex.Message + " Some pause values could not be rolled back; they stay backed up.");
            }

            attempt.Commit();
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
    public static (bool Success, string Message) Resume(RegistryService? registry = null) =>
        Resume(ServiceRegistryAccess.Create(registry));

    internal static (bool Success, string Message) Resume(IRegistryGateway registry, IUpdatePauseGate? gate = null)
    {
        using var operation = (gate ?? DefaultGate).TryBegin();
        if (operation is null)
            return (false, "Another Stratus process is changing the update pause right now. Try again in a moment.");

        try
        {
            // Strict reads: a value that cannot be read must not look like "the pause is not
            // ours" or "the external pause is gone", either of which would delete something.
            var owner = registry.ReadRequired(RegistryHive.LocalMachine, SettingsKey, OwnerValue);
            var ownedByStratus = owner.Exists && owner.Value is string ownerValue && ownerValue.Length > 0;

            if (!ownedByStratus)
            {
                // No owner value normally means the pause came from Windows Settings, which must
                // be left alone. The exception is a pause whose attempt was interrupted before it
                // wrote the owner: its saved originals are the explicit evidence of that attempt,
                // and without this recovery the pause would stay active and unowned forever.
                if (registry.HasBackups(TweakId))
                {
                    Log.Warn("Windows Update resume found an interrupted pause and is restoring its saved values.");
                    if (!registry.RestoreAll(TweakId))
                    {
                        Log.Warn("The interrupted pause could not be restored; its saved originals were kept.");
                        return (false, "An interrupted update pause could not be restored. See the Stratus log for details.");
                    }

                    return (true, "An interrupted Windows Update pause was repaired and updates are active again.");
                }

                Log.Info("Windows Update resume skipped: the pause was not set by Stratus.");
                return (true, "Windows Update was paused outside Stratus, so the pause was left alone. Unpause it in Settings > Windows Update.");
            }

            var ownerText = (string)owner.Value!;
            var current = registry.ReadRequired(RegistryHive.LocalMachine, SettingsKey, ExpiryValue);
            if (current.Exists && current.Value is string currentText && currentText.Length > 0 &&
                !string.Equals(currentText, ownerText, StringComparison.OrdinalIgnoreCase))
            {
                // Leave the owner value in place: it keeps the guard active until the external
                // pause is gone. Removing it here would let a later Resume touch someone else's pause.
                Log.Info("Windows Update resume skipped: the pause changed outside Stratus.");
                return (true, "The pause changed outside Stratus, so it was left alone. Unpause it in Settings > Windows Update.");
            }

            if (registry.HasBackups(TweakId))
            {
                if (!registry.RestoreAll(TweakId))
                {
                    Log.Warn("Windows Update resume could not restore every original value; the backups were kept.");
                    return (false, "Some update pause values could not be restored. See the Stratus log for details.");
                }
            }
            else
            {
                // A pause written by an older Stratus build recorded ownership but no backup,
                // so the original values are unknown and cannot be restored safely.
                Log.Info("Windows Update resume skipped: the pause has no saved originals.");
                return (false, "This pause was set by an older Stratus build, so its original values were never saved. Unpause it in Settings > Windows Update.");
            }

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
