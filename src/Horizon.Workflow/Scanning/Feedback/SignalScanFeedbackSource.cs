using Horizon.Common;

namespace Horizon.Workflow.Scanning.Feedback;

/// <summary>
/// Scan feedback backed by the unified signal catalog
/// (<see cref="Signals.ISignalSource"/>) rather than a single engine facet — so a
/// scan can optimise ANY readable signal, including a product source such as
/// FAAM's SiPhOG, exactly the keys the <c>signal.read</c> block offers. Reads are
/// on-demand: each call resolves the current value through the registry (live I/O
/// for SMU / PSU / Measurement, a cached frame for SiPhOG), so no streaming
/// subscription is needed — the averaging loop in <see cref="FeedbackSourceBase"/>
/// just polls this on its natural spacing.
/// </summary>
internal sealed class SignalScanFeedbackSource : FeedbackSourceBase
{
    private readonly Func<string, CancellationToken, Task<Result<double>>> _read;
    private readonly string _key;

    public SignalScanFeedbackSource(
        Func<string, CancellationToken, Task<Result<double>>> read,
        string key, string description, string unit)
    {
        _read = read;
        _key = key;
        Description = string.IsNullOrWhiteSpace(description) ? $"Signal '{key}'" : description;
        Unit = unit ?? string.Empty;
    }

    public override string Description { get; }
    public override string Unit { get; }

    /// <summary>
    /// Primes one read — both to confirm a registered source <em>owns</em> the key
    /// (fail fast when the product signal isn't present on this head) and to set
    /// <see cref="FeedbackSourceBase.HasValidSample"/> for the orchestrator's
    /// first-sample wait.
    /// </summary>
    public override async Task<Result> StartAsync(CancellationToken ct)
    {
        var read = await _read(_key, ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result.Fail(read.Error, $"signal feedback '{_key}': {read.Message}");
        Publish(read.Value);
        return Result.Ok();
    }

    public override async Task<double> ReadAsync(CancellationToken ct)
    {
        var read = await _read(_key, ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Latest();   // hold the last good value over a transient read failure
        Publish(read.Value);
        return read.Value;
    }

    public override Task StopAsync() => Task.CompletedTask;
}
