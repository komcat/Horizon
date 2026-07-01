namespace Horizon.Workflow.UCorrection;

/// <summary>
/// Inputs for the <b>vision</b>-based U correction — the automated counterpart to the
/// operator-driven <see cref="UCorrectionOptions"/>. Each of the four reference points is a
/// named position (taught beforehand) paired with a saved per-position vision profile whose
/// Line detection reports the facet edge angle. The PIC facet line is P1/P2, the FAU facet
/// line is F1/F2; all four positions live on <see cref="GantryDevice"/> and the correction is
/// a relative U move on <see cref="HexapodDevice"/>.
/// </summary>
public sealed record VisionUCorrectionOptions(
    string GantryDevice,
    string HexapodDevice,
    string P1Position, string P1Profile,
    string P2Position, string P2Profile,
    string F1Position, string F1Profile,
    string F2Position, string F2Profile,
    // Settle wait (ms) after arriving at each point, before running its vision profile — lets the
    // stage stop vibrating and a fresh post-move frame stream in.
    int MoveSettleMs = 300,
    // Sign of the applied U rotation; flip to -1 if the hexapod U convention is reversed.
    double Sign = +1.0);

/// <summary>
/// Result of a vision U-correction <b>measurement</b> (no move is applied — the caller decides
/// whether to apply based on <see cref="DeltaDeg"/> and its own thresholds).
/// </summary>
/// <param name="AllFound">True only if the Line detection matched at all four points.</param>
/// <param name="FailedPoint">The first position whose facet wasn't detected, or null when all found.</param>
/// <param name="PicAvgDeg">Mean of the two PIC facet angles (P1, P2).</param>
/// <param name="FauAvgDeg">Mean of the two FAU facet angles (F1, F2).</param>
/// <param name="DeltaDeg">The U rotation the caller would apply: <c>Sign × (PicAvg − FauAvg)</c>.</param>
public sealed record VisionUCorrectionResult(
    bool AllFound,
    string? FailedPoint,
    double P1AngleDeg, double P2AngleDeg,
    double F1AngleDeg, double F2AngleDeg,
    double PicAvgDeg, double FauAvgDeg,
    double DeltaDeg);
