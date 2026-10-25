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

    public static string GetCpu()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "Unknown CPU";
        }
        catch { return "Unknown CPU"; }
    }

    public static string GetGpu()
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

    public static string GetRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return "Unknown RAM";

        var gb = status.ullTotalPhys / (1024.0 * 1024 * 1024);
        return $"{Math.Round(gb, gb < 32 ? 1 : 0)} GB";
    }

    public static string GetOsDisplayName()
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

    public static string GetOsBuild() => $"Build {Environment.OSVersion.Version.Build}";
}
