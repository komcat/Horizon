using Horizon.Common;

namespace Horizon.Workflow;

/// <summary>
/// Per-block settings for an alignment run started by the <c>align.correction</c> /
/// <c>align.uCorrection</c> / <c>align.placeFau</c> steps, handed to the Layer 3 head via
/// <see cref="IUtilityAppHost.RunAsync"/>. Every member is optional: <c>null</c> keeps the
/// head's own default (its fixed reference-point names, the Run-page target setting, or the
/// routine's default), so a block without these params runs exactly as the panel's Start.
/// </summary>
public sealed record AlignmentRunOverrides
{
    // --- U correction: the four gantry reference points + the U guard ---
    public string? UPicPoint1 { get; init; }
    public string? UPicPoint2 { get; init; }
    public string? UFauPoint1 { get; init; }
    public string? UFauPoint2 { get; init; }

    /// <summary>Refuse a U rotation larger than this (deg).</summary>
    public double? MaxUDeg { get; init; }

    // --- Accuracy place FAU (FAU pre-align): the four points, target vector, guards ---
    public string? PlaceOut1 { get; init; }
    public string? PlaceOut2 { get; init; }
    public string? PlaceFiber1 { get; init; }
    public string? PlaceFiber2 { get; init; }

    /// <summary>Desired PIC↔FAU gap (µm) — the master vector's length.</summary>
    public double? TargetDistanceUm { get; init; }

    /// <summary>Angle (deg) of the master vector.</summary>
    public double? TargetAngleDeg { get; init; }

    /// <summary>Waveguide tilt (deg) for the hex Y+ follow-up (gap · tan angle); 0 = none.</summary>
    public double? WaveguideAngleDeg { get; init; }

    /// <summary>Refuse a FAU move longer than this (mm).</summary>
    public double? MaxPlaceCorrectionMm { get; init; }

    /// <summary>Range-checks the numeric members that are set; names are checked by the head (must be taught).</summary>
    public Result Validate()
    {
        if (MaxUDeg is { } u && !(u > 0 && u <= 10))
            return Bad($"Max U must be > 0 and ≤ 10° (got {u}).");
        if (TargetDistanceUm is { } d && !(d > 0 && d <= 2000))
            return Bad($"Gap must be > 0 and ≤ 2000 µm (got {d}).");
        if (TargetAngleDeg is { } a && (double.IsNaN(a) || Math.Abs(a) > 180))
            return Bad($"Target angle must be within ±180° (got {a}).");
        if (WaveguideAngleDeg is { } w && (double.IsNaN(w) || Math.Abs(w) >= 45))
            return Bad($"Waveguide angle must be within ±45° (got {w}).");
        if (MaxPlaceCorrectionMm is { } m && !(m > 0 && m <= 5))
            return Bad($"Max FAU correction must be > 0 and ≤ 5 mm (got {m}).");
        return Result.Ok();
    }

    private static Result Bad(string message) => Result.Fail(ErrorCode.InvalidParameter, message);
}
