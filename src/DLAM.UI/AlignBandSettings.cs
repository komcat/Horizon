using Horizon.Common;

namespace DLAM.UI;

/// <summary>
/// The Run page's alignment band (<c>dlam_align_band.json</c>, Machine › Settings): which
/// hexapods hold the lenses, which SMU channel is the feedback, and the XCAN / climb / two-lens
/// presets its buttons run. Levels are in µA (FireDog's convention for the Keithley photocurrent).
/// </summary>
public sealed record AlignBandSettings
{
    public const string FileName = "dlam_align_band";

    public string LeftDevice { get; init; } = "hex-left";
    public string RightDevice { get; init; } = "hex-right";

    /// <summary>SMU whose channels the band reads and scans on (ch1 / ch2 readouts).</summary>
    public string Smu { get; init; } = "Keithley_2602B";

    /// <summary>SMU channel the scans maximise (catalog <c>smu.{Smu}.ch{n}.current</c>).</summary>
    public int FeedbackChannel { get; init; } = 1;

    /// <summary>Axis set every scan covers (order is always Z, Y, X).</summary>
    public string Axes { get; init; } = "XYZ";

    public string CoarseStepsUm { get; init; } = "10,5,2,1";
    public string FineStepsUm { get; init; } = "2,1,0.5";

    /// <summary>Per-lens XCAN exit target (µA); 0 = scan the whole ladder.</summary>
    public double ExitTargetUa { get; init; }

    /// <summary>Refuse to scan below this starting photocurrent (µA) — the light is probably off.</summary>
    public double MinSignalUa { get; init; } = 1;

    /// <summary>Two-lens XCAN target (µA) — FireDog production 6800.</summary>
    public double TwoLensTargetUa { get; init; } = 6800;

    /// <summary>Two-lens adaptive zone low (µA).</summary>
    public double ZoneLowUa { get; init; } = 200;

    /// <summary>Climb: expected peak (µA); the climb stops at 85 % of it.</summary>
    public double ClimbTargetUa { get; init; } = 6800;

    public double ClimbZStepUm { get; init; } = 1;
    public double ClimbMaxZUm { get; init; } = 20;

    /// <summary>The catalog key of the feedback signal.</summary>
    public string Signal => $"smu.{Smu}.ch{FeedbackChannel}.current";

    public static AlignBandSettings Load()
        => ConfigLoader.LoadJson<AlignBandSettings>(ConfigLoader.GetConfigDirectory(), FileName) ?? new AlignBandSettings();

    public void Save() => ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, this);

    /// <summary>The first problem with these settings, or null when they are usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(LeftDevice) || string.IsNullOrWhiteSpace(RightDevice))
            return "both lens hexapods must be named";
        if (string.IsNullOrWhiteSpace(Smu))
            return "the SMU must be named";
        if (FeedbackChannel is < 1 or > 2)
            return "the feedback channel must be 1 or 2";
        if (Axes.Length == 0 || Axes.Any(c => !"XYZUVW".Contains(c)))
            return "the axes must be letters of XYZUVW (e.g. XYZ)";
        if (!IsLadder(CoarseStepsUm) || !IsLadder(FineStepsUm))
            return "a step ladder must be numbers in µm, each above 0 and at most 100, comma-separated";
        if (!IsLevel(ExitTargetUa) || !IsLevel(MinSignalUa) || !IsLevel(ZoneLowUa))
            return "levels must be 0 or more";
        if (!IsPositive(TwoLensTargetUa) || !IsPositive(ClimbTargetUa))
            return "the two-lens and climb targets must be above 0";
        if (!IsPositive(ClimbZStepUm) || !IsPositive(ClimbMaxZUm) || ClimbMaxZUm > 200)
            return "the climb Z step must be above 0 and the Z budget above 0 and at most 200 µm";
        return null;
    }

    private static bool IsLevel(double v) => double.IsFinite(v) && v >= 0;

    private static bool IsPositive(double v) => double.IsFinite(v) && v > 0;

    private static bool IsLadder(string text)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && parts.All(p =>
            double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v > 0 && v <= 100);
    }
}
