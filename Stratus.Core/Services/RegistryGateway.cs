using Microsoft.Win32;

namespace Stratus.Core.Services;

/// <summary>
/// Seam over <see cref="RegistryService"/> for services that need backed-up registry values
/// but are tested without a real hive.
/// </summary>
internal interface IRegistryGateway
{
    RegistryRead Read(RegistryHive hive, string path, string name);

    /// <summary>
    /// Reads a value and throws when it cannot be read, so an unknown state is never mistaken
    /// for "absent" by a decision that would delete or overwrite.
    /// </summary>
    RegistryRead ReadRequired(RegistryHive hive, string path, string name);

    /// <summary>Starts an attempt whose writes can be rolled back to their pre-attempt state.</summary>
    IRegistryAttempt BeginAttempt(string tweakId);

    bool HasBackups(string tweakId);

    bool RestoreAll(string tweakId);
}

/// <summary>One multi-value operation that can be undone to its own starting state.</summary>
internal interface IRegistryAttempt
{
    void Write(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value);

    /// <summary>Puts back only the values this attempt changed; durable originals are kept.</summary>
    bool Rollback();

    /// <summary>The attempt succeeded; the saved originals stay for a later revert.</summary>
    void Commit();
}

internal sealed class RegistryServiceGateway : IRegistryGateway
{
    private readonly RegistryService _registry;

    public RegistryServiceGateway(RegistryService registry) => _registry = registry;

    public RegistryRead Read(RegistryHive hive, string path, string name) =>
        _registry.Read(hive, path, name);

    public RegistryRead ReadRequired(RegistryHive hive, string path, string name) =>
        _registry.ReadRequired(hive, path, name);

    public IRegistryAttempt BeginAttempt(string tweakId) =>
        new RegistryAttemptAdapter(_registry.BeginAttempt(tweakId));

    public bool HasBackups(string tweakId) => _registry.HasBackups(tweakId);

    public bool RestoreAll(string tweakId) => _registry.RestoreAll(tweakId);

    private sealed class RegistryAttemptAdapter : IRegistryAttempt
    {
        private readonly RegistryAttempt _attempt;

        public RegistryAttemptAdapter(RegistryAttempt attempt) => _attempt = attempt;

        public void Write(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value) =>
            _attempt.Write(hive, path, name, kind, value);

        public bool Rollback() => _attempt.Rollback();

        public void Commit() => _attempt.Commit();
    }
}

internal static class ServiceRegistryAccess
{
    private static readonly Lazy<RegistryService> Fallback =
        new(() => new RegistryService(new BackupStore(AppPaths.BackupFile)));

    /// <summary>
    /// Uses the caller's shared service when supplied. The fallback keeps the static entry
    /// points working for callers that do not pass one.
    /// </summary>
    public static IRegistryGateway Create(RegistryService? registry) =>
        new RegistryServiceGateway(registry ?? Fallback.Value);
}
