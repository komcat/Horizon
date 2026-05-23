using Horizon.Common;

namespace Horizon.Workflow.Scanning;

/// <summary>Which engine facet supplies the scan feedback signal.</summary>
public enum FeedbackKind
{
    /// <summary>Source-measure unit (<c>ISmuFacet</c>) — true streaming.</summary>
    Smu,
    /// <summary>Current-measurement device (<c>IMeasurementFacet</c>) — fast streaming.</summary>
    Measurement,
    /// <summary>Power supply (<c>IPowerSupplyFacet</c>) — polled (~2 s).</summary>
    PowerSupply,
}

/// <summary>Which scalar of a multi-value source to optimise.</summary>
public enum FeedbackValue
{
    Current,
    Voltage,
}

/// <summary>
/// Picks and configures a feedback source. The scan algorithm stays
/// source-agnostic; only this descriptor names the hardware.
/// </summary>
public sealed record FeedbackDescriptor
{
    public FeedbackKind Kind { get; init; }

    /// <summary>Name of the feedback device in its facet.</summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>1-based channel.</summary>
    public int Channel { get; init; } = 1;

    /// <summary>Which scalar to read (ignored by the current-measurement source).</summary>
    public FeedbackValue ValueType { get; init; } = FeedbackValue.Current;

    /// <summary>Streaming interval (ms) for sources that stream. Ignored when polled.</summary>
    public int IntervalMs { get; init; } = 50;
}

/// <summary>
/// Bridges an event-driven engine facet to the scan algorithm's
/// "give me a number now" expectation. Replaces the legacy global
/// <c>DataStore</c> channel. Implementations cache the latest streamed value
/// and serve it synchronously; non-streaming sources poll on demand.
/// </summary>
public interface IScanFeedbackSource : IAsyncDisposable
{
    /// <summary>Human-readable source name, e.g. <c>"SMU SMU-1 CH1 current"</c>.</summary>
    string Description { get; }

    /// <summary>Unit of the value, e.g. <c>"A"</c>, <c>"V"</c>.</summary>
    string Unit { get; }

    /// <summary>True once at least one valid sample has been observed.</summary>
    bool HasValidSample { get; }

    /// <summary>
    /// Total samples observed since <see cref="StartAsync"/> — a monotonically
    /// rising counter that lets a caller confirm the stream is alive.
    /// </summary>
    long SampleCount { get; }

    /// <summary>Begins streaming (or arms polling). Must be called before reading.</summary>
    Task<Result> StartAsync(CancellationToken ct);

    /// <summary>
    /// Reads one feedback sample — the latest streamed value for streaming
    /// sources, or a fresh poll for non-streaming sources.
    /// </summary>
    Task<double> ReadAsync(CancellationToken ct);

    /// <summary>Averages <paramref name="samples"/> reads spaced by the source's natural interval.</summary>
    Task<double> ReadAveragedAsync(int samples, CancellationToken ct);

    /// <summary>Stops streaming / polling. Idempotent.</summary>
    Task StopAsync();
}
