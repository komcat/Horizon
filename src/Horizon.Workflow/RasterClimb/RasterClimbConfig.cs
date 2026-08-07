namespace Horizon.Workflow.RasterClimb;

/// <summary>How each Z plane is searched for its maximum.</summary>
public enum RasterPlaneScan
{
    /// <summary>
    /// Cross scan (default): a bidirectional line scan along A, move to that axis's best,
    /// then the same along B. Each direction stops early once the signal is past its peak,
    /// so a plane typically costs ~10–20 points instead of the full grid. Assumes the peak
    /// is roughly axis-separable, which holds for fibre-to-chip coupling.
    /// </summary>
    Cross,

    /// <summary>
    /// Full square spiral outward from the plane centre over the whole (2N+1)² grid — no
    /// assumption about the peak's shape, at the cost of every point. With
    /// <see cref="RasterClimbConfig.RingsWithoutImprovementStop"/> &gt; 0 the spiral still
    /// stops expanding once successive rings stop improving.
    /// </summary>
    Spiral,
}

/// <summary>
/// Run parameters for the <see cref="RasterClimbEngine"/> — a Z-stepped XY raster
/// peak-hold search. Plain POCO so a front-end can serialize/edit it and override
/// the step sizes per run.
/// </summary>
public sealed class RasterClimbConfig
{
    /// <summary>How each plane is searched — see <see cref="RasterPlaneScan"/>.</summary>
    public RasterPlaneScan PlaneScan { get; set; } = RasterPlaneScan.Cross;

    /// <summary>
    /// Consecutive non-improving samples that mean "past the peak" and end a
    /// <see cref="RasterPlaneScan.Cross"/> line-scan direction. 1 is twitchy on a noisy
    /// signal; 2 (default) tolerates a single bad sample.
    /// </summary>
    public int PeakDropCount { get; set; } = 2;

    /// <summary>
    /// Relative gain over the running best that counts as an improvement, so sensor noise
    /// doesn't read as "still climbing" (or as a drop). Also decides ties in favour of the
    /// point nearer the centre.
    /// </summary>
    public double ImprovementThreshold { get; set; } = 0.02;

    /// <summary>
    /// <see cref="RasterPlaneScan.Spiral"/> only: stop expanding after this many consecutive
    /// spiral rings fail to improve the plane's best — the 2D equivalent of "past the peak",
    /// since the signal falls off with radius. 0 disables it (always sweep the full grid).
    /// </summary>
    public int RingsWithoutImprovementStop { get; set; } = 2;

    /// <summary>
    /// <see cref="RasterPlaneScan.Cross"/> only: how many A→B passes to run per plane. One
    /// pass is enough for a separable peak; a second pass helps when the peak is tilted, at
    /// roughly double the points.
    /// </summary>
    public int CrossPasses { get; set; } = 1;

    /// <summary>How far the stage advances along Z between rasters (µm). Default 1 µm.</summary>
    public double ZStep_um { get; set; } = 1.0;

    /// <summary>Spacing between adjacent raster sample points in the XY plane (µm). Default 0.5 µm.</summary>
    public double XyStep_um { get; set; } = 0.5;

    /// <summary>
    /// Half-width of the raster in steps: the grid spans −<see cref="XyStepsPerSide"/> …
    /// +<see cref="XyStepsPerSide"/> on both in-plane axes, centred on the position the
    /// plane started at. Default 6 → a 13 × 13 grid (169 points).
    /// </summary>
    public int XyStepsPerSide { get; set; } = 6;

    /// <summary>
    /// Target signal (V) — normally the SiPhOG <c>TARGET SAG PWR</c>. The run stops as soon
    /// as a plane's best reaches <see cref="TargetReachedFraction"/> of this. Must be &gt; 0:
    /// unlike a spiral this search has no radius budget, so the target is its exit criterion.
    /// </summary>
    public double TargetSagnacV { get; set; }

