namespace Stratus.Core.Services;

/// <summary>
/// Splits a raw Windows command line into its executable and arguments.
/// Shared by the debloat uninstall runner and the startup entry resolver so both
/// treat quoted paths and unquoted paths with spaces exactly the same way.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// Quoted prefixes are used as-is; unquoted paths with spaces are resolved by
    /// growing the prefix until an existing file matches. When nothing matches the
    /// first word is treated as the executable, mirroring CreateProcess behavior.
    /// </summary>
    public static (string Exe, string Args) Split(string command)
    {
        var s = command.Trim();
        if (s.Length == 0)
            return ("", "");

        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0)
                return (s.Trim('"'), "");

            return (s[1..end], s[(end + 1)..].Trim());
        }

        if (File.Exists(s))
            return (s, "");

        for (var space = s.IndexOf(' '); space > 0; space = s.IndexOf(' ', space + 1))
        {
            var candidate = s[..space];
            if (File.Exists(candidate))
                return (candidate, s[(space + 1)..].Trim());
        }

        var firstSpace = s.IndexOf(' ');
        return firstSpace < 0 ? (s, "") : (s[..firstSpace], s[(firstSpace + 1)..].Trim());
    }

    /// <summary>Returns the executable of a command line when it exists on disk, otherwise null.</summary>
    public static string? ResolveExistingPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var (exe, _) = Split(command);
        return exe.Length > 0 && File.Exists(exe) ? exe : null;
    }
}
