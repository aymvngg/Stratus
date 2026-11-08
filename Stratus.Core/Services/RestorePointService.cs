using System.Globalization;
using Microsoft.Win32;

namespace Stratus.Core.Services;

/// <summary>One System Restore point as recorded by Windows.</summary>
public sealed record RestorePointItem(int Sequence, string Description, DateTime CreatedUtc, string Type);

/// <summary>Restore points plus how much disk the shadow storage uses. Available is false when System Restore is off.</summary>
public sealed record RestorePointList(
    IReadOnlyList<RestorePointItem> Points,
    long UsedBytes,
    long AllocatedBytes,
    bool Available,
    string? Error);

/// <summary>Outcome of the Checkpoint-Computer child, before the frequency value is restored.</summary>
internal sealed record RestorePointCheckpoint(bool Success, string Message, string RawOutput);

internal interface IRestorePointCheckpoint
{
    RestorePointCheckpoint Create();
}

public static class RestorePointService
{
    private const string RestoreKey = @"SOFTWARE\Microsoft\Windows NT\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";

    /// <summary>Stable id so the original frequency value survives a crash and stays backed up.</summary>
    internal const string FrequencyTweakId = "restore-point-frequency";

    /// <summary>
    /// Creates a system restore point. The 24 hour frequency limit is backed up and lifted
    /// through <see cref="RegistryService"/> in this process, so the child being killed or
    /// timing out cannot skip putting the original value back.
    /// </summary>
    public static (bool Success, string Message) Create() =>
        Create(ServiceRegistryAccess.Create(null), new PowerShellRestorePointCheckpoint());

    public static (bool Success, string Message) Create(RegistryService registry) =>
        Create(ServiceRegistryAccess.Create(registry), new PowerShellRestorePointCheckpoint());

