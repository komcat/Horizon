namespace Horizon.Workflow.GridSearch;

/// <summary>
/// Run parameters for the <see cref="FauGridSearchEngine"/> — the post-FAU-pre-align signal
/// finder. Two stages over the hexapod XY plane, both scored by the SiPhOG Sagnac signal:
/// <list type="number">
///   <item><b>Stage 1</b> — a 1-D X line search: jump <see cref="LineJumpUm"/> in +X, then step
///         back in −X by <see cref="LineStepUm"/> for <see cref="LineSteps"/> steps, watching for
///         the signal to clear the baseline by <see cref="DetectDeltaV"/> and then crest. With
///         <see cref="LineYSweepEnabled"/>, each −X step also sweeps Y ±<see cref="LineYRangeUm"/>;
///         any reading above <see cref="ExitAboveV"/> stops the search there.</item>
///   <item><b>Stage 2</b> — only if Stage 1 saw nothing: step Y+ for <see cref="GridYRows"/> rows at
///         <see cref="GridStepUm"/> pitch and run a Stage-1-style X line scan at each row, stopping at
///         the first row that clears the detect level so the caller can hand off to the hill climber.</item>
/// </list>
/// Plain POCO so a front-end can serialize/edit it (System.Text.Json). Mirrors the
/// <c>AutoWConfig</c>/<c>HillClimbConfig</c> shape (timing, sampling, I/O timeout, axis indices).
/// </summary>
public sealed class GridSearchConfig
{
    // ---- Stage 1: X line search ----

    /// <summary>When false, Stage 1 (the X line search) is skipped entirely — the run goes straight to Stage 2.</summary>
    public bool Stage1Enabled { get; set; } = true;

    /// <summary>Initial +X excursion from centre (µm) before the downward sweep — the line "apex".</summary>
    public double LineJumpUm { get; set; } = 60.0;
    /// <summary>Step size for the −X sweep (µm).</summary>
    public double LineStepUm { get; set; } = 5.0;
    /// <summary>Number of −X steps after the apex (apex + this = sample count). 60→−60 at 5 µm = 24.</summary>
    public int LineSteps { get; set; } = 24;
    /// <summary>Index of the line-scan axis into the 6-DOF pose (X,Y,Z,U,V,W). Default X.</summary>
    public int LineAxisIndex { get; set; }

    /// <summary>
    /// When true, every −X step of Stage 1 is followed by a Y sweep: ±<see cref="LineYRangeUm"/>
    /// about the row's Y at <see cref="LineYStepUm"/> pitch (on <see cref="GridYAxisIndex"/>).
    /// </summary>
    public bool LineYSweepEnabled { get; set; } = true;
    /// <summary>Half-width of the per-X-step Y sweep (µm): the sweep covers −this … +this.</summary>
    public double LineYRangeUm { get; set; } = 50.0;
    /// <summary>Y pitch of the per-X-step sweep (µm).</summary>
    public double LineYStepUm { get; set; } = 10.0;

    /// <summary>
    /// Absolute stop level (V): the moment any search reading exceeds this, the search ends and
    /// parks at that point (signal found → hill-climb hand-off). 0 disables the early stop.
    /// </summary>
    public double ExitAboveV { get; set; } = 0.1;

    // ---- Stage 2: Y+ line-scan fallback ----

    /// <summary>When false, the Stage 2 fallback is skipped — if Stage 1 finds nothing the run reports "no signal".</summary>
    public bool Stage2Enabled { get; set; } = true;

    /// <summary>Number of Y+ rows to test; the search only steps Y in the + direction from centre.</summary>
    public int GridYRows { get; set; } = 12;
    /// <summary>Y+ pitch between rows (µm) — how far Y advances between successive X line scans.</summary>
    public double GridStepUm { get; set; } = 5.0;
    /// <summary>Grid Y axis index into the 6-DOF pose. Default Y. (The per-row X scan uses <see cref="LineAxisIndex"/>.)</summary>
    public int GridYAxisIndex { get; set; } = 1;

