namespace Stratus.Core.Coordination;

/// <summary>
/// Persisted cleanup checkbox selection. A missing setting means "use the defaults";
/// an empty string is a real choice and must stay empty on the next scan.
/// </summary>
public static class CleanupSelection
{
    public static HashSet<string>? Parse(string? raw)
    {
        if (raw is null)
            return null;

        return new HashSet<string>(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string Format(IEnumerable<string> ids) => string.Join(",", ids);
}
