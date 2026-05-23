using Horizon.Common;

namespace Horizon.Workflow.Scanning.Feedback;

/// <summary>
/// Shared base for feedback adapters — a thread-safe latest-sample cache and
/// the averaging loop. Facet events fire on a background poll thread, so the
/// cached value is guarded by a lock.
/// </summary>
internal abstract class FeedbackSourceBase : IScanFeedbackSource
{
    private readonly object _lock = new();
    private double _latest;
    private bool _hasSample;
    private long _sampleCount;

    public abstract string Description { get; }
    public abstract string Unit { get; }

    public bool HasValidSample
    {
        get { lock (_lock) { return _hasSample; } }
    }

    public long SampleCount => Interlocked.Read(ref _sampleCount);

    /// <summary>Called by adapters when a new sample arrives (from any thread).</summary>
    protected void Publish(double value)
    {
        lock (_lock)
        {
            _latest = value;
            _hasSample = true;
        }
        Interlocked.Increment(ref _sampleCount);
    }

    /// <summary>Newest cached sample (0 before the first sample).</summary>
    protected double Latest()
    {
        lock (_lock) { return _latest; }
    }

    /// <summary>Spacing between averaged reads — large enough to land fresh samples.</summary>
    protected virtual int SampleSpacingMs => 50;

    public abstract Task<Result> StartAsync(CancellationToken ct);
    public abstract Task StopAsync();

    public virtual Task<double> ReadAsync(CancellationToken ct) => Task.FromResult(Latest());

    public async Task<double> ReadAveragedAsync(int samples, CancellationToken ct)
    {
        if (samples <= 1)
            return await ReadAsync(ct).ConfigureAwait(false);

        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            sum += await ReadAsync(ct).ConfigureAwait(false);
            if (i < samples - 1)
                await Task.Delay(SampleSpacingMs, ct).ConfigureAwait(false);
        }
        return sum / samples;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
