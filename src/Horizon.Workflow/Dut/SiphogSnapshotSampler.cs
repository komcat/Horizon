using System.Diagnostics;
using Horizon.Common;
using Horizon.Workflow.Signals;

namespace Horizon.Workflow;

/// <summary>
/// Samples the six SiPhOG signals through the <see cref="SignalRegistry"/> for a short
/// window and reduces each to mean / min / max / std — the values of one
/// <see cref="SiphogSnapshotSet"/>. The SiPhOG source is the one a Layer 3 head registers
/// (FAAM's <c>SiphogSignalSource</c>); no new seam is needed.
/// </summary>
internal static class SiphogSnapshotSampler
{
    /// <summary>Value key (signal key without <c>siphog.</c>) → unit, in panel order.</summary>
    public static readonly IReadOnlyList<(string Key, string Unit)> Signals = new[]
    {
        ("sledCurrentMa",  "mA"),
        ("photoCurrentUa", "µA"),
        ("sledTempC",      "°C"),
        ("targetSagPwrV",  "V"),
        ("sagPwrV",        "V"),
        ("tecCurrentMa",   "mA"),
    };

    public const int MaxWindowMs = 10_000;
    private const int PollMs = 10;   // SiPhOG streams ≈200 Hz → a fresh frame most polls

    public sealed record Sample(Dictionary<string, SignalStats> Values, int ActualMs, int Samples);

    /// <summary>
    /// Reads every signal once per poll for <paramref name="windowMs"/> (0 = one read).
    /// Fails when not a single read of any signal succeeds (client not connected / no frames).
    /// Bounded: at most <c>windowMs / PollMs + 1</c> polls; cancellable between polls.
    /// </summary>
    public static async Task<Result<Sample>> SampleAsync(SignalRegistry signals, int windowMs, CancellationToken ct)
    {
        windowMs = Math.Clamp(windowMs, 0, MaxWindowMs);
        var acc = Signals.ToDictionary(s => s.Key, _ => new List<double>());
        string? lastError = null;
        int polls = 0;
        int maxPolls = windowMs / PollMs + 1;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < maxPolls; i++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (key, _) in Signals)
            {
                var r = await signals.ReadAsync("siphog." + key, ct).ConfigureAwait(false);
                if (r.IsSuccess && double.IsFinite(r.Value)) acc[key].Add(r.Value);
                else if (!r.IsSuccess) lastError = r.Message;
            }
            polls++;
            if (sw.ElapsedMilliseconds >= windowMs) break;
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }

        var values = new Dictionary<string, SignalStats>();
        foreach (var (key, list) in acc)
            if (list.Count > 0) values[key] = Stats(list);

        if (values.Count == 0)
            return Result<Sample>.Fail(ErrorCode.OperationFailed,
                $"SiPhOG snapshot: no readings ({lastError ?? "no SiPhOG signal source registered"}).");

        return Result<Sample>.Ok(new Sample(values, (int)sw.ElapsedMilliseconds, polls));
    }

    private static SignalStats Stats(List<double> xs)
    {
        double mean = xs.Average();
        double variance = xs.Count > 1 ? xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1) : 0;
        return new SignalStats(mean, xs.Min(), xs.Max(), Math.Sqrt(variance));
    }
}
