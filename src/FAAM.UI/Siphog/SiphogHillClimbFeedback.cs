using Horizon.Workflow.HillClimb;

namespace FAAM.UI.Siphog;

/// <summary>
/// Feeds the Layer-2 <see cref="HillClimbEngine"/> from the live SiPhOG stream —
/// the <c>SAG PWR (V)</c> field of the latest frame. This is the Layer-3 half of
/// the <see cref="IHillClimbFeedback"/> seam (the legacy
/// <c>RealtimeDataClass["ActualSagnac"]</c>). Wraps the same <see cref="SiphogClient"/>
/// the Run-page SiPhOG panel owns, so the climb reads exactly what the chart plots.
/// </summary>
public sealed class SiphogHillClimbFeedback : IHillClimbFeedback
{
    private readonly SiphogClient _client;

    public SiphogHillClimbFeedback(SiphogClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Description => "SiPhOG SAG PWR (V)";

    /// <summary>A sample exists once connected and at least one frame has been decoded.</summary>
    public bool HasSample => _client.IsConnected && _client.Latest.HasValue;

    /// <summary>Latest SAG PWR (V); 0 before the first frame (matches the legacy cached-value contract).</summary>
    public double Read() => _client.Latest?.SagPwrV ?? 0.0;
}