    internal static (bool Success, string Message) Create(
        IRegistryGateway registry,
        IRestorePointCheckpoint checkpoint)
    {
        IRegistryAttempt? attempt = null;
        try
        {
            // Strict read: when the live value cannot be read, this attempt must not start from
            // an unknown state, and crash recovery must not consume its saved original blindly.
            var current = registry.ReadRequired(RegistryHive.LocalMachine, RestoreKey, FrequencyValue);

            // Crash recovery only. A saved original is applied when the live value is still the
            // lifted 0 that a killed run left behind. If the live value is anything else, the
            // machine has moved on since, so an older saved value must not overwrite it.
            if (registry.HasBackups(FrequencyTweakId) &&
                current.Exists && IsLifted(current))
            {
                if (!registry.RestoreAll(FrequencyTweakId))
                {
                    return (false,
                        "A previous restore point attempt left the 24 hour limit lifted and it could not be restored. "
                        + "Set SystemRestorePointCreationFrequency by hand, then try again.");
                }

                current = registry.Read(RegistryHive.LocalMachine, RestoreKey, FrequencyValue);
            }
            else if (registry.HasBackups(FrequencyTweakId))
            {
                Log.Warn("A saved restore point frequency original no longer matches the live value; it was left alone.");
            }

            // The lift is attempt-scoped, so finishing returns to whatever the machine had when
            // this attempt started, not to an older saved value.
            attempt = registry.BeginAttempt(FrequencyTweakId);
            try
            {
                attempt.Write(RegistryHive.LocalMachine, RestoreKey, FrequencyValue, RegistryValueKind.DWord, 0);
            }
            catch
            {
                // The attempt can be left half written by the failing set, so it must be undone
                // before the error surfaces.
                RollbackLift(attempt);
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not lift the restore point frequency limit", ex);
            return (false, "The restore point frequency limit could not be changed: " + ex.Message);
        }

        RestorePointCheckpoint result;
        var frequencyRestored = true;
        try
        {
            result = checkpoint.Create();
        }
        catch (Exception ex)
        {
            Log.Error("Creating a restore point failed", ex);
            result = new RestorePointCheckpoint(false, ex.Message, "");
        }
        finally
        {
            frequencyRestored = RollbackLift(attempt);
            if (!frequencyRestored)
                Log.Error("The restore point frequency limit could not be restored; its backup was kept.");
        }

        if (!frequencyRestored)
        {
            return (false, result.Success
                ? "Restore point created, but the 24 hour restore point limit could not be put back. The original value stays backed up."
                : $"{result.Message} The 24 hour restore point limit also could not be put back; the original value stays backed up.");
        }

        if (result.Success)
        {
            Log.Info("Restore point created.");
            return (true, "Restore point created.");
        }

        Log.Error("Restore point failed: " + result.Message);
        return (false, result.Message);
    }

    private static bool IsLifted(RegistryRead read) =>
        read.Value is not null && Convert.ToInt64(read.Value, System.Globalization.CultureInfo.InvariantCulture) == 0;

    private static bool RollbackLift(IRegistryAttempt? attempt)
    {
        if (attempt is null)
            return true;

        try
        {
            return attempt.Rollback();
        }
        catch (Exception ex)
        {
            Log.Error("Restoring the restore point frequency limit failed", ex);
            return false;
        }
    }

    /// <summary>Seam over the PowerShell child used by the scan, so a failed child is testable.</summary>
    internal interface IRestorePointScanner
    {
        ProcessResult? Run(string script);
    }

    /// <summary>
    /// Lists the restore points and the shadow storage they occupy. Windows does not expose
    /// a per-point size, so the size shown is the total the snapshots use on the system drive.
    /// </summary>
    public static RestorePointList Scan() =>
        Scan(new PowerShellRestorePointScanner());

    internal static RestorePointList Scan(IRestorePointScanner scanner)
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            try {
              foreach ($p in (Get-ComputerRestorePoint -ErrorAction Stop)) {
                $created = [Management.ManagementDateTimeConverter]::ToDateTime($p.CreationTime).ToUniversalTime().ToString('o')
                'POINT|' + $p.SequenceNumber + '|' + $created + '|' + $p.RestorePointType + '|' + $p.Description
              }
              $used = 0
              $allocated = 0
              foreach ($s in (Get-CimInstance -ClassName Win32_ShadowStorage -ErrorAction SilentlyContinue)) {
                $used += [long]$s.UsedSpace
                $allocated += [long]$s.AllocatedSpace
              }
              'STORAGE|' + $used + '|' + $allocated
            } catch {
              'STRATUS_FAIL|' + $_.Exception.Message
            }
            """;

        ProcessResult? result;
        try
        {
            result = scanner.Run(script);
        }
        catch (Exception ex)
        {
            Log.Error("Restore point scan could not start", ex);
            return new RestorePointList([], 0, 0, false, "The restore points could not be read: " + ex.Message);
        }

        // A child that never ran or exited non-zero must not be reported as "no restore
        // points": that reads as a clean machine and hides a broken System Restore.
        if (result is null)
        {
            Log.Warn("Restore point scan failed: the PowerShell process could not be started.");
            return new RestorePointList([], 0, 0, false,
                "The restore points could not be read because the query process did not start.");
        }

        if (!result.Success)
        {
            var detail = (result.StdErr + " " + result.StdOut).Trim();
            Log.Warn($"Restore point scan failed with exit code {result.ExitCode}: {detail}");
            return new RestorePointList([], 0, 0, false,
                "The restore points could not be read"
                + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail));
        }

        var output = (result.StdOut + result.StdErr).Replace("\r", "");
        var points = new List<RestorePointItem>();
        long used = 0;
        long allocated = 0;

        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
                continue;

            if (text.StartsWith("STRATUS_FAIL", StringComparison.OrdinalIgnoreCase))
            {
                var reason = text.Contains('|') ? text[(text.IndexOf('|') + 1)..].Trim() : text;
                Log.Warn("Restore point scan failed: " + reason);
                return new RestorePointList(points, 0, 0, false,
                    string.IsNullOrEmpty(reason) ? "System Restore is not available." : reason);
            }

            if (text.StartsWith("STORAGE|", StringComparison.OrdinalIgnoreCase))
            {
                var parts = text.Split('|');
                if (parts.Length >= 3)
                {
                    long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out used);
                    long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out allocated);
                }
                continue;
            }

            if (!text.StartsWith("POINT|", StringComparison.OrdinalIgnoreCase))
                continue;

            // Split into five parts so a description that contains a pipe survives.
            var fields = text.Split('|', 5);
            if (fields.Length < 5)
                continue;

            int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence);
            DateTime.TryParse(fields[2], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created);

            points.Add(new RestorePointItem(sequence, fields[4].Trim(), created, fields[3].Trim()));
        }

        points.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return new RestorePointList(points, used, allocated, true, null);
    }

    /// <summary>Deletes one restore point by sequence number. The other points and your files are untouched.</summary>
    public static (bool Success, string Message) Delete(int sequence)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
              $point = Get-CimInstance -ClassName SystemRestore -Namespace root/default -ErrorAction Stop |
                Where-Object { $_.SequenceNumber -eq {{sequence}} } | Select-Object -First 1
              if ($null -eq $point) {
                'STRATUS_FAIL|That restore point no longer exists.'
              } else {
                $point | Remove-CimInstance -ErrorAction Stop
                'STRATUS_OK'
              }
            } catch {
              'STRATUS_FAIL|' + $_.Exception.Message
            }
            """;

        var result = ProcessRunner.PowerShell(script, timeoutMs: 120_000);
        var output = (result.StdOut + result.StdErr).Trim();

        if (output.Contains("STRATUS_OK"))
        {
            Log.Info($"Restore point {sequence} deleted.");
            return (true, "Restore point deleted.");
        }

        var reason = output.Contains("STRATUS_FAIL")
            ? output[(output.IndexOf("STRATUS_FAIL") + 12)..].Trim().TrimStart('|').Trim()
            : output;
        var message = string.IsNullOrEmpty(reason) ? "Unknown failure." : reason;
        Log.Warn($"Could not delete restore point {sequence}: {message}");
        return (false, message);
    }

    /// <summary>
    /// Runs Checkpoint-Computer. The frequency limit is not touched here: the caller owns the
    /// backed-up value and restores it even when this child is killed or times out.
    /// </summary>
    private sealed class PowerShellRestorePointCheckpoint : IRestorePointCheckpoint
    {
        public RestorePointCheckpoint Create()
        {
            var script = """
                $ErrorActionPreference = 'Stop'
                try {
                  Enable-ComputerRestore -Drive "$env:SystemDrive\"
                  Checkpoint-Computer -Description 'Stratus Optimizer' -RestorePointType MODIFY_SETTINGS
                  'STRATUS_OK'
                } catch {
                  'STRATUS_FAIL: ' + $_.Exception.Message
                }
                """;

            var result = ProcessRunner.PowerShell(script, timeoutMs: 180_000);
            var output = (result.StdOut + result.StdErr).Trim();

            if (output.Contains("STRATUS_OK"))
                return new RestorePointCheckpoint(true, "Restore point created.", output);

            var reason = output.Contains("STRATUS_FAIL")
                ? output[(output.IndexOf("STRATUS_FAIL") + 13)..].Trim()
                : output;
            return new RestorePointCheckpoint(false,
                string.IsNullOrEmpty(reason) ? "Unknown failure." : reason, output);
        }
    }

    private sealed class PowerShellRestorePointScanner : IRestorePointScanner
    {
        public ProcessResult? Run(string script) => ProcessRunner.PowerShell(script, timeoutMs: 120_000);
    }
}
