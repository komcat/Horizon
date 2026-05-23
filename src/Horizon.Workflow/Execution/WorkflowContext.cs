using System.Collections.Concurrent;
using System.Globalization;

namespace Horizon.Workflow;

/// <summary>
/// Runtime variable store for a process run — thread-safe, with typed getters.
/// </summary>
public sealed class WorkflowContext
{
    private readonly ConcurrentDictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The DUT run that <c>dut.*</c> steps record into — set by the run driver
    /// when a serial number is supplied, null otherwise. A deck shares one
    /// context, so every program in the deck records into the same run.
    /// </summary>
    public DutRun? Dut { get; set; }

    public void Set(string name, string value) => _vars[name] = value;

    /// <summary>Removes all variables. Does not affect the active <see cref="Dut"/> run.</summary>
    public void Clear() => _vars.Clear();

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
