namespace Horizon.Engine;

/// <summary>
/// IO-watch domain — input→output bindings (a switch input driving an output pin).
/// Exposes read state, the configured bindings, and a fired event; the polling +
/// output-drive loop is internal to the engine.
/// </summary>
public interface IIoWatchFacet
{
    /// <summary>Point-in-time io-watch state.</summary>
    IoWatchState GetState();

    /// <summary>The configured (enabled) input→output bindings.</summary>
    IReadOnlyList<IoBindingInfo> Bindings { get; }

    /// <summary>Raised when a binding fires and drives its output.</summary>
    event Action<IoBindingFired>? Fired;
}
