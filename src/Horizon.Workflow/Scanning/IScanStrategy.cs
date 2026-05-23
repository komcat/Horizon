using Horizon.Common;

namespace Horizon.Workflow.Scanning;

/// <summary>
/// A scan algorithm. Port of the legacy <c>IScanStrategy</c>. The strategy
/// drives hardware only through the <see cref="ScanContext"/>; cancellation of
/// <paramref name="ct"/> is the halt signal.
/// </summary>
public interface IScanStrategy
{
    /// <summary>Strategy name, for logging.</summary>
    string Name { get; }

    /// <summary>
    /// Runs the scan. Throws <see cref="OperationCanceledException"/> if halted.
    /// A non-success <see cref="Result"/> signals an internal failure.
    /// </summary>
    Task<Result> ExecuteAsync(ScanContext context, CancellationToken ct);
}
