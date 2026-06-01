namespace Horizon.Workflow.HillClimb;

/// <summary>
/// The feedback signal the <see cref="HillClimbEngine"/> maximises. In
/// <c>acs_solution_saa</c> this was <c>RealtimeDataClass["ActualSagnac"]</c>; in
/// Horizon the signal source (the SiPhOG <c>SAG PWR (V)</c>) lives in the Layer-3
/// app, so the engine reads it through this seam. The Layer-3 head supplies an
/// implementation backed by its <c>SiphogClient</c> latest frame.
/// </summary>
public interface IHillClimbFeedback
{
    /// <summary>Human-readable signal name, e.g. <c>"SiPhOG SAG PWR (V)"</c>. Used for logging.</summary>
    string Description { get; }

    /// <summary>True once at least one valid sample is available (e.g. the SiPhOG is streaming).</summary>
    bool HasSample { get; }

    /// <summary>
    /// The latest signal value, served synchronously (the legacy
    /// <c>GetValueByName</c> contract). For a streaming source this is the most
    /// recent cached frame; the engine averages several reads per step itself.
    /// </summary>
    double Read();
}
