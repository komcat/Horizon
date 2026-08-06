using Horizon.Workflow.HillClimb;
using Horizon.Workflow.SpiralScan;

namespace FAAM.UI.Siphog;

/// <summary>
/// Adapts the alignment <see cref="IHillClimbFeedback"/> seam (the live SiPhOG
/// <c>SAG PWR (V)</c>, registered by the Run page) to the
/// <see cref="ISpiralSearchFeedback"/> the <see cref="SpiralSearchEngine"/> reads.
/// Lets the Engineer Testing Ground spiral reuse the exact same signal source the
/// hill climber uses, without a second SiPhOG connection.
/// </summary>
public sealed class HillClimbToSpiralFeedback : ISpiralSearchFeedback
{
    private readonly IHillClimbFeedback _inner;

    public HillClimbToSpiralFeedback(IHillClimbFeedback inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Description => _inner.Description;
    public bool HasSample => _inner.HasSample;
    public double Read() => _inner.Read();
}
