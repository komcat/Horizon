using Horizon.Common;

namespace Horizon.Workflow.Xcan;

/// <summary>
/// Configuration of one XCAN pass — the coordinate-descent, coarse-to-fine active
/// alignment ported from FireDog's <c>hexapod_align.lua</c> (itself bobcat's
/// <c>CoarseToFineStrategy</c> plus FireDog's cycle-time work). Distances are in
/// millimetres; signal levels (<see cref="ExitThreshold"/>, <see cref="ZoneLow"/>,
/// <see cref="ZoneHigh"/>, <see cref="MinSignal"/>) are in the feedback signal's own
/// unit (amps for a Keithley current). Defaults are the FireDog script defaults, except
/// <see cref="SettleMinMs"/> (see its remarks).
/// </summary>
public sealed record XcanParameters
{
    /// <summary>Axes descended, in order. The gap axis first, so the transverse scans run near coupling.</summary>
    public IReadOnlyList<string> Axes { get; init; } = new[] { "Z", "Y", "X" };

    /// <summary>The coarse → fine step ladder (mm). With <see cref="Adaptive"/> it doubles as the zone steps.</summary>
    public IReadOnlyList<double> StepsMm { get; init; } = new[] { 0.010, 0.005, 0.002, 0.001 };

    /// <summary>New samples per measurement (fresh-sample gate); 0 = the original blind <see cref="SettleCeilingMs"/> sleep.</summary>
    public int FreshSamples { get; init; } = 2;

    /// <summary>Ceiling of the fresh-sample wait (ms) — not a delay; the wait ends as soon as the samples land.</summary>
    public int SettleCeilingMs { get; init; } = 300;

    /// <summary>
    /// Precautionary floor slept before every measurement (ms). FireDog's production value is
    /// 50. It is also the default here because Horizon's move returns when the controller stops
    /// reporting motion — there is no PI on-target (<c>qONT</c>) check behind it as in FireDog.
    /// </summary>
    public int SettleMinMs { get; init; } = 50;

    /// <summary>Samples averaged for the baseline — the only averaging in the scan.</summary>
    public int AvgCount { get; init; } = 3;

    /// <summary>A direction ends after this many consecutive non-improving steps.</summary>
    public int ConsecLimit { get; init; } = 3;

    /// <summary>A direction ends at once on a relative drop above this (%). 15, not bobcat's 5: noise dips reach ~6 %.</summary>
    public double SigDropPct { get; init; } = 15;

    /// <summary>Per-direction travel cap (mm).</summary>
    public double MaxTravelMm { get; init; } = 0.2;

    /// <summary>After each axis, drive back to the global peak when it beats the current reading by more than this (%).</summary>
    public double ImprovePct { get; init; } = 1;

    /// <summary>Abort before moving when |baseline| is below this (0 = off).</summary>
    public double MinSignal { get; init; }

    /// <summary>Stop-and-stay target: the first sample ≥ this ends the scan where it stands (0 = off).</summary>
    public double ExitThreshold { get; init; }

    /// <summary>Scan velocity, set once up front so every micro-move has the same profile (mm/s; 0 = keep).</summary>
    public double VelocityMmPerSec { get; init; } = 1.0;

    /// <summary>Signal-zoned ladder: passes coarser than the current signal zone's step are skipped.</summary>
    public bool Adaptive { get; init; }

    /// <summary>Lower edge of the adaptive zone range. Must sit at or above the hand-off signal level.</summary>
    public double ZoneLow { get; init; } = 1e-6;

    /// <summary>Upper edge of the adaptive zone range.</summary>
    public double ZoneHigh { get; init; } = 400e-6;

    /// <summary>Skip the opposite direction when the first one walked uphill (the peak is bracketed).</summary>
    public bool SkipOpposite { get; init; } = true;

    /// <summary>Start each axis with the direction that last improved it.</summary>
    public bool DirMemory { get; init; } = true;

    /// <summary>Do not re-measure a point that was just measured.</summary>
    public bool ReuseSample { get; init; } = true;

    /// <summary>Log one CSV line per sample (<c>xcan,seq,t_s,phase,axis,dir,step_um,pos…,v,dv,dmove,note</c>).</summary>
    public bool Trace { get; init; }

    /// <summary>Arrival tolerance (mm): a best point within this of where the stage stands needs no move.</summary>
    public double TolMm { get; init; } = 0.001;

    /// <summary>Per-move timeout (ms).</summary>
    public int MoveTimeoutMs { get; init; } = 10_000;

    /// <summary>Hard cap on any one step (mm) — a typo in µm must not become a 10 mm hexapod jump.</summary>
    public const double MaxStepMm = 0.1;

    /// <summary>Hard cap on the per-direction travel (mm).</summary>
    public const double MaxTravelCapMm = 1.0;

    public Result Validate()
    {
        if (Axes.Count == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN: no axes.");
        foreach (var a in Axes)
            if (!Scanning.ScanAxisMotion.IsAxis(a))
                return Result.Fail(ErrorCode.InvalidParameter, $"XCAN: unknown axis '{a}'.");
        if (StepsMm.Count == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN: the step ladder is empty.");
        foreach (var s in StepsMm)
            if (!Scanning.ScanLimits.InRange(s, 0, MaxStepMm))
                return Result.Fail(ErrorCode.InvalidParameter,
                    $"XCAN: step {s * 1000:0.###} µm is out of range (0, {MaxStepMm * 1000:0} µm].");
        if (!Scanning.ScanLimits.InRange(MaxTravelMm, 0, MaxTravelCapMm))
            return Result.Fail(ErrorCode.InvalidParameter,
                $"XCAN: max travel {MaxTravelMm * 1000:0.#} µm is out of range (0, {MaxTravelCapMm * 1000:0} µm].");
        if (AvgCount < 1 || ConsecLimit < 1)
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN: avg count and consecutive limit must be ≥ 1.");
        if (FreshSamples < 0 || SettleCeilingMs < 0 || SettleMinMs < 0 || SettleMinMs > 5000)
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN: fresh samples / settle times out of range.");
        if (SigDropPct <= 0 || ImprovePct < 0)
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN: drop % must be > 0 and improve % ≥ 0.");
        if (Adaptive && !(Scanning.ScanLimits.Positive(ZoneLow) && ZoneHigh > ZoneLow))
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN: adaptive zones need 0 < zone low < zone high.");
        if (!Scanning.ScanLimits.Positive(TolMm) || MoveTimeoutMs < 100)
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN: tolerance must be > 0 and move timeout ≥ 100 ms.");
        return Result.Ok();
    }
}

/// <summary>The outcome of one XCAN pass.</summary>
public sealed record XcanResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Halted { get; init; }

    /// <summary>Averaged reading before anything moved.</summary>
    public double Baseline { get; init; }

    /// <summary>The confirming reading where the stage was left (the threshold point, else the global peak).</summary>
    public double Best { get; init; }

    /// <summary>The best single sample seen anywhere during the pass.</summary>
    public double Peak { get; init; }

    public bool ExitHit { get; init; }
    public int PassesRun { get; init; }
    public int PassesSkipped { get; init; }
    public int Samples { get; init; }
    public TimeSpan Duration { get; init; }

    public double Delta => Best - Baseline;

    /// <summary>Best / baseline, 0 unless both are real positive signal (a ratio of dark noise means nothing).</summary>
    public double Gain => Baseline > 0 && Best > 0 ? Best / Baseline : 0;
}
