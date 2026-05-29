namespace Horizon.Engine;

/// <summary>Point-in-time view of the io-watch domain.</summary>
public sealed record IoWatchState(
    bool IsRunning,
    bool Enabled,
    int PollIntervalMs,
    int BindingCount);

/// <summary>A configured input→output IO binding (display form).</summary>
public sealed record IoBindingInfo(
    string Name,
    string Input,
    string Output,
    string Trigger,
    int DebounceMs,
    bool Enabled);

/// <summary>Raised on <see cref="IIoWatchFacet.Fired"/> when a binding drives its output.</summary>
public sealed record IoBindingFired(string Name, string Output, bool State);