    /// <summary>Fraction of <see cref="TargetSagnacV"/> that counts as "reached" (0.85 = 85%).</summary>
    public double TargetReachedFraction { get; set; } = 0.85;

    /// <summary>
    /// Total Z+ travel the climb may spend (µm) — the operational budget. Default 20 µm:
    /// the FAU should reach the PIC well inside that, so needing more means something is
    /// wrong and the climb should stop rather than keep driving in.
    /// </summary>
    public double MaxZTravel_um { get; set; } = 20.0;

    /// <summary>
    /// Absolute cap on how many Z steps the climb may take, whatever the step size
    /// (Power of 10 Rule 2 — a fixed, statically-provable bound). The effective budget is
    /// the smaller of this and <see cref="MaxZTravel_um"/> ÷ <see cref="ZStep_um"/>.
    /// </summary>
    public int MaxZSteps { get; set; } = 2000;

    /// <summary>
    /// Suspected-contact guard: if a completed plane's best falls more than this fraction
    /// below the best seen so far, the climb stops (<see cref="RasterClimbStopReason.SignalDropped"/>)
    /// and retreats to the peak. A real climb's signal rises or plateaus as the FAU closes on
    /// the PIC — a sustained collapse means the FAU is touching something, and continuing to
    /// step Z+ would push into it. 0 disables the guard.
    /// </summary>
    public double ContactDropFraction { get; set; } = 0.10;

    /// <summary>
    /// Per-operation timeout (ms) on every hardware await — move and position read
    /// (Power of 10 Rule 2, temporal: a hung controller or feedback socket can't stall
    /// the climb forever despite the step cap).
    /// </summary>
    public int IoTimeoutMs { get; set; } = 15_000;

    /// <summary>Wait (ms) after a raster move before sampling.</summary>
    public int SettleMs { get; set; } = 60;
    /// <summary>Extra wait (ms) after a Z step, before the plane's raster begins.</summary>
    public int ZSettleMs { get; set; } = 200;
    /// <summary>Reads averaged per raster point.</summary>
    public int SamplesPerPoint { get; set; } = 3;
    /// <summary>Spacing (ms) between per-point reads.</summary>
    public int SampleDelayMs { get; set; } = 5;

    // Axis indices into the 6-DOF position (X,Y,Z,U,V,W). The raster runs in the A/B
    // plane; the climb advances along Z.
    /// <summary>First in-plane axis index (default X = 0).</summary>
    public int AxisIndexA { get; set; } = 0;
    /// <summary>Second in-plane axis index (default Y = 1).</summary>
    public int AxisIndexB { get; set; } = 1;
    /// <summary>Climb axis index (default Z = 2).</summary>
    public int AxisIndexZ { get; set; } = 2;

    /// <summary>Sign of the climb direction along Z; +1 = Z+ (the FAU approaching the PIC).</summary>
    public double ZSign { get; set; } = +1.0;

    /// <summary>
    /// Rasters the starting plane before the first Z step. Off by default: the caller
    /// normally arrives from a coarse search that already peaked the current plane.
    /// </summary>
    public bool RasterStartPlane { get; set; }

    public static RasterClimbConfig Default => new();

    public RasterClimbConfig Clone() => new()
    {
        PlaneScan = PlaneScan,
        PeakDropCount = PeakDropCount,
        ImprovementThreshold = ImprovementThreshold,
        RingsWithoutImprovementStop = RingsWithoutImprovementStop,
        CrossPasses = CrossPasses,
        ZStep_um = ZStep_um,
        XyStep_um = XyStep_um,
        XyStepsPerSide = XyStepsPerSide,
        TargetSagnacV = TargetSagnacV,
        TargetReachedFraction = TargetReachedFraction,
        MaxZTravel_um = MaxZTravel_um,
        MaxZSteps = MaxZSteps,
        ContactDropFraction = ContactDropFraction,
        IoTimeoutMs = IoTimeoutMs,
        SettleMs = SettleMs,
        ZSettleMs = ZSettleMs,
        SamplesPerPoint = SamplesPerPoint,
        SampleDelayMs = SampleDelayMs,
        AxisIndexA = AxisIndexA,
        AxisIndexB = AxisIndexB,
        AxisIndexZ = AxisIndexZ,
        ZSign = ZSign,
        RasterStartPlane = RasterStartPlane,
    };

