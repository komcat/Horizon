using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Pneumatic domain of the engine — slides actuated through IO output pins
/// with optional extended/retracted sensors. Exposes engine-owned types only.
/// </summary>
public interface IPneumaticFacet
{
    /// <summary>Names of all configured pneumatic slides.</summary>
    IReadOnlyList<string> SlideNames { get; }

    /// <summary>Static slide description, or null if the name is unknown.</summary>
    PneumaticSlideInfo? GetSlide(string slide);

    /// <summary>Last polled state for a slide. <see cref="PneumaticSlideState.Unknown"/> if unknown.</summary>
    PneumaticSlideState GetState(string slide);

    /// <summary>Drives the slide output to extend (does not wait for the sensor).</summary>
    Task<Result> ExtendAsync(string slide, CancellationToken ct = default);

    /// <summary>Drives the slide output to retract (does not wait for the sensor).</summary>
    Task<Result> RetractAsync(string slide, CancellationToken ct = default);

    /// <summary>Extends and blocks until the extended sensor confirms or times out.</summary>
    Task<Result> ExtendAndWaitAsync(string slide, CancellationToken ct = default);

    /// <summary>Retracts and blocks until the retracted sensor confirms or times out.</summary>
    Task<Result> RetractAndWaitAsync(string slide, CancellationToken ct = default);

    /// <summary>Raised (edge-triggered) when a slide's polled state changes.</summary>
    event Action<PneumaticStateChanged>? StateChanged;
}
