namespace Horizon.Workflow.SpiralScan;

/// <summary>
/// Run parameters for the <see cref="SpiralSearchEngine"/> — a constant-pitch
/// outward square-spiral signal search in a single motion plane. Plain POCO so a
/// front-end can serialize/edit it (System.Text.Json) and override
/// <see cref="Pitch_um"/> / <see cref="MaxRadius_um"/> per run.
/// </summary>
public sealed class SpiralSearchConfig
{
    /// <summary>
    /// Spacing between adjacent sample points — and, for a unit-step square spiral,
    /// the gap between successive spiral arms. Default 5 µm (0.005 mm).
    /// </summary>
    public double Pitch_um { get; set; } = 5.0;

    /// <summary>
    /// Hard cap on how far the spiral travels from the start point (Euclidean, µm).
    /// The engine stops before making any step that would cross this radius
    /// (<c>SpiralSearchStopReason.RadiusReached</c>).
    /// </summary>
    public double MaxRadius_um { get; set; } = 50.0;

    /// <summary>
    /// Absolute upper bound on the number of points visited (Power of 10 Rule 2 —
    /// a fixed, statically-provable cap so the spiral can never walk unbounded even
    /// if the radius budget is misconfigured). Sized well above any normal run.
    /// </summary>
    public int MaxPoints { get; set; } = 100_000;

    /// <summary>
    /// Target signal (V). When &gt; 0 the search stops early as soon as the running
    /// best reaches <see cref="TargetReachedFraction"/> of this value
    /// (<c>SpiralSearchStopReason.TargetReached</c>). 0 disables the target exit.
    /// </summary>
    public double TargetSagnacV { get; set; }

    /// <summary>Fraction of <see cref="TargetSagnacV"/> that counts as "reached" (0.90 = 90%).</summary>
    public double TargetReachedFraction { get; set; } = 0.90;

    /// <summary>
    /// Minimum relative gain over the running best that counts as a new best. Floor
    /// for the noise-aware effective threshold (see <see cref="NoiseSafetyMultiplier"/>),
    /// so sensor noise doesn't park the "best" on a spurious spike.
    /// </summary>
    public double ImprovementThreshold { get; set; } = 0.01;

    /// <summary><c>effThreshold = max(ImprovementThreshold, this · σ / V_start)</c>.</summary>
    public double NoiseSafetyMultiplier { get; set; } = 3.0;

    /// <summary>
    /// Per-operation timeout (ms) on every hardware await — move and position read
    /// (Power of 10 Rule 2, temporal: a hung controller or feedback socket can't
    /// stall the search forever despite the point cap).
    /// </summary>
    public int IoTimeoutMs { get; set; } = 15_000;

    /// <summary>Wait (ms) after a move settles before sampling.</summary>
    public int SettleMs { get; set; } = 200;
    /// <summary>Reads averaged per per-point measurement.</summary>
    public int SamplesPerPoint { get; set; } = 5;
    /// <summary>Spacing (ms) between per-point reads.</summary>
    public int SampleDelayMs { get; set; } = 10;

    /// <summary>Reads for the one-time baseline mean/σ (feeds the effective threshold).</summary>
    public int BaselineSamples { get; set; } = 20;
    /// <summary>Spacing (ms) between baseline reads.</summary>
    public int BaselineSampleDelayMs { get; set; } = 20;

    // Plane axes: indices into the 6-DOF position (X,Y,Z,U,V,W). Default plane is the
    // lateral X/Y plane — A = X (0), B = Y (1). The spiral moves only these two axes.
    /// <summary>First in-plane axis index (default X = 0).</summary>
    public int AxisIndexA { get; set; } = 0;
    /// <summary>Second in-plane axis index (default Y = 1).</summary>
    public int AxisIndexB { get; set; } = 1;

    public static SpiralSearchConfig Default => new();

    public SpiralSearchConfig Clone() => new()
    {
        Pitch_um = Pitch_um,
        MaxRadius_um = MaxRadius_um,
        MaxPoints = MaxPoints,
        TargetSagnacV = TargetSagnacV,
        TargetReachedFraction = TargetReachedFraction,
        ImprovementThreshold = ImprovementThreshold,
        NoiseSafetyMultiplier = NoiseSafetyMultiplier,
        IoTimeoutMs = IoTimeoutMs,
        SettleMs = SettleMs,
        SamplesPerPoint = SamplesPerPoint,
        SampleDelayMs = SampleDelayMs,
        BaselineSamples = BaselineSamples,
        BaselineSampleDelayMs = BaselineSampleDelayMs,
        AxisIndexA = AxisIndexA,
        AxisIndexB = AxisIndexB,
    };

    public void Validate()
    {
        if (Pitch_um <= 0)
            throw new ArgumentException("Pitch_um must be positive.");
        if (MaxRadius_um <= 0)
            throw new ArgumentException("MaxRadius_um must be positive.");
        if (MaxRadius_um < Pitch_um)
            throw new ArgumentException("MaxRadius_um must be at least one pitch.");
        if (MaxPoints < 1)
            throw new ArgumentException("MaxPoints must be at least 1.");
        if (TargetSagnacV < 0)
            throw new ArgumentException("TargetSagnacV must be non-negative (0 disables the target exit).");
        if (TargetReachedFraction is <= 0 or > 1)
            throw new ArgumentException("TargetReachedFraction must be in (0, 1].");
        if (ImprovementThreshold <= 0)
            throw new ArgumentException("ImprovementThreshold must be positive.");
        if (IoTimeoutMs < 1)
            throw new ArgumentException("IoTimeoutMs must be positive.");
        if (SettleMs < 0 || SampleDelayMs < 0 || BaselineSampleDelayMs < 0)
            throw new ArgumentException("Timing parameters must be non-negative.");
        if (SamplesPerPoint < 1 || BaselineSamples < 1)
            throw new ArgumentException("Sample counts must be at least 1.");
        if (AxisIndexA is < 0 or > 5 || AxisIndexB is < 0 or > 5)
            throw new ArgumentException("Axis indices must be in range 0..5.");
        if (AxisIndexA == AxisIndexB)
            throw new ArgumentException("Plane axes must differ (AxisIndexA != AxisIndexB).");
    }
}
