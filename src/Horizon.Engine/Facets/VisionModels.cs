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
/// Method-agnostic detection result: an ordered set of named values (so the UI can
/// show them as a key→value grid) plus an optional crop around the hit. Used by all
/// detection methods (NCC / circle / line).
/// </summary>
public sealed record VisionDetectResult(
    bool Found,
    IReadOnlyList<VisionResultValue> Values,
    NccCrop? Crop);

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

/// <summary>Metadata returned when an NCC template/model is created from an ROI.</summary>
public sealed record NccTemplateInfo(
    int Width,
    int Height,
    double AnchorX,
    double AnchorY);
