using System.Text.Json;

namespace Stratus.Core.Services;

/// <summary>Tiny persisted settings store (e.g. the GUID of the Ultimate Performance plan we created).</summary>
public sealed class SettingsStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, string> _values;

    public SettingsStore(string filePath)
    {
        _filePath = filePath;
        _values = JsonFile.Load(filePath, "settings file", () => new Dictionary<string, string>());
    }

    public string? Get(string key) { lock (_lock) { return _values.GetValueOrDefault(key); } }

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            _values[key] = value;
            try
            {
                JsonFile.Save(_filePath, _values);
            }
            catch (Exception ex)
            {
                // A preference that cannot be written must never take the app down.
                Log.Warn("Could not save the settings file: " + ex.Message);
            }
        }
    }
}
