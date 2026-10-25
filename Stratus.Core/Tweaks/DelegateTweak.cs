using Stratus.Core.Models;

namespace Stratus.Core.Tweaks;

/// <summary>Tweak with custom logic (power plans, per-interface network values, ...).</summary>
public sealed class DelegateTweak : Tweak
{
    private readonly Func<bool> _isApplied;
    private readonly Func<TweakResult> _apply;
    private readonly Func<TweakResult> _revert;
    private readonly Func<bool>? _isApplicable;

    public DelegateTweak(
        string id, string name, string description, TweakCategory category,
        Func<bool> isApplied,
        Func<TweakResult> apply,
        Func<TweakResult> revert,
        Func<bool>? isApplicable = null)
        : base(id, name, description, category)
    {
        _isApplied = isApplied;
        _apply = apply;
        _revert = revert;
        _isApplicable = isApplicable;
    }

    public override bool IsApplicable => _isApplicable?.Invoke() ?? true;
    public override bool IsApplied { get { try { return _isApplied(); } catch { return false; } } }
    public override TweakResult Apply() { try { return _apply(); } catch (Exception ex) { return TweakResult.Fail(ex.Message); } }
    public override TweakResult Revert() { try { return _revert(); } catch (Exception ex) { return TweakResult.Fail(ex.Message); } }
}
