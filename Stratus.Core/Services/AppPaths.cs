namespace Stratus.Core.Services;

/// <summary>Well-known paths for Stratus (backup store, settings, logs).</summary>
public static class AppPaths
{
    public static readonly string ProgramData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Stratus");

    public static readonly string BackupFile = Path.Combine(ProgramData, "backups.json");
    public static readonly string SettingsFile = Path.Combine(ProgramData, "settings.json");
    public static readonly string HistoryFile = Path.Combine(ProgramData, "history.json");
    public static readonly string ScoreHistoryFile = Path.Combine(ProgramData, "score-history.json");
    public static readonly string TasksFile = Path.Combine(ProgramData, "tasks.json");
    public static readonly string LogsDir = Path.Combine(ProgramData, "logs");
    public static readonly string ReportsDir = Path.Combine(ProgramData, "reports");
    public static readonly string ProfilesDir = Path.Combine(ProgramData, "profiles");
}