    /// <summary>
    /// Worst-case points sampled per plane: the full <c>(2N+1)²</c> grid for a spiral,
    /// or the two full lines (centre shared) per pass for a cross scan. Early stops only
    /// ever reduce it; the readouts use it as the progress denominator.
    /// </summary>
    public int PointsPerRaster => PlaneScan == RasterPlaneScan.Cross
        ? 1 + CrossPasses * 8 * XyStepsPerSide   // 4 directions × up to 2N cells each
        : (2 * XyStepsPerSide + 1) * (2 * XyStepsPerSide + 1);

    /// <summary>
    /// Z steps the climb may actually take — the travel budget, floored by the absolute
    /// step cap, and never less than one step.
    /// </summary>
    public int EffectiveZSteps => Math.Max(1, Math.Min(MaxZSteps, (int)Math.Floor(MaxZTravel_um / ZStep_um)));

    public void Validate()
    {
        if (PeakDropCount is < 1 or > 20)
            throw new ArgumentException("PeakDropCount must be in 1..20.");
        if (ImprovementThreshold is < 0 or > 1)
            throw new ArgumentException("ImprovementThreshold must be in [0, 1].");
        if (RingsWithoutImprovementStop is < 0 or > 100)
            throw new ArgumentException("RingsWithoutImprovementStop must be in 0..100.");
        if (CrossPasses is < 1 or > 5)
            throw new ArgumentException("CrossPasses must be in 1..5.");
        if (ZStep_um <= 0)
            throw new ArgumentException("ZStep_um must be positive.");
        if (XyStep_um <= 0)
            throw new ArgumentException("XyStep_um must be positive.");
        if (XyStepsPerSide is < 1 or > 100)
            throw new ArgumentException("XyStepsPerSide must be in 1..100.");
        if (TargetSagnacV <= 0)
            throw new ArgumentException("TargetSagnacV must be positive — it is this search's only exit criterion.");
        if (TargetReachedFraction is <= 0 or > 1)
            throw new ArgumentException("TargetReachedFraction must be in (0, 1].");
        if (MaxZTravel_um <= 0)
            throw new ArgumentException("MaxZTravel_um must be positive.");
        if (MaxZTravel_um < ZStep_um)
            throw new ArgumentException("MaxZTravel_um must be at least one Z step.");
        if (MaxZSteps is < 1 or > 10_000)
            throw new ArgumentException("MaxZSteps must be in 1..10000.");
        if (ContactDropFraction is < 0 or >= 1)
            throw new ArgumentException("ContactDropFraction must be in [0, 1).");
        if (IoTimeoutMs < 1)
            throw new ArgumentException("IoTimeoutMs must be positive.");
        if (SettleMs < 0 || ZSettleMs < 0 || SampleDelayMs < 0)
            throw new ArgumentException("Timing parameters must be non-negative.");
        if (SamplesPerPoint < 1)
            throw new ArgumentException("SamplesPerPoint must be at least 1.");
        if (AxisIndexA is < 0 or > 5 || AxisIndexB is < 0 or > 5 || AxisIndexZ is < 0 or > 5)
            throw new ArgumentException("Axis indices must be in range 0..5.");
        if (AxisIndexA == AxisIndexB || AxisIndexA == AxisIndexZ || AxisIndexB == AxisIndexZ)
            throw new ArgumentException("Plane axes and the climb axis must all differ.");
        if (ZSign is not (1.0 or -1.0))
            throw new ArgumentException("ZSign must be +1 or -1.");
    }
}
