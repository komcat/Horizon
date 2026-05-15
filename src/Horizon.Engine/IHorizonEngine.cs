namespace Horizon.Engine;

/// <summary>
/// The Horizon Layer 1 hardware engine. A headless, UI-free host that owns all
/// hardware. Hardware facets (Motion, IO, …) are added by their own bring-up
/// steps; this skeleton exposes lifecycle, status, and events only.
/// </summary>
public interface IHorizonEngine : IAsyncDisposable
{
    /// <summary>Current lifecycle state.</summary>
    EngineState State { get; }

    /// <summary>Digital IO domain (Fastech Ezi-IO). Valid after <see cref="InitializeAsync"/>.</summary>
    IIOFacet IO { get; }

    /// <summary>Motion domain (PI GCS2, ACS SPiiPlus). Valid after <see cref="InitializeAsync"/>.</summary>
    IMotionFacet Motion { get; }

    /// <summary>Pneumatic domain (IO-driven slides). Valid after <see cref="InitializeAsync"/>.</summary>
    IPneumaticFacet Pneumatic { get; }

    /// <summary>Current-measurement domain (Keithley 6482). Valid after <see cref="InitializeAsync"/>.</summary>
    IMeasurementFacet Measurement { get; }

    /// <summary>Laser-controller domain (Thorlabs CLD101x). Valid after <see cref="InitializeAsync"/>.</summary>
    ILaserFacet Laser { get; }

    /// <summary>Camera domain (Basler Pylon, IDS Peak, uEye). Valid after <see cref="InitializeAsync"/>.</summary>
    ICameraFacet Camera { get; }

    /// <summary>Vision domain (HALCON over camera frames). Valid after <see cref="InitializeAsync"/>.</summary>
    IVisionFacet Vision { get; }

    /// <summary>Loads configuration and builds device objects. Performs no I/O.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Opens every configured device, reporting progress as it goes.</summary>
    Task ConnectAllAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Synchronous, cached status view — safe to call from any thread, never blocks on hardware.</summary>
    HardwareStatusSnapshot GetStatusSnapshot();

    /// <summary>Stops and disconnects all hardware. Idempotent.</summary>
    Task ShutdownAsync();

    /// <summary>Raised when a device's cached status changes.</summary>
    event Action<HardwareStatusChanged>? StatusChanged;

    /// <summary>Raised for every engine log line. The only logging surface.</summary>
    event Action<LogEntry>? LogEmitted;
}
