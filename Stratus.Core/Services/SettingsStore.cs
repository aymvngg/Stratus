namespace Stratus.Core.Services;

/// <summary>
/// Tiny persisted settings store (e.g. the GUID of the Ultimate Performance plan we created).
/// Every read and write goes through the shared state lock, so a second Stratus process (the
/// CLI next to the app) can neither lose an update nor read a half-written file.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _filePath;

    public SettingsStore(string filePath)
    {
        _filePath = filePath;

        // Same early read a cached copy performed, so a broken file warns at startup.
        // Reads stay fresh; nothing is cached.
        JsonFile.ReadLatest(_filePath, "settings file", () => new Dictionary<string, string>());
    }

    public string? Get(string key)
    {
        var values = JsonFile.ReadLatest(_filePath, "settings file", () => new Dictionary<string, string>());
        return values.GetValueOrDefault(key);
    }

    /// <summary>
    /// Saves the candidate file before returning, so a failed write leaves both the file and
    /// this store unchanged. True when the value was persisted.
    /// </summary>
    public bool TrySet(string key, string value)
    {
        try
        {
            JsonFile.Update(_filePath, "settings file", () => new Dictionary<string, string>(), values =>
            {
                var candidate = new Dictionary<string, string>(values);
                candidate[key] = value;
                return candidate;
            });
            return true;
        }
        catch (Exception ex)
        {
            // A preference that cannot be written must never take the app down.
            Log.Warn($"Could not save setting {key}: {ex.Message}");
            return false;
        }
    }

    public void Set(string key, string value)
    {
        if (!TrySet(key, value))
            Log.Warn($"The setting {key} was not persisted.");
    }
}
