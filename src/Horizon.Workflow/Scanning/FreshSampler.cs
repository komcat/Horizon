namespace Horizon.Workflow.Scanning;

/// <summary>
/// One feedback sample that is guaranteed to have been taken AFTER the last move — the
/// "fresh-sample gate" ported from FireDog's XCAN (<c>measure()</c> in
/// <c>hexapod_align.lua</c>). The instrument integrates continuously, so the sample
/// latched when a move returns may have been integrating while the stage travelled.
/// Instead of a blind settle delay, the gate waits for <c>freshSamples</c> new samples
/// on the source's monotonic <see cref="IScanFeedbackSource.SampleCount"/>: with 2, the
/// second one is entirely post-move. <c>ceilingMs</c> caps the wait — a stalled stream
/// degrades to "use the latest value" and the caller is told (<c>TimedOut</c>).
/// <para>Works for both source kinds: a streaming source (SMU stream) counts its own
/// publishes, so the loop waits for them; a polled source (the signal catalog) counts one
/// sample per <see cref="IScanFeedbackSource.ReadAsync"/>, so <c>freshSamples</c> reads are
/// <c>freshSamples</c> live measurements taken after the move.</para>
/// </summary>
public static class FreshSampler
{
    /// <summary>Poll granularity of the count gate (ms).</summary>
    private const int PollMs = 10;

    /// <summary>
    /// Reads one post-move sample. <paramref name="freshSamples"/> = 0 restores the original
    /// blind settle (sleep <paramref name="ceilingMs"/>, then read). <paramref name="settleMinMs"/>
    /// is a precautionary floor slept first — the gate proves the instrument sample is post-move
    /// but cannot see a slow analogue front-end (photodiode + TIA) in front of it.
    /// </summary>
    public static async Task<(double Value, bool TimedOut)> ReadAsync(
        IScanFeedbackSource source, int freshSamples, int settleMinMs, int ceilingMs, CancellationToken ct)
    {
        if (freshSamples <= 0)
        {
            if (ceilingMs > 0) await Task.Delay(ceilingMs, ct).ConfigureAwait(false);
            return (await source.ReadAsync(ct).ConfigureAwait(false), false);
        }

        long n0 = source.SampleCount;
        if (settleMinMs > 0) await Task.Delay(settleMinMs, ct).ConfigureAwait(false);

        // Bounded by the ceiling (Power of 10 rule 2): at most ceiling / PollMs polls, plus one
        // read per wanted sample for a polled source whose every read is a new sample.
        long started = Environment.TickCount64;
        int maxPolls = Math.Max(0, ceilingMs) / PollMs + freshSamples + 1;
        for (int i = 0; i < maxPolls; i++)
        {
            double value = await source.ReadAsync(ct).ConfigureAwait(false);
            if (source.SampleCount >= n0 + freshSamples) return (value, false);
            if (Environment.TickCount64 - started >= ceilingMs) break;
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }
        return (await source.ReadAsync(ct).ConfigureAwait(false), true);
    }

    /// <summary>Waits until the source has produced its first sample; throws <see cref="TimeoutException"/> after <paramref name="timeoutMs"/>.</summary>
    public static async Task WaitForFirstSampleAsync(IScanFeedbackSource source, int timeoutMs, CancellationToken ct)
    {
        int maxPolls = Math.Max(1, timeoutMs / 20);
        for (int i = 0; i < maxPolls && !source.HasValidSample; i++)
            await Task.Delay(20, ct).ConfigureAwait(false);
        if (!source.HasValidSample)
            throw new TimeoutException($"No feedback sample from {source.Description} within {timeoutMs / 1000.0:0.#} s.");
    }
}
