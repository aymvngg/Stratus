namespace Stratus.Core.Models;

public enum RiskLevel
{
    /// <summary>Zero downside. Reversible with no side effects.</summary>
    Safe,

    /// <summary>Community-proven, recommended for gaming/performance.</summary>
    Recommended,

    /// <summary>Real trade-offs. Requires extra confirmation in the UI.</summary>
    Advanced
}
