using System.Runtime.InteropServices;

namespace Stratus.Core.Services;

public static class SystemInfoService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string? _cpu;
    private static string? _gpu;
    private static string? _ram;
    private static string? _osName;
    private static string? _osBuild;

    // Hardware and OS identity never change during a session: read once, cache for the process.

    public static string GetCpu() => _cpu ??= ReadCpu();

    private static string ReadCpu()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "Unknown CPU";
        }
        catch { return "Unknown CPU"; }
    }

    public static string GetGpu() => _gpu ??= ReadGpu();

    private static string ReadGpu()
    {
        var gpus = new List<string>();
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null)
                return "Unknown GPU";

            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string desc &&
                    !desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                {
                    gpus.Add(desc);
                }
            }
        }
        catch { /* ignore */ }

        return gpus.Count == 0 ? "Unknown GPU" : string.Join(" + ", gpus.Distinct());
    }

    public static string GetRam() => _ram ??= ReadRam();

    private static string ReadRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return "Unknown RAM";

        var gb = status.ullTotalPhys / (1024.0 * 1024 * 1024);
        return $"{Math.Round(gb, gb < 32 ? 1 : 0)} GB";
    }

    public static string GetOsDisplayName() => _osName ??= ReadOsName();

    private static string ReadOsName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = key?.GetValue("ProductName") as string;
            var displayVersion = key?.GetValue("DisplayVersion") as string;
            var build = Environment.OSVersion.Version.Build;

            var name = build >= 22000 ? "Windows 11" : "Windows 10";
            return string.IsNullOrEmpty(displayVersion) ? name : $"{name} {displayVersion}";
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    public static string GetOsBuild() => _osBuild ??= $"Build {Environment.OSVersion.Version.Build}";

    /// <summary>
    /// True when Windows itself flags a pending restart: Component Based Servicing,
    /// Windows Update, or queued file rename operations. Read live, never cached.
    /// </summary>
    public static bool RebootPending()
    {
        try
        {
            using var cbs = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null)
                return true;

            using var wu = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu is not null)
                return true;

            using var session = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            return HasMeaningfulRenames(session?.GetValue("PendingFileRenameOperations"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// PendingFileRenameOperations is noisy: Windows leaves a trailing empty entry after
    /// processing, and the Gaming Services proxy DLL re-registers its rename on every boot
    /// (the service re-locks gamingservicesproxy*.dll.0, so the entry never clears).
    /// Only entries outside those cases mean a real restart is still owed.
    /// </summary>
    private static bool HasMeaningfulRenames(object? value)
    {
        if (value is not string[] entries)
            return false;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            if (entry.Contains("gamingservicesproxy", StringComparison.OrdinalIgnoreCase))
                continue;

            return true;
        }

        return false;
    }
}