    /// <summary>
    /// Hard cap on the total node count — a bounded-loop guard (Power of 10 Rule 2: no runaway
    /// sweep). Stage 2 measures <c>GridYRows × (LineSteps + 1)</c> nodes; the cap forces fewer rows
    /// or a shorter X line before a sweep could explode.
    /// </summary>
    public int MaxGridNodes { get; set; } = 4096;

    // ---- Signal detection ----

    /// <summary>
    /// Minimum rise (V) over the noise floor that counts as signal. The floor is the running median
    /// of every reading (the unit's own dark level — e.g. −0.031, −0.024 or +0.002 V; the baseline
    /// alone until 3 readings), and a reading is significant when
    /// <c>v &gt; floor + max(NoiseK × baseline σ, DetectDeltaV)</c>. Absolute (not a percentage) so a
    /// near-zero or negative floor is handled cleanly.
    /// </summary>
    public double DetectDeltaV { get; set; } = 0.005;

    /// <summary>Significant rise = this × the baseline noise (σ), but at least <see cref="DetectDeltaV"/>.</summary>
    public double NoiseK { get; set; } = 6;

    /// <summary>
    /// Stop at the first significant point (after re-measuring it) and park there, instead of finishing
    /// the line and judging the crest. If the start pose already sees light, the floor drops once the
    /// sweep reaches the dark and the start pose itself becomes that point.
    /// </summary>
    public bool StopOnSignal { get; set; } = true;

    /// <summary>
    /// Early-exit drop, as a fraction of the peak's height above baseline. Once signal is seen,
    /// the sweep has "passed the crest" when <c>(peak − v) &gt; this · (peak − vBase)</c>. Measuring
    /// the drop against (peak − vBase) — guaranteed ≥ <see cref="DetectDeltaV"/> &gt; 0 once signal
    /// is seen — keeps the test well-defined regardless of the baseline's sign.
    /// </summary>
    public double DropFraction { get; set; } = 0.3;

    /// <summary>Consecutive past-crest readings required to confirm the early exit (noise guard).</summary>
    public int DropConfirmCount { get; set; } = 2;

    // ---- Timing / sampling (mirrors AutoW/HillClimb) ----

    /// <summary>Hexapod system (path) velocity set before the run (mm/s).</summary>
    public double SystemVelocity_mmPerS { get; set; } = 1.0;

    /// <summary>Per-operation timeout (ms) on every hardware await — move and position read
    /// (Power of 10 Rule 2, temporal: a hung controller or feedback socket can't stall forever).</summary>
    public int IoTimeoutMs { get; set; } = 15000;

    /// <summary>Wait (ms) after a move settles before sampling.</summary>
    public int SettleMs { get; set; } = 200;
    /// <summary>Reads averaged per per-step measurement.</summary>
    public int SamplesPerStep { get; set; } = 5;
    /// <summary>Spacing (ms) between per-step reads.</summary>
    public int SampleDelayMs { get; set; } = 10;

    /// <summary>Reads for the one-time baseline mean/σ.</summary>
    public int BaselineSamples { get; set; } = 20;
    /// <summary>Spacing (ms) between baseline reads.</summary>
    public int BaselineSampleDelayMs { get; set; } = 20;

    public static GridSearchConfig Default => new();

    /// <summary>Y sweep points on each side of centre: ⌊<see cref="LineYRangeUm"/> / <see cref="LineYStepUm"/>⌋ (50/10 → 5).</summary>
    public int LineYHalfSteps => (int)Math.Floor((LineYRangeUm / LineYStepUm) + 1e-9);

