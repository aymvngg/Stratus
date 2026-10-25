namespace Stratus.Core.Models;

/// <summary>
/// Snapshot of a system value before a tweak changed it. Persisted as JSON so
/// every change is exactly reversible, even across app restarts.
/// ScopeType: "registry" (ScopeId = "Hive|Path|Name") or "service" (ScopeId = service name).
/// </summary>
public sealed class BackupEntry
{
    public string TweakId { get; set; } = "";
    public string ScopeType { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public string Key { get; set; } = "";
    public string? ValueKind { get; set; }
    public string? ValueJson { get; set; }
    public bool Existed { get; set; }
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
}
