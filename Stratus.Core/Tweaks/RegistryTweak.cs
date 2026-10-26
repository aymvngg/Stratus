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

    public override bool IsApplied
    {
        get
        {
            try
            {
                return _actions.All(Matches);
            }
            catch
            {
                return false;
            }
        }
    }

    private bool Matches(RegistryAction a)
    {
        var read = _registry.Read(a.Hive, a.Path, a.Name);

        if (a.Op == RegistryOp.DeleteValue)
            return !read.Exists;

        if (!read.Exists)
            return a.MissingMeansApplied;

        return read.Kind == a.Kind && RegistryService.ValuesEqual(a.Kind, read.Value, a.TweakedValue);
    }

    public override TweakResult Apply()
    {
        try
        {
            foreach (var a in _actions)
            {
                if (a.Op == RegistryOp.DeleteValue)
                {
                    _registry.DeleteValue(a.Hive, a.Path, a.Name, Id);
                }
                else
                {
                    _registry.WriteValue(a.Hive, a.Path, a.Name, a.Kind, a.TweakedValue, Id);
                }
            }
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }

    public override TweakResult Revert()
    {
        try
        {
            var hadBackups = _registry.HasBackups(Id);
            var restored = !hadBackups || _registry.RestoreAll(Id);

            // For values that never got a backup entry (tweak applied by hand or by another tool
            // before Stratus saw it), fall back to the declared default.
            foreach (var a in _actions)
            {
                if (a.DefaultValue is null)
                    continue;

                var read = _registry.Read(a.Hive, a.Path, a.Name);
                var stillTweaked = read.Exists && read.Kind == a.Kind &&
                                   RegistryService.ValuesEqual(a.Kind, read.Value, a.TweakedValue);

                if (stillTweaked)
                    _registry.WriteValueRaw(a.Hive, a.Path, a.Name, a.Kind, a.DefaultValue);
            }

            if (!restored)
                return TweakResult.Fail("Some values could not be restored. See the Stratus log for details.");

            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }
}
