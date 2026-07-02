namespace Horizon.Engine;

/// <summary>HALCON image statistics for one analyzed frame.</summary>
public sealed record VisionImageStats(
    int Width,
    int Height,
    int Channels,
    double MinGray,
    double MeanGray,
    double MaxGray);

/// <summary>Result of a threshold + connected-components blob count.</summary>
public sealed record VisionBlobResult(
    int BlobCount,
    double TotalArea,
    int ThresholdMin,
    int ThresholdMax);

/// <summary>Which pre-process filter to apply to an image before detection.</summary>
public enum VisionFilterKind
{
    /// <summary>No filtering — use the raw grayscale image.</summary>
    None,
    /// <summary>Binary threshold: pixels in [Min,Max] become foreground (full).</summary>
    BinaryThreshold,
    /// <summary>Gaussian smoothing with a square mask (3 or 5).</summary>
    Gaussian,
    /// <summary>Canny edge detector (HALCON <c>edges_image</c>) — outputs an edge image.</summary>
    CannyEdge,
}

/// <summary>
/// A pre-process filter applied to a frame before NCC matching. Filters chain in
/// order (each consumes the previous one's output). Only the fields relevant to
/// <see cref="Kind"/> are used: threshold uses Min/Max; gaussian uses GaussMask;
/// Canny uses Min/Max as the hysteresis low/high and <see cref="Alpha"/> as the
/// smoothing.
/// </summary>
public sealed record VisionFilter(
    VisionFilterKind Kind = VisionFilterKind.None,
    int ThresholdMin = 0,
    int ThresholdMax = 128,
    int GaussMask = 3,
    double Alpha = 1.0);

/// <summary>An axis-aligned rectangular region of interest, in source pixels.</summary>
public sealed record RoiSpec(int X, int Y, int Width, int Height);

/// <summary>One NCC pattern-match result, in source-frame pixel coordinates.</summary>
public sealed record NccMatch(
    double CenterX,
    double CenterY,
    double Angle,
    double Score,
    double AnchorX,
    double AnchorY);

/// <summary>
/// Raw bytes of a cropped region around a match, for UI display. <see cref="OriginX"/>/
/// <see cref="OriginY"/> are the crop's top-left in source-frame pixels (0,0 for a
/// full-frame image), letting the UI map detection geometry onto the crop.
/// </summary>
public sealed record NccCrop(
    int Width,
    int Height,
    int Channels,
    byte[] Data,
    int OriginX = 0,
    int OriginY = 0);

/// <summary>An NCC match together with the cropped image around it.</summary>
public sealed record NccFindResult(NccMatch Match, NccCrop Crop);

/// <summary>One named scalar in a detection result (e.g. <c>x</c>, <c>score</c>).</summary>
public sealed record VisionResultValue(string Key, double Value);

/// <summary>
/// One sampled point in a detection result, in source-frame pixels, tagged with whether the
/// fit kept it (<see cref="Inlier"/>) — e.g. the per-caliper edge points of the edge-regression
/// detector, so the UI can show which contributed to the line and which were rejected.
/// </summary>
public sealed record VisionPoint(double X, double Y, bool Inlier);

/// <summary>
/// Method-agnostic detection result: an ordered set of named values (so the UI can
/// show them as a key→value grid) plus an optional crop around the hit and an optional
/// set of sampled points. Used by all detection methods (NCC / circle / line / corner /
/// edge-regression).
/// </summary>
public sealed record VisionDetectResult(
    bool Found,
    IReadOnlyList<VisionResultValue> Values,
    NccCrop? Crop,
    IReadOnlyList<VisionPoint>? Points = null);

/// <summary>Parameters for the circle detector (operates on the filtered foreground).</summary>
public sealed record CircleParams(
    double MinRadius = 5,
    double MaxRadius = 200,
    double MinCircularity = 0.7);

/// <summary>Expected orientation of a detected line/edge.</summary>
public enum LineDirection { Any, Horizontal, Vertical }

