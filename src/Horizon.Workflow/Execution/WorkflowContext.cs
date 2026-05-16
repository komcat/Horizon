using System.Collections.Concurrent;
using System.Globalization;

namespace Horizon.Workflow;

/// <summary>
/// Runtime variable store for a process run — thread-safe, with typed getters.
/// </summary>
public sealed class WorkflowContext
{
    private readonly ConcurrentDictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string value) => _vars[name] = value;

    public void Set(string name, double value)
        => _vars[name] = value.ToString(CultureInfo.InvariantCulture);

    public string GetString(string name)
        => _vars.TryGetValue(name, out var v) ? v : string.Empty;

    public double GetNumeric(string name)
        => _vars.TryGetValue(name, out var v)
           && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0.0;

    public IReadOnlyDictionary<string, string> Snapshot()
        => new Dictionary<string, string>(_vars);
}