    public GridSearchConfig Clone() => new()
    {
        Stage1Enabled = Stage1Enabled,
        Stage2Enabled = Stage2Enabled,
        LineJumpUm = LineJumpUm,
        LineStepUm = LineStepUm,
        LineSteps = LineSteps,
        LineAxisIndex = LineAxisIndex,
        LineYSweepEnabled = LineYSweepEnabled,
        LineYRangeUm = LineYRangeUm,
        LineYStepUm = LineYStepUm,
        ExitAboveV = ExitAboveV,
        GridYRows = GridYRows,
        GridStepUm = GridStepUm,
        GridYAxisIndex = GridYAxisIndex,
        MaxGridNodes = MaxGridNodes,
        DetectDeltaV = DetectDeltaV,
        NoiseK = NoiseK,
        StopOnSignal = StopOnSignal,
        DropFraction = DropFraction,
        DropConfirmCount = DropConfirmCount,
        SystemVelocity_mmPerS = SystemVelocity_mmPerS,
        IoTimeoutMs = IoTimeoutMs,
        SettleMs = SettleMs,
        SamplesPerStep = SamplesPerStep,
        SampleDelayMs = SampleDelayMs,
        BaselineSamples = BaselineSamples,
        BaselineSampleDelayMs = BaselineSampleDelayMs,
    };

    public void Validate()
    {
        if (LineJumpUm <= 0) throw new ArgumentException("LineJumpUm must be positive.");
        if (LineStepUm <= 0) throw new ArgumentException("LineStepUm must be positive.");
        if (LineSteps < 1) throw new ArgumentException("LineSteps must be at least 1.");
        if (LineYSweepEnabled)
        {
            if (LineYStepUm <= 0) throw new ArgumentException("LineYStepUm must be positive.");
            if (LineYRangeUm < LineYStepUm) throw new ArgumentException("LineYRangeUm must be at least LineYStepUm.");
            // Stage-1 node count with the sweep: apex + LineSteps × (centre + 2·half-steps).
            long stage1Nodes = 1 + ((long)LineSteps * (1 + (2 * LineYHalfSteps)));
            if (stage1Nodes > MaxGridNodes)
                throw new ArgumentException(
                    $"Stage 1 with Y sweep = {stage1Nodes} nodes exceeds the {MaxGridNodes}-node cap — " +
                    "reduce line steps / Y range or raise the cap.");
        }
        if (ExitAboveV < 0) throw new ArgumentException("ExitAboveV must be ≥ 0 (0 disables the early stop).");
        if (GridYRows < 1) throw new ArgumentException("GridYRows must be at least 1.");
        if (GridStepUm <= 0) throw new ArgumentException("GridStepUm must be positive.");
        if (MaxGridNodes < 1) throw new ArgumentException("MaxGridNodes must be at least 1.");
        if (DetectDeltaV <= 0) throw new ArgumentException("DetectDeltaV must be positive.");
        if (!double.IsFinite(NoiseK) || NoiseK < 0) throw new ArgumentException("NoiseK must be ≥ 0.");
        if (DropFraction is <= 0 or >= 1) throw new ArgumentException("DropFraction must be in (0, 1).");
        if (DropConfirmCount < 1) throw new ArgumentException("DropConfirmCount must be at least 1.");
        if (SystemVelocity_mmPerS <= 0) throw new ArgumentException("SystemVelocity_mmPerS must be positive.");
        if (IoTimeoutMs < 1) throw new ArgumentException("IoTimeoutMs must be positive.");
        if (SettleMs < 0 || SampleDelayMs < 0 || BaselineSampleDelayMs < 0)
            throw new ArgumentException("Timing parameters must be non-negative.");
        if (SamplesPerStep < 1 || BaselineSamples < 1)
            throw new ArgumentException("Sample counts must be at least 1.");
        if (LineAxisIndex is < 0 or > 5 || GridYAxisIndex is < 0 or > 5)
            throw new ArgumentException("Axis indices must be in range 0..5.");
        if (LineAxisIndex == GridYAxisIndex)
            throw new ArgumentException("The X line axis and the Y row axis must differ.");
    }
}
