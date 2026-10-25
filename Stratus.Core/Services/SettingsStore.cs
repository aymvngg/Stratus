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
        try
        {
            _values = File.Exists(filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath)) ?? []
                : [];
        }
        catch { _values = []; }
    }

    public string? Get(string key) { lock (_lock) { return _values.GetValueOrDefault(key); } }

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            _values[key] = value;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
