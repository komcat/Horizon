using Horizon.Common;

namespace Horizon.Workflow.Signals;

/// <summary>
/// A provider of readable scalar signals for the <c>signal.read</c> workflow step.
/// This is the generic seam that lets product-specific telemetry (e.g. FAAM's SiPhOG,
/// which the engine deliberately does not own) feed workflow expressions WITHOUT any
/// product code leaking into <c>Horizon.Engine</c>/<c>Horizon.Common</c> — exactly the
/// pattern used by <see cref="HillClimb.IHillClimbFeedback"/>, generalised from one
/// value to a named catalog.
///
/// Two kinds of source plug into the same <see cref="SignalRegistry"/>:
/// <list type="bullet">
///   <item><b>Built-in</b> — <c>EngineSignalSource</c> exposes the generic engine
///   facets (SMU / PSU / Measurement / TEC / Laser). Every head gets these free.</item>
///   <item><b>Product</b> — a Layer 3 head registers one via
///   <c>IProcessService.AddSignalSource</c> (FAAM wraps its <c>SiphogClient</c>).</item>
/// </list>
/// </summary>
public interface ISignalSource
{
    /// <summary>
    /// The signals this source currently offers — built dynamically (connected
    /// devices, streaming product links). Drives the editor's read-out drop-down.
    /// </summary>
    IReadOnlyList<SignalDescriptor> Describe();

    /// <summary>
    /// True if <paramref name="key"/> belongs to this source. Lets the registry route
    /// a read to exactly one source and tell "not mine" apart from "read failed".
    /// </summary>
    bool Owns(string key);

    /// <summary>
    /// Reads the current value for <paramref name="key"/>. May perform live I/O
    /// (a fresh SMU measurement, a PSU refresh) — callers await it off the UI thread.
    /// Only invoked for keys this source <see cref="Owns"/>.
    /// </summary>
    Task<Result<double>> ReadAsync(string key, CancellationToken ct = default);
}