/// <summary>
/// Expected intensity transition across the edge, in a fixed frame: for a vertical edge
/// <see cref="DarkToBright"/> means brighter on the right; for a horizontal edge, brighter
/// below.
/// </summary>
public enum LinePolarity { Any, DarkToBright, BrightToDark }

/// <summary>
/// Which qualifying edge to keep when several match. <see cref="Forward"/> = first along
/// the scan (left→right for a vertical edge, top→bottom for a horizontal one),
/// <see cref="Backward"/> = last, <see cref="CenterOut"/> = closest to the ROI centre.
/// </summary>
public enum LineScanOrder { Forward, Backward, CenterOut }

/// <summary>Parameters for the line detector (operates on the filtered foreground).</summary>
public sealed record LineParams(
    double MinLength = 20,
    LineDirection Direction = LineDirection.Any,
    LinePolarity Polarity = LinePolarity.Any,
    LineScanOrder ScanOrder = LineScanOrder.Forward);

/// <summary>
/// Which corner of the die a detected corner is. Named by the die corner it represents:
/// e.g. <see cref="TopLeft"/> means the filled region lies down-and-right of the corner
/// point. Reported as a 0..3 code in the result (<c>orientation</c>); <see cref="Any"/> is
/// only an input hint (auto-detect), never a result.
/// </summary>
public enum CornerOrientation { Any, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Parameters for the corner detector — locates a die/rectangle corner by fitting the two
/// straight edges of the thresholded foreground and intersecting them (sub-pixel). Operates
/// on the filtered foreground.
/// </summary>
/// <param name="MinEdgeLength">Shortest contour segment (px) accepted as a die edge.</param>
/// <param name="MaxAngleDevDeg">How far from 90° the two edges may deviate and still count as a corner.</param>
/// <param name="Expected">If not <see cref="CornerOrientation.Any"/>, a detected corner of a
/// different orientation is rejected — lets a profile pin down which of the four corners it wants.</param>
public sealed record CornerParams(
    double MinEdgeLength = 20,
    double MaxAngleDevDeg = 25,
    CornerOrientation Expected = CornerOrientation.Any);

/// <summary>
/// Parameters for the edge-regression detector — measures a ragged / low-contrast edge by
/// laying a stack of caliper boxes along it (each projects across its width to one sub-pixel
/// edge point via HALCON <c>measure_pos</c>), then robustly fits a line through the points,
/// rejecting outlier boxes. Best for edges where a single contour fit fails.
/// </summary>
/// <param name="Direction">Edge orientation. <see cref="LineDirection.Any"/> auto-picks from the
/// search-ROI aspect (taller ⇒ vertical). Sets both the caliper orientation and the regression axis.</param>
/// <param name="CaliperCount">How many caliper boxes to stack along the edge.</param>
/// <param name="CaliperLength">Scan extent (px) across the edge — each caliper's long axis.</param>
/// <param name="CaliperWidth">Averaging extent (px) along the edge — each caliper's short axis.</param>
/// <param name="Polarity">Intensity transition to accept across the edge (maps to <c>measure_pos</c>
/// transition along the scan: dark→bright = positive).</param>
/// <param name="Sigma">Gaussian smoothing of each caliper's 1-D profile.</param>
/// <param name="MinAmplitude">Minimum edge amplitude for a caliper crossing to count.</param>
/// <param name="MaxOutlierDist">Max distance (px) from the fitted line for a point to stay an inlier.</param>
public sealed record EdgeRegParams(
    LineDirection Direction = LineDirection.Vertical,
    int CaliperCount = 15,
    double CaliperLength = 60,
    double CaliperWidth = 20,
    LinePolarity Polarity = LinePolarity.Any,
    double Sigma = 1.0,
    double MinAmplitude = 20,
    double MaxOutlierDist = 3.0);

/// <summary>Metadata returned when an NCC template/model is created from an ROI.</summary>
public sealed record NccTemplateInfo(
    int Width,
    int Height,
    double AnchorX,
    double AnchorY);
