namespace Horizon.Workflow.SpiralScan;

/// <summary>
/// The feedback signal the <see cref="SpiralSearchEngine"/> maximises across the
/// spiral. Same seam contract as <c>HillClimb.IHillClimbFeedback</c>: the signal
/// source (e.g. the FAAM SiPhOG <c>SAG PWR (V)</c>) lives in the Layer-3 head, so
/// the engine reads it synchronously through this interface. A Layer-3 head
/// supplies an implementation backed by its latest streamed frame; an adapter
/// wrapping an existing <c>IHillClimbFeedback</c> is trivial.
/// </summary>
public interface ISpiralSearchFeedback
{
    /// <summary>Human-readable signal name, e.g. <c>"SiPhOG SAG PWR (V)"</c>. Used for logging.</summary>
    string Description { get; }

    /// <summary>True once at least one valid sample is available (e.g. the source is streaming).</summary>
    bool HasSample { get; }

    /// <summary>
    /// The latest signal value, served synchronously. For a streaming source this
    /// is the most recent cached frame; the engine averages several reads per point.
    /// </summary>
    double Read();
}
