namespace Stratus.Core.Services;

/// <summary>Well-known paths for Stratus (backup store, settings, logs).</summary>
public static class AppPaths
{
    public static readonly string ProgramData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Stratus");

    public static readonly string BackupFile = Path.Combine(ProgramData, "backups.json");
    public static readonly string SettingsFile = Path.Combine(ProgramData, "settings.json");
    public static readonly string HistoryFile = Path.Combine(ProgramData, "history.json");
}
