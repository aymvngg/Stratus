using Stratus.Core.Services;

namespace Stratus.Core.Services;

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
            try {
              $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\SystemRestore'
              $prev = (Get-ItemProperty $key -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
              Set-ItemProperty $key -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue
              Enable-ComputerRestore -Drive "$env:SystemDrive\"
              Checkpoint-Computer -Description 'Stratus Optimizer' -RestorePointType MODIFY_SETTINGS
              if ($null -ne $prev) { Set-ItemProperty $key -Name SystemRestorePointCreationFrequency -Value $prev -Type DWord -ErrorAction SilentlyContinue }
              'STRATUS_OK'
            } catch {
              'STRATUS_FAIL: ' + $_.Exception.Message
            }
            """;

        var result = ProcessRunner.PowerShell(script, timeoutMs: 180_000);
        var output = (result.StdOut + result.StdErr).Trim();

        if (output.Contains("STRATUS_OK"))
            return (true, "Restore point created.");

        var reason = output.Contains("STRATUS_FAIL")
            ? output[(output.IndexOf("STRATUS_FAIL") + 13)..].Trim()
            : output;
        return (false, string.IsNullOrEmpty(reason) ? "Unknown failure." : reason);
    }
}
