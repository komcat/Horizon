namespace Horizon.IO.Models;

/// <summary>Edge of an input transition that triggers an IO binding.</summary>
public enum IoEdge
{
    Rising,
    Falling,
    Both,
}

/// <summary>An IO pin reference (device name + named pin).</summary>
public record IoEndpoint
{
    public string Device { get; init; } = string.Empty;
    public string PinName { get; init; } = string.Empty;
}

/// <summary>
/// One input→output binding: when <see cref="Input"/> sees <see cref="Trigger"/>
/// (debounced by <see cref="DebounceMs"/>), the <see cref="Output"/> pin is driven.
/// </summary>
public record IoBinding
{
    public string Name { get; init; } = string.Empty;
    public IoEndpoint Input { get; init; } = new();
    public IoEndpoint Output { get; init; } = new();
    public IoEdge Trigger { get; init; } = IoEdge.Rising;
    public int DebounceMs { get; init; } = 100;
    public bool Enabled { get; init; } = true;
}

/// <summary>The <c>io_watch.json</c> document: global enable + poll interval + bindings.</summary>
public record IoWatchConfig
{
    public bool Enabled { get; init; } = true;
    public int PollingIntervalMs { get; init; } = 50;
    public List<IoBinding> Bindings { get; init; } = new();
}
