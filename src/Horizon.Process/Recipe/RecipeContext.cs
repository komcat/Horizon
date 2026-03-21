namespace Horizon.Process.Recipe;

/// <summary>
/// Thread-safe runtime variable storage for recipe execution.
/// Stores typed values (double, string, bool) keyed by case-insensitive name.
/// </summary>
public sealed class RecipeContext
{
    private readonly object _lock = new();
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public CancellationToken CancellationToken { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }

    public void Set(string key, double value) { lock (_lock) _values[key] = value; }
    public void Set(string key, string value) { lock (_lock) _values[key] = value; }
    public void Set(string key, bool value) { lock (_lock) _values[key] = value; }

    public void SetObject(string key, object value) { lock (_lock) _values[key] = value; }

    public double GetNumeric(string key, double defaultValue = 0.0)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var val)) return defaultValue;
            return val switch
            {
                double d => d,
                float f => f,
                int i => i,
                string s => double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue,
                bool b => b ? 1.0 : 0.0,
                _ => defaultValue
            };
        }
    }

    public string GetString(string key, string defaultValue = "")
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var val)) return defaultValue;
            return val?.ToString() ?? defaultValue;
        }
    }

    public bool GetBoolean(string key, bool defaultValue = false)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var val)) return defaultValue;
            return val switch
            {
                bool b => b,
                double d => d != 0.0,
                float f => f != 0f,
                string s => bool.TryParse(s, out var parsed) ? parsed : defaultValue,
                _ => defaultValue
            };
        }
    }

    public bool HasValue(string key)
    {
        lock (_lock) return _values.ContainsKey(key);
    }

    public object? GetRaw(string key)
    {
        lock (_lock) return _values.TryGetValue(key, out var val) ? val : null;
    }

    public List<(string Key, string Value, string Type)> GetSnapshot()
    {
        lock (_lock)
        {
            return _values
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value?.ToString() ?? "", kv.Value?.GetType().Name ?? "null"))
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock) _values.Clear();
    }
}
