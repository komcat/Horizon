namespace Horizon.Workflow.UCorrection;

/// <summary>
/// Inputs for the <b>vision</b>-based U correction — the automated counterpart to the
/// operator-driven <see cref="UCorrectionOptions"/>. Two reference points are visited: one
/// looking at the PIC facet, one at the FAU facet. Each is a named position (taught
/// beforehand) paired with a saved per-position vision profile whose edge/line detection
/// reports that facet's angle. Both positions live on <see cref="GantryDevice"/> and the
/// correction is a relative U move on <see cref="HexapodDevice"/>.
/// </summary>
public sealed record VisionUCorrectionOptions(
    string GantryDevice,
    string HexapodDevice,
    string PicPosition, string PicProfile,
    string FauPosition, string FauProfile,
    // Settle wait (ms) after arriving at each point, before running its vision profile — lets the
    // stage stop vibrating and a fresh post-move frame stream in.
    int MoveSettleMs = 300,
    // Sign of the applied U rotation; flip to -1 if the hexapod U convention is reversed.
    double Sign = +1.0);

/// <summary>
/// Result of a vision U-correction <b>measurement</b> (no move is applied — the caller decides
/// whether to apply based on <see cref="DeltaDeg"/> and its own thresholds).
/// </summary>
/// <param name="AllFound">True only if the facet was detected at both points.</param>
/// <param name="FailedPoint">The first position whose facet wasn't detected, or null when both found.</param>
/// <param name="PicAngleDeg">The PIC facet angle as reported by its vision profile.</param>
/// <param name="FauAngleDeg">The FAU facet angle as reported by its vision profile.</param>
/// <param name="DeltaDeg">The U rotation the caller would apply: <c>Sign × (PicAngle − FauAngle)</c>,
/// wrapped into (−90, +90] because the reported angle is modulo 180°.</param>
public sealed record VisionUCorrectionResult(
    bool AllFound,
    string? FailedPoint,
    double PicAngleDeg, double FauAngleDeg,
    double DeltaDeg);
