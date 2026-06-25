namespace Horizon.Workflow.AutoW;

/// <summary>
/// Run parameters for the <see cref="AutoWEngine"/> — the "Auto W maximizer". The engine
/// is a 1-D line search over the hexapod W (rotation) axis where each W candidate is scored
/// by re-peaking XY first (a small first-decline hill-climb), so W is judged on its true
/// potential rather than the laterally-misaligned signal a raw rotation leaves behind.
/// Plain POCO so a front-end can serialize/edit it (System.Text.Json).
/// </summary>
public sealed class AutoWConfig
{
    /// <summary>1% relative improvement. Floor for the noise-aware effective threshold,
    /// used for BOTH the W direction decision and the peak (T(N) ≤ T(N-1)) stop.</summary>
    public double ImprovementThreshold { get; set; } = 0.01;

    /// <summary>W rotation step per probe (deg).</summary>
    public double WStep_deg { get; set; } = 0.1;

    /// <summary>Hard cap on cumulative W excursion from the start angle (deg, ±). The search
    /// stops and returns to the global best once a step would exceed it (Power of 10 Rule 2 —
    /// a fixed bound, so a noisy signal can't drive an unbounded rotation).</summary>
    public double MaxWExcursion_deg { get; set; } = 2.0;

    /// <summary>Step for the inner XY re-peak (µm) — the "small XY climb" between W steps.</summary>
    public double XYStep_um { get; set; } = 1.0;

    /// <summary>Cap on steps a single inner X/Y first-decline scan may take in one direction
    /// (Power of 10 Rule 2). A travel of <c>cap × XYStep_um</c> bounds the worst case.</summary>
    public int MaxLateralStepsPerDirection { get; set; } = 50;

    /// <summary>Hexapod system (path) velocity set before the run (mm/s). W follows it.</summary>
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

    /// <summary><c>effThreshold = max(ImprovementThreshold, this · σ / V_start)</c>.</summary>
    public double NoiseSafetyMultiplier { get; set; } = 3.0;

    // Axis indices into the 6-DOF position (X,Y,Z,U,V,W).
    public int XAxisIndex { get; set; } = 0;
    public int YAxisIndex { get; set; } = 1;
    public int WAxisIndex { get; set; } = 5;

    public static AutoWConfig Default => new();

    public AutoWConfig Clone() => new()
    {
        ImprovementThreshold = ImprovementThreshold,
        WStep_deg = WStep_deg,
        MaxWExcursion_deg = MaxWExcursion_deg,
        XYStep_um = XYStep_um,
        MaxLateralStepsPerDirection = MaxLateralStepsPerDirection,
        SystemVelocity_mmPerS = SystemVelocity_mmPerS,
        IoTimeoutMs = IoTimeoutMs,
        SettleMs = SettleMs,
        SamplesPerStep = SamplesPerStep,
        SampleDelayMs = SampleDelayMs,
        BaselineSamples = BaselineSamples,
        BaselineSampleDelayMs = BaselineSampleDelayMs,
        NoiseSafetyMultiplier = NoiseSafetyMultiplier,
        XAxisIndex = XAxisIndex,
        YAxisIndex = YAxisIndex,
        WAxisIndex = WAxisIndex,
    };

    public void Validate()
    {
        if (ImprovementThreshold <= 0)
            throw new ArgumentException("ImprovementThreshold must be positive.");
        if (WStep_deg <= 0)
            throw new ArgumentException("WStep_deg must be positive.");
        if (MaxWExcursion_deg <= 0)
            throw new ArgumentException("MaxWExcursion_deg must be positive.");
        if (MaxWExcursion_deg < WStep_deg)
            throw new ArgumentException("MaxWExcursion_deg must be at least one WStep_deg.");
        if (XYStep_um <= 0)
            throw new ArgumentException("XYStep_um must be positive.");
        if (MaxLateralStepsPerDirection < 1)
            throw new ArgumentException("MaxLateralStepsPerDirection must be at least 1.");
        if (SystemVelocity_mmPerS <= 0)
            throw new ArgumentException("SystemVelocity_mmPerS must be positive.");
        if (IoTimeoutMs < 1)
            throw new ArgumentException("IoTimeoutMs must be positive.");
        if (SettleMs < 0 || SampleDelayMs < 0 || BaselineSampleDelayMs < 0)
            throw new ArgumentException("Timing parameters must be non-negative.");
        if (SamplesPerStep < 1 || BaselineSamples < 1)
            throw new ArgumentException("Sample counts must be at least 1.");
        if (XAxisIndex is < 0 or > 5 || YAxisIndex is < 0 or > 5 || WAxisIndex is < 0 or > 5)
            throw new ArgumentException("Axis indices must be in range 0..5.");
    }
}
