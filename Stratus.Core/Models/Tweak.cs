namespace Stratus.Core.Models;

/// <summary>
/// A single optimization. Knows how to detect its own state, apply and revert itself.
/// </summary>
public abstract class Tweak
{
    protected Tweak(string id, string name, string description, TweakCategory category)
    {
        Id = id;
        Name = name;
        Description = description;
        Category = category;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public TweakCategory Category { get; }
    public RiskLevel Risk { get; init; } = RiskLevel.Recommended;
    public int Weight { get; init; } = 1;
    public bool RequiresReboot { get; init; }

    /// <summary>Optional machine-specific gate (e.g. feature only exists on some hardware).</summary>
    public Func<bool>? Applicability { get; init; }

    /// <summary>False when the tweak does not apply to this machine (e.g. unsupported feature).</summary>
    public virtual bool IsApplicable => Applicability?.Invoke() ?? true;

    /// <summary>Reads the actual system state — never assumes the app itself made the change.</summary>
    public abstract bool IsApplied { get; }

    public abstract TweakResult Apply();
    public abstract TweakResult Revert();
}
