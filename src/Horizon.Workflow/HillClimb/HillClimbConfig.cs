namespace Horizon.Workflow.HillClimb;

/// <summary>
/// Run parameters for the <see cref="HillClimbEngine"/> — an exact port of the
/// legacy <c>acs_solution_saa</c> <c>HillClimberConfig</c>. Plain POCO so a
/// front-end can serialize/edit it (System.Text.Json) and override
/// <see cref="ZBudget_um"/> per run from the Distance input.
/// </summary>
public sealed class HillClimbConfig
{
    /// <summary>1% relative improvement. Floor for the effective threshold.</summary>
    public double ImprovementThreshold { get; set; } = 0.01;

    /// <summary>Coarse-regime Z step (remaining &gt; <see cref="MidRemainingThreshold_um"/>).</summary>
    public double ZStepInitial_um { get; set; } = 2.0;
    /// <summary>Coarse-regime XY step.</summary>
    public double XYStepInitial_um { get; set; } = 1.0;
    /// <summary>Z step when remaining ≤ <see cref="MidRemainingThreshold_um"/>.</summary>
    public double ZStepMid_um { get; set; } = 1.0;
    /// <summary>Step for all axes when remaining ≤ <see cref="FineRemainingThreshold_um"/>.</summary>
    public double StepFine_um { get; set; } = 0.5;

    /// <summary>Remaining-budget breakpoint into the mid regime (must be &gt; <see cref="FineRemainingThreshold_um"/>).</summary>
    public double MidRemainingThreshold_um { get; set; } = 30.0;
    /// <summary>Remaining-budget breakpoint into the fine regime.</summary>
    public double FineRemainingThreshold_um { get; set; } = 10.0;

    /// <summary>Hard cap on cumulative Z+ travel (µm). Overridden per-run from the Distance input.</summary>
    public double ZBudget_um { get; set; } = 99.0;

    /// <summary>Consecutive non-improving Z+ steps before falling out to lateral correction.</summary>
    public int ZStallLimit { get; set; } = 3;

    /// <summary>
    /// Hard cap on the number of steps a single lateral (X/Y) hill-climb may take in one
    /// direction (Power of 10 Rule 2 — every loop has a fixed upper bound, so the lateral
    /// scan cannot step indefinitely on a noisy signal). A travel of
    /// <c>cap × XYStepInitial_um</c> bounds the worst case.
    /// </summary>
    public int MaxLateralStepsPerDirection { get; set; } = 50;

    /// <summary>
    /// Per-operation timeout (ms) on every hardware await — move and position read
    /// (Power of 10 Rule 2, temporal: bound the time of each I/O so a hung controller
    /// or feedback socket can't stall the climb forever despite the iteration caps).
    /// </summary>
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
    public int ZAxisIndex { get; set; } = 2;
    public int XAxisIndex { get; set; } = 0;
    public int YAxisIndex { get; set; } = 1;

    public static HillClimbConfig Default => new();

    public HillClimbConfig Clone() => new()
    {
        ImprovementThreshold = ImprovementThreshold,
        ZStepInitial_um = ZStepInitial_um,
        XYStepInitial_um = XYStepInitial_um,
        ZStepMid_um = ZStepMid_um,
        StepFine_um = StepFine_um,
        MidRemainingThreshold_um = MidRemainingThreshold_um,
        FineRemainingThreshold_um = FineRemainingThreshold_um,
        ZBudget_um = ZBudget_um,
        ZStallLimit = ZStallLimit,
        MaxLateralStepsPerDirection = MaxLateralStepsPerDirection,
        IoTimeoutMs = IoTimeoutMs,
        SettleMs = SettleMs,
        SamplesPerStep = SamplesPerStep,
        SampleDelayMs = SampleDelayMs,
        BaselineSamples = BaselineSamples,
        BaselineSampleDelayMs = BaselineSampleDelayMs,
        NoiseSafetyMultiplier = NoiseSafetyMultiplier,
        ZAxisIndex = ZAxisIndex,
        XAxisIndex = XAxisIndex,
        YAxisIndex = YAxisIndex,
    };

    public void Validate()
    {
        if (ImprovementThreshold <= 0)
            throw new ArgumentException("ImprovementThreshold must be positive.");
        if (ZStepInitial_um <= 0 || XYStepInitial_um <= 0 || ZStepMid_um <= 0 || StepFine_um <= 0)
            throw new ArgumentException("Step sizes must be positive.");
        if (ZBudget_um <= 0)
            throw new ArgumentException("ZBudget_um must be positive.");
        if (ZStallLimit < 1)
            throw new ArgumentException("ZStallLimit must be at least 1.");
        if (MaxLateralStepsPerDirection < 1)
            throw new ArgumentException("MaxLateralStepsPerDirection must be at least 1.");
        if (IoTimeoutMs < 1)
            throw new ArgumentException("IoTimeoutMs must be positive.");
        if (SettleMs < 0 || SampleDelayMs < 0 || BaselineSampleDelayMs < 0)
            throw new ArgumentException("Timing parameters must be non-negative.");
        if (SamplesPerStep < 1 || BaselineSamples < 1)
            throw new ArgumentException("Sample counts must be at least 1.");
        if (MidRemainingThreshold_um <= FineRemainingThreshold_um)
            throw new ArgumentException("MidRemainingThreshold_um must be greater than FineRemainingThreshold_um.");
        if (ZAxisIndex is < 0 or > 5 || XAxisIndex is < 0 or > 5 || YAxisIndex is < 0 or > 5)
            throw new ArgumentException("Axis indices must be in range 0..5.");
    }
}
