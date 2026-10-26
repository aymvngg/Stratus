using System.Text.Json;

namespace Stratus.Core.Services;

/// <summary>
/// Atomic JSON persistence shared by the small stores (backups, history, scores, settings).
///
/// A save goes to a temporary file that is then moved over the real one, so a crash, a full
/// disk or a second Stratus process can never leave a half written file behind. Loads move an
/// unreadable file aside as ".corrupt" instead of returning an empty list that the next save
/// would silently make permanent.
/// </summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path, string label, Func<T> empty)
    {
        try
        {
            if (!File.Exists(path))
                return empty();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? empty();
        }
        catch (Exception ex)
        {
            Log.Warn($"{label} could not be read, starting empty: {ex.Message}");
            Quarantine(path, label);
            return empty();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A unique temp name per save: the app and the CLI can persist to the same
        // ProgramData file, and a shared ".tmp" name would let them overwrite each other.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    private static void Quarantine(string path, string label)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var broken = path + ".corrupt";
            File.Move(path, broken, overwrite: true);
            Log.Warn($"The unreadable {label} was moved to {broken}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move the unreadable {label} aside: {ex.Message}");
        }
    }
}
