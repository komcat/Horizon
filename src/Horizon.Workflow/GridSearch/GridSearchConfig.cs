namespace Horizon.Workflow.GridSearch;

/// <summary>
/// Run parameters for the <see cref="FauGridSearchEngine"/> — the post-FAU-pre-align signal
/// finder. Two stages over the hexapod XY plane, both scored by the SiPhOG Sagnac signal:
/// <list type="number">
///   <item><b>Stage 1</b> — a 1-D X line search: jump <see cref="LineJumpUm"/> in +X, then step
///         back in −X by <see cref="LineStepUm"/> for <see cref="LineSteps"/> steps, watching for
///         the signal to clear the baseline by <see cref="DetectDeltaV"/> and then crest.</item>
///   <item><b>Stage 2</b> — only if Stage 1 saw nothing: a full serpentine XY raster over
///         ±<see cref="GridExtentUm"/> at <see cref="GridStepUm"/> pitch, stopping at the first
///         node that clears the detect level so the caller can hand off to the hill climber.</item>
/// </list>
/// Plain POCO so a front-end can serialize/edit it (System.Text.Json). Mirrors the
/// <c>AutoWConfig</c>/<c>HillClimbConfig</c> shape (timing, sampling, I/O timeout, axis indices).
/// </summary>
public sealed class GridSearchConfig
{
    // ---- Stage 1: X line search ----

    /// <summary>Initial +X excursion from centre (µm) before the downward sweep — the line "apex".</summary>
    public double LineJumpUm { get; set; } = 60.0;
    /// <summary>Step size for the −X sweep (µm).</summary>
    public double LineStepUm { get; set; } = 5.0;
    /// <summary>Number of −X steps after the apex (apex + this = sample count). 60→−60 at 5 µm = 24.</summary>
    public int LineSteps { get; set; } = 24;
    /// <summary>Index of the line-scan axis into the 6-DOF pose (X,Y,Z,U,V,W). Default X.</summary>
    public int LineAxisIndex { get; set; }

    // ---- Stage 2: XY grid fallback ----

    /// <summary>Half-extent per axis for the grid (µm); the raster spans centre ± this on X and Y.</summary>
    public double GridExtentUm { get; set; } = 60.0;
    /// <summary>Grid pitch between adjacent nodes (µm).</summary>
    public double GridStepUm { get; set; } = 5.0;
    /// <summary>Grid X axis index into the 6-DOF pose. Default X.</summary>
    public int GridXAxisIndex { get; set; }
    /// <summary>Grid Y axis index into the 6-DOF pose. Default Y.</summary>
    public int GridYAxisIndex { get; set; } = 1;

    /// <summary>
    /// Hard cap on the grid node count — a bounded-loop guard (Power of 10 Rule 2: no runaway
    /// sweep). ±60 µm at 5 µm pitch is 25×25 = 625; the cap forces a coarser step or tighter
    /// extent before a sweep could explode.
    /// </summary>
    public int MaxGridNodes { get; set; } = 4096;

    // ---- Signal detection ----

    /// <summary>
    /// Absolute rise over the baseline (V) that counts as "signal seen": <c>v &gt; vBase + this</c>.
    /// Absolute (not relative) so a near-zero or negative baseline (e.g. −0.031 V) is handled
    /// cleanly — a percentage threshold blows up around zero.
    /// </summary>
    public double DetectDeltaV { get; set; } = 0.1;

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

    public GridSearchConfig Clone() => new()
    {
        LineJumpUm = LineJumpUm,
        LineStepUm = LineStepUm,
        LineSteps = LineSteps,
        LineAxisIndex = LineAxisIndex,
        GridExtentUm = GridExtentUm,
        GridStepUm = GridStepUm,
        GridXAxisIndex = GridXAxisIndex,
        GridYAxisIndex = GridYAxisIndex,
        MaxGridNodes = MaxGridNodes,
        DetectDeltaV = DetectDeltaV,
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
        if (GridExtentUm <= 0) throw new ArgumentException("GridExtentUm must be positive.");
        if (GridStepUm <= 0) throw new ArgumentException("GridStepUm must be positive.");
        if (GridStepUm > GridExtentUm) throw new ArgumentException("GridStepUm must not exceed GridExtentUm.");
        if (MaxGridNodes < 1) throw new ArgumentException("MaxGridNodes must be at least 1.");
        if (DetectDeltaV <= 0) throw new ArgumentException("DetectDeltaV must be positive.");
        if (DropFraction is <= 0 or >= 1) throw new ArgumentException("DropFraction must be in (0, 1).");
        if (DropConfirmCount < 1) throw new ArgumentException("DropConfirmCount must be at least 1.");
        if (SystemVelocity_mmPerS <= 0) throw new ArgumentException("SystemVelocity_mmPerS must be positive.");
        if (IoTimeoutMs < 1) throw new ArgumentException("IoTimeoutMs must be positive.");
        if (SettleMs < 0 || SampleDelayMs < 0 || BaselineSampleDelayMs < 0)
            throw new ArgumentException("Timing parameters must be non-negative.");
        if (SamplesPerStep < 1 || BaselineSamples < 1)
            throw new ArgumentException("Sample counts must be at least 1.");
        if (LineAxisIndex is < 0 or > 5 || GridXAxisIndex is < 0 or > 5 || GridYAxisIndex is < 0 or > 5)
            throw new ArgumentException("Axis indices must be in range 0..5.");
        if (GridXAxisIndex == GridYAxisIndex)
            throw new ArgumentException("Grid X and Y axes must differ.");
    }
}
