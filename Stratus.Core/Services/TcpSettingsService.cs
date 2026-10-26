using Microsoft.Win32;

namespace Stratus.Core.Services;

/// <summary>One TCP global setting with its allowed values, the Windows default and the current value.</summary>
public sealed record TcpSettingItem(
    string Id,
    string Name,
    string Description,
    string[] Values,
    string DefaultValue,
    string CurrentValue);

/// <summary>
/// Reads the TCP global settings from the registry (language independent) and applies them
/// through netsh, which is what the Windows networking stack listens to. Every change is
/// reversible by picking the default value shown next to the current one.
/// </summary>
public static class TcpSettingsService
{
    private const string TcpipParameters = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

    private static readonly string[] AutoTuningValues =
        ["normal", "restricted", "highlyrestricted", "disabled", "experimental"];

    private static readonly string[] ToggleValues = ["enabled", "disabled"];

    public static List<TcpSettingItem> Scan()
    {
        return
        [
            new TcpSettingItem(
                "autotuning",
                "Receive window auto-tuning",
                "Lets Windows grow the receive window with the link speed. Normal suits almost every connection.",
                AutoTuningValues,
                "normal",
                ReadAutoTuning()),

            new TcpSettingItem(
                "rss",
                "Receive-side scaling",
                "Spreads incoming packets across CPU cores. Disable only when a driver or a very old NIC misbehaves.",
                ToggleValues,
                "enabled",
                ReadDword("EnableRSS", 1) == 0 ? "disabled" : "enabled"),

            new TcpSettingItem(
                "timestamps",
                "TCP timestamps",
                "Adds a timestamp to each segment for better round-trip estimates. Off saves a little per-packet work.",
                ToggleValues,
                "enabled",
                (ReadDword("Tcp1323Opts", 3) & 2) != 0 ? "enabled" : "disabled"),
        ];
    }

    /// <summary>Applies one setting through netsh. Returns a message ready for the status bar.</summary>
    public static (bool Success, string Message) Apply(TcpSettingItem item, string value)
    {
        if (!item.Values.Contains(value, StringComparer.OrdinalIgnoreCase))
            return (false, $"'{value}' is not a valid value for {item.Name}.");

        var parameter = item.Id switch
        {
            "autotuning" => "autotuninglevel",
            "rss" => "rss",
            "timestamps" => "timestamps",
            _ => null,
        };

        if (parameter is null)
            return (false, "Unknown TCP setting.");

        var result = ProcessRunner.Run("netsh.exe", $"int tcp set global {parameter}={value}", 30_000);
        if (result.Success)
        {
            Log.Info($"TCP setting {item.Id} set to {value}.");
            return (true, $"{item.Name} set to {value}.");
        }

        var detail = (result.StdErr.Length > 0 ? result.StdErr : result.StdOut)
            .Replace("\r", "").Replace("\n", " ").Trim();
        if (detail.Length > 180)
            detail = detail[..180] + "…";
        if (detail.Length == 0)
            detail = $"netsh exited with code {result.ExitCode}.";

        Log.Warn($"Could not set TCP setting {item.Id}: {detail}");
        return (false, detail);
    }

    /// <summary>Puts every setting back to the Windows default and reports the first failure, if any.</summary>
    public static (bool Success, string Message) ResetAll()
    {
        string? failure = null;

        foreach (var item in Scan())
        {
            var (ok, message) = Apply(item, item.DefaultValue);
            if (!ok && failure is null)
                failure = message;
        }

        return failure is null
            ? (true, "TCP settings restored to Windows defaults.")
            : (false, failure);
    }

    private static string ReadAutoTuning()
    {
        var level = ReadDword("AutoTuningLevelLocal", 3);
        return level switch
        {
            0 => "disabled",
            1 => "highlyrestricted",
            2 => "restricted",
            4 => "experimental",
            _ => "normal",
        };
    }

    private static int ReadDword(string name, int fallback)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TcpipParameters);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the TCP value {name}: {ex.Message}");
            return fallback;
        }
    }
}
