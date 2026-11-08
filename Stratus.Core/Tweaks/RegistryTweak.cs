using Microsoft.Win32;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.Core.Tweaks;

/// <summary>Declarative registry tweak: apply writes all actions (with backup), revert restores backups/defaults.</summary>
public sealed class RegistryTweak : Tweak
{
    private readonly RegistryService _registry;
    private readonly RegistryAction[] _actions;

    public RegistryTweak(
        string id, string name, string description, TweakCategory category,
        RegistryService registry,
        RegistryAction[] actions)
        : base(id, name, description, category)
    {
        _registry = registry;
        _actions = actions;
    }

    /// <summary>The values this entry changes. Read-only so tests can compare catalog entries without reflection.</summary>
    public IReadOnlyList<RegistryAction> Actions => _actions;

    public override bool IsApplied
    {
        get
        {
            foreach (var a in _actions)
            {
                try
                {
                    if (!Matches(a))
                        return false;
                }
                catch (RegistryReadException)
                {
                    // An unreadable value is unknown, not "not applied": ScoreService retains
                    // the tweak as incomplete instead of scoring it against a guessed state.
                    throw;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }
    }

    private bool Matches(RegistryAction a)
    {
        var read = _registry.Read(a.Hive, a.Path, a.Name);
        if (read.ReadFailed)
            throw new RegistryReadException($"Could not read {a.Hive}\\{a.Path}\\{a.Name}.");

        if (a.Op == RegistryOp.DeleteValue)
            return !read.Exists;

        if (!read.Exists)
            return a.MissingMeansApplied;

        return read.Kind == a.Kind && RegistryService.ValuesEqual(a.Kind, read.Value, a.TweakedValue);
    }

    public override TweakResult Apply()
    {
        var attempt = _registry.BeginAttempt(Id);
        try
        {
            foreach (var a in _actions)
            {
                if (a.Op == RegistryOp.DeleteValue)
                    attempt.Delete(a.Hive, a.Path, a.Name);
                else
                    attempt.Write(a.Hive, a.Path, a.Name, a.Kind, a.TweakedValue);
            }

            attempt.Commit();
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            // Multi-value tweaks must not stay half applied. Only this attempt is undone; the
            // originals it saved stay in the backup file for a later revert.
            try
            {
                var rolledBack = attempt.Rollback();
                var message = rolledBack
                    ? ex.Message
                    : $"{ex.Message} The half-finished changes could not be rolled back; see the Stratus log.";
                Log.Warn($"Apply of '{Id}' failed: {message}");
                return TweakResult.Fail(message);
            }
            catch (Exception rollbackEx)
            {
                Log.Error($"Rollback of '{Id}' threw after a failed apply", rollbackEx);
                return TweakResult.Fail($"{ex.Message} The half-finished changes could not be rolled back; see the Stratus log.");
            }
        }
    }

    public override TweakResult Revert()
    {
        try
        {
            // Snapshot which actions had no saved original before RestoreAll removes them.
            // Afterwards every backup is gone, so asking again would treat a restored original
            // as "never backed up" and overwrite it with the declared default.
            var withoutOriginal = _actions
                .Where(a => !_registry.HasBackup(Id, a.Hive, a.Path, a.Name))
                .ToList();
            var ownedElsewhere = _registry.OwnedScopeIds();

            var hadBackups = _registry.HasBackups(Id);
            var restored = !hadBackups || _registry.RestoreAll(Id);

            // A value with no original of its own (tweak applied by hand or by another tool
            // before Stratus saw it) falls back to the declared default. A value whose original
            // belongs to another catalog entry is left to that entry.
            if (restored)
            {
                foreach (var a in withoutOriginal)
                {
                    if (a.DefaultValue is null || ownedElsewhere.Contains(RegistryService.ScopeIdOf(a.Hive, a.Path, a.Name)))
                        continue;

                    // Never overwrite a value whose state is unknown.
                    var read = _registry.ReadRequired(a.Hive, a.Path, a.Name);
                    var stillTweaked = read.Exists && read.Kind == a.Kind &&
                                       RegistryService.ValuesEqual(a.Kind, read.Value, a.TweakedValue);

                    if (stillTweaked)
                        _registry.WriteValueRaw(a.Hive, a.Path, a.Name, a.Kind, a.DefaultValue);
                }
            }

            if (!restored)
                return TweakResult.Fail("Some values could not be restored. See the Stratus log for details.");

            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Error($"Revert of '{Id}' failed", ex);
            return TweakResult.Fail(ex.Message);
        }
    }
}
