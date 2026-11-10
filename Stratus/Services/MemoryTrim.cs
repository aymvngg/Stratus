using System.Runtime.InteropServices;

namespace Stratus.Services;

/// <summary>
/// Drops the physical footprint when the main window hides to the tray. Pages fault back in on
/// demand when the window reopens, costing milliseconds, and the app sits hidden most of the
/// time. Cosmetic by design: Task Manager's number falls, commit barely moves.
/// </summary>
internal static class MemoryTrim
{
    /// <summary>Collects, then asks the system to trim the working set. Never throws.</summary>
    public static void OnWindowHidden()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            // -1, -1 asks the system to page out everything it can for this process.
            SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Trimming is a courtesy; hiding must never fail because of it.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
}
