using Horizon.Common;

namespace Horizon.Workflow.ClimbScan;

/// <summary>How much of an XY plane a climb measures.</summary>
public enum ClimbPlaneScan
{
    /// <summary>Bidirectional line along A to its best, then along B — a handful of points; assumes an axis-separable peak.</summary>
    Cross,

    /// <summary>Square spiral outward over the (2N+1)² grid, assuming nothing about the peak's shape.</summary>
    Spiral,
}

/// <summary>
/// Configuration of a climb scan — the gap-closing Z + XY plane search ported from FireDog's
/// <c>climb_raster.lua</c> (itself an evolution of Horizon's deprecated <c>RasterClimbEngine</c>;
/// this is a fresh port, not a revival). Distances in millimetres; <see cref="Target"/> in the
/// feedback signal's unit. Defaults are FireDog's except <see cref="SettleMinMs"/> (see
/// <see cref="Xcan.XcanParameters.SettleMinMs"/>).
/// </summary>
public sealed record ClimbScanParameters
{
    /// <summary>The climb axis.</summary>
    public string ZAxis { get; init; } = "Z";

    /// <summary>The first searched plane axis (A).</summary>
    public string PlaneAxisA { get; init; } = "X";

    /// <summary>The second searched plane axis (B).</summary>
    public string PlaneAxisB { get; init; } = "Y";

    /// <summary>+1 = the climb axis' positive direction closes the gap; −1 = negative.</summary>
    public int ZDir { get; init; } = 1;

    /// <summary>Travel per climb step (mm).</summary>
    public double ZStepMm { get; init; } = 0.001;

    /// <summary>Extra wait after each Z step (ms).</summary>
    public int ZSettleMs { get; init; } = 200;

    /// <summary>Plane grid pitch (mm).</summary>
    public double XyStepMm { get; init; } = 0.0005;

    /// <summary>Plane half-width in cells (6 = 13 × 13).</summary>
    public int XySteps { get; init; } = 6;

    public ClimbPlaneScan PlaneScan { get; init; } = ClimbPlaneScan.Cross;

    /// <summary>Cross: non-improvements that mean "past the peak" this way.</summary>
    public int PeakDrop { get; init; } = 2;

    /// <summary>Cross: A → B passes per plane.</summary>
    public int CrossPasses { get; init; } = 1;

    /// <summary>Spiral: rings without improvement before it stops expanding (0 = the full grid).</summary>
    public int RingsStop { get; init; } = 2;

    /// <summary>Noise margin on "is this better?" (%), scaled by |reference| so a negative signal works.</summary>
    public double ImprovePct { get; init; } = 2;

    /// <summary>Z-probe fast path: while one Z step alone gains more than this (%), skip the plane search. 0 = search every plane.</summary>
    public double ZProbePct { get; init; } = 5;

    /// <summary>Force a plane search after this many skipped planes in a row (0 = unlimited).</summary>
    public int MaxSkips { get; init; } = 5;

    /// <summary>The exit target — REQUIRED: this search has no area budget, so the target is what ends it.</summary>
    public double Target { get; init; }

    /// <summary>The fraction of <see cref="Target"/> that counts as reached (%).</summary>
    public double TargetPct { get; init; } = 85;

    /// <summary>Total climb travel budget (mm).</summary>
    public double MaxZMm { get; init; } = 0.020;

    /// <summary>Absolute climb-step cap, whatever the step size.</summary>
    public int MaxZSteps { get; init; } = 2000;

    /// <summary>A plane best this far under the run's peak (%) is read as contact — retreat. 0 = off.</summary>
    public double ContactDropPct { get; init; } = 10;

    /// <summary>Stop after this many searched planes without a new run best (0 = off).</summary>
    public int StallPlanes { get; init; }

    /// <summary>Search the starting plane before the first Z step.</summary>
    public bool RasterStart { get; init; }

    public int FreshSamples { get; init; } = 2;
    public int SettleCeilingMs { get; init; } = 300;
    public int SettleMinMs { get; init; } = 50;
    public int AvgCount { get; init; } = 3;
    public double VelocityMmPerSec { get; init; } = 1.0;
    public bool Trace { get; init; }
    public int MoveTimeoutMs { get; init; } = 10_000;

