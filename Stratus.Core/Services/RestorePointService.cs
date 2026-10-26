using System.Globalization;

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

public static class RestorePointService
{
    /// <summary>
    /// Creates a system restore point (temporarily lifts the 24h frequency limit,
    /// enables System Restore on the system drive, checkpoints, restores the limit).
    /// </summary>
    public static (bool Success, string Message) Create()
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\SystemRestore'
            $prev = $null
            try {
              $prev = (Get-ItemProperty $key -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
              Set-ItemProperty $key -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue
              Enable-ComputerRestore -Drive "$env:SystemDrive\"
              Checkpoint-Computer -Description 'Stratus Optimizer' -RestorePointType MODIFY_SETTINGS
              'STRATUS_OK'
            } catch {
              'STRATUS_FAIL: ' + $_.Exception.Message
            } finally {
              # Always put the frequency limit back, even when the checkpoint failed,
              # otherwise the machine keeps the lifted limit forever.
              if ($null -ne $prev) {
                Set-ItemProperty $key -Name SystemRestorePointCreationFrequency -Value $prev -Type DWord -ErrorAction SilentlyContinue
              } else {
                # There was no limit value before, so remove the one we created (Windows then uses its default).
                Remove-ItemProperty $key -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue
              }
            }
            """;

        var result = ProcessRunner.PowerShell(script, timeoutMs: 180_000);
        var output = (result.StdOut + result.StdErr).Trim();

        if (output.Contains("STRATUS_OK"))
        {
            Log.Info("Restore point created.");
            return (true, "Restore point created.");
        }

        var reason = output.Contains("STRATUS_FAIL")
            ? output[(output.IndexOf("STRATUS_FAIL") + 13)..].Trim()
            : output;
        var message = string.IsNullOrEmpty(reason) ? "Unknown failure." : reason;
        Log.Error("Restore point failed: " + message);
        return (false, message);
    }

    /// <summary>
    /// Lists the restore points and the shadow storage they occupy. Windows does not expose
    /// a per-point size, so the size shown is the total the snapshots use on the system drive.
    /// </summary>
    public static RestorePointList Scan()
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

        var result = ProcessRunner.PowerShell(script, timeoutMs: 120_000);
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
}
