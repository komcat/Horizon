namespace Horizon.Process;

public sealed class ProcessContext
{
    private readonly object _lock = new();
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public int StepCount { get; set; }
    public CancellationToken CancellationToken { get; set; }

    public void SetValue(string key, float value)
    {
        lock (_lock) { _values[key] = value; }
    }

    public void SetValue(string key, string value)
    {
        lock (_lock) { _values[key] = value; }
    }

    public void SetValue(string key, bool value)
    {
        lock (_lock) { _values[key] = value; }
    }

    public float GetNumeric(string key, float defaultValue = 0f)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var val)) return defaultValue;
            return val switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                string s when float.TryParse(s, out var parsed) => parsed,
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
                float f => f != 0,
                string s => bool.TryParse(s, out var parsed) ? parsed : defaultValue,
                _ => defaultValue
            };
        }
    }

    public bool HasValue(string key)
    {
        lock (_lock) { return _values.ContainsKey(key); }
    }

    public List<string> GetAllKeys()
    {
        lock (_lock) { return _values.Keys.ToList(); }
    }
}