    /// <summary>Hard caps — a typo in µm must not become a crash into the part.</summary>
    public const double MaxStepCapMm = 0.05;
    public const double MaxZCapMm = 0.2;
    public const int MaxXyStepsCap = 50;

    public Result Validate()
    {
        var axes = new[] { ZAxis, PlaneAxisA, PlaneAxisB };
        foreach (var a in axes)
            if (!Scanning.ScanAxisMotion.IsAxis(a))
                return Fail($"unknown axis '{a}'");
        if (axes.Select(a => a.Trim().ToUpperInvariant()).Distinct().Count() != 3)
            return Fail("the plane axes and the climb axis must all differ");
        if (ZDir is not (1 or -1))
            return Fail("zDir must be +1 or -1");
        if (!Scanning.ScanLimits.InRange(ZStepMm, 0, MaxStepCapMm) || !Scanning.ScanLimits.InRange(XyStepMm, 0, MaxStepCapMm))
            return Fail($"Z / XY step must be in (0, {MaxStepCapMm * 1000:0} µm]");
        if (!Scanning.ScanLimits.InRange(MaxZMm, 0, MaxZCapMm))
            return Fail($"max Z travel must be in (0, {MaxZCapMm * 1000:0} µm]");
        if (MaxZSteps < 1)
            return Fail("max Z steps must be ≥ 1");
        if (XySteps < 1 || XySteps > MaxXyStepsCap)
            return Fail($"XY half-width must be 1..{MaxXyStepsCap} cells");
        if (!Scanning.ScanLimits.Positive(Target))
            return Fail("the target must be > 0 — it is this search's only exit criterion");
        if (!Scanning.ScanLimits.InRange(TargetPct, 0, 100))
            return Fail("target % must be in (0, 100]");
        if (PeakDrop < 1 || CrossPasses < 1 || RingsStop < 0 || MaxSkips < 0 || StallPlanes < 0)
            return Fail("peak drop / cross passes must be ≥ 1; rings stop / max skips / stall planes ≥ 0");
        if (ImprovePct < 0 || ZProbePct < 0 || ContactDropPct < 0)
            return Fail("percentages must be ≥ 0");
        if (AvgCount < 1 || FreshSamples < 0 || SettleCeilingMs < 0 || SettleMinMs < 0 || ZSettleMs < 0)
            return Fail("avg count must be ≥ 1 and settle times ≥ 0");
        return Result.Ok();

        static Result Fail(string why) => Result.Fail(ErrorCode.InvalidParameter, $"Climb scan: {why}.");
    }
}

/// <summary>Why a climb ended.</summary>
public enum ClimbStop
{
    /// <summary>A plane best (or a probe) reached <c>TargetPct</c> of the target.</summary>
    Target,
    /// <summary>A plane best collapsed under the peak — suspected contact; retreated.</summary>
    Contact,
    /// <summary><c>StallPlanes</c> planes without a new run best.</summary>
    Stall,
    /// <summary>The Z travel budget was spent.</summary>
    ZBudget,
    /// <summary>Stopped by the operator or failed.</summary>
    Aborted,
}

/// <summary>The outcome of a climb scan. The stage is left on the best point of the whole run.</summary>
public sealed record ClimbScanResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Halted { get; init; }
    public ClimbStop Stop { get; init; } = ClimbStop.Aborted;
    public double Baseline { get; init; }
    public double Best { get; init; }

    /// <summary>A confirming reading after the final park on the best point.</summary>
    public double Final { get; init; }
    public double Target { get; init; }

    /// <summary>Signed climb travel at the end (µm).</summary>
    public double ZTravelUm { get; init; }

    /// <summary>Where the best point was: climb travel and plane offsets from the plane centre (µm).</summary>
    public double BestZUm { get; init; }
    public double BestAUm { get; init; }
    public double BestBUm { get; init; }
    public int PlanesSearched { get; init; }
    public int PlanesSkipped { get; init; }
    public int Points { get; init; }

    public bool ExitHit => Stop == ClimbStop.Target;
    public double TargetFraction => Target > 0 ? Best / Target : 0;
    public double Gain => Baseline > 0 && Best > 0 ? Best / Baseline : 0;
}
