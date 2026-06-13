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

    /// <summary>
    /// True when <see cref="Dut"/> was supplied as a <em>fallback</em> (an open
    /// operator session or the sticky active serial) rather than owned by this run
    /// (an up-front serial or a <c>dut.promptSerial</c> step). A
    /// <c>dut.promptSerial</c> step is allowed to override a fallback DUT, but not
    /// an owned one.
    /// </summary>
    public bool DutIsFallback { get; set; }

    /// <summary>
    /// Serial of the active <see cref="Dut"/> run, kept alongside it (the run itself
    /// doesn't carry the serial). Lets a <c>dut.promptSerial</c> step <em>adopt</em>
    /// an already-active DUT — skipping its prompt — so the block and the Run-page
    /// "Start DUT" button are interchangeable.
    /// </summary>
    public string? DutSerial { get; set; }

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
