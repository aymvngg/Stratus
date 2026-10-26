namespace Stratus.Core.Services;

/// <summary>
/// Best-effort file log: %ProgramData%\Stratus\logs\stratus-yyyyMMdd.log, one file per day,
/// files older than 14 days are pruned on init. Logging never throws and never blocks a change.
/// </summary>
public static class Log
{
    private const int RetainDays = 14;

    /// <summary>Roll a single day to "file.log.1" once it passes this size, so one chatty day cannot fill the disk.</summary>
    private const long MaxBytesPerFile = 8L * 1024 * 1024;
    private static readonly object Gate = new();
    private static string _dir = AppPaths.LogsDir;
    private static bool _pruned;

    /// <summary>Creates the log directory if needed and prunes old files. Call once at startup.</summary>
    public static void Init(string? directory = null)
    {
        if (directory is not null)
            _dir = directory;
        try { System.IO.Directory.CreateDirectory(_dir); } catch { }
        Prune();
    }

    /// <summary>Folder the daily log files are written to.</summary>
    public static string LogDir => _dir;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string message)
    {
        try
        {
            var file = Path.Combine(_dir, "stratus-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine;
            lock (Gate)
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytesPerFile)
                {
                    try { File.Move(file, file + ".1", overwrite: true); } catch { /* keep appending */ }
                }

                File.AppendAllText(file, line);
            }
        }
        catch { /* logging must never break a change */ }
    }

    private static void Prune()
    {
        if (_pruned) return;
        _pruned = true;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetainDays);
            foreach (var file in System.IO.Directory.GetFiles(_dir, "stratus-*.log*"))
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    try { File.Delete(file); } catch { }
        }
        catch { }
    }
}
