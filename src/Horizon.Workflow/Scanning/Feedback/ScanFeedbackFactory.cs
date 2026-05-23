using Horizon.Engine;

namespace Horizon.Workflow.Scanning.Feedback;

/// <summary>Builds the right <see cref="IScanFeedbackSource"/> for a descriptor.</summary>
public static class ScanFeedbackFactory
{
    /// <summary>
    /// Creates a feedback source over the given engine. The caller owns the
    /// returned source and must dispose it (which stops streaming).
    /// </summary>
    public static IScanFeedbackSource Create(IHorizonEngine engine, FeedbackDescriptor descriptor)
        => descriptor.Kind switch
        {
            FeedbackKind.Smu => new SmuFeedbackSource(engine, descriptor),
            FeedbackKind.Measurement => new MeasurementFeedbackSource(engine, descriptor),
            FeedbackKind.PowerSupply => new PowerSupplyFeedbackSource(engine, descriptor),
            _ => throw new ArgumentOutOfRangeException(
                nameof(descriptor), descriptor.Kind, "Unknown feedback kind."),
        };
}
