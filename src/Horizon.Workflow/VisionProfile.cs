using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// A saved vision recipe for one taught position: which camera + lighting to use,
/// a pre-process filter, a detection method (NCC pattern matching for Phase 1) and
/// an optional template. Persisted as a <c>.hvp</c> file in the product's
/// <c>vision\</c> directory and operator-stamped into the SQLite history store.
/// See <c>docs/vision-profile-design.md</c>.
/// </summary>
public sealed record VisionProfile
{
    /// <summary>Stable id, preserved across renames so history stays tied to the profile.</summary>
    public string Uid { get; init; } = string.Empty;

    /// <summary>Profile name — also the <c>.hvp</c> file name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Motion device the camera rides on (e.g. <c>gantry-main</c>) — drives the position combo.</summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>Named position the camera moves to for this inspection.</summary>
    public string PositionName { get; init; } = string.Empty;

    /// <summary>Camera device id used to capture frames.</summary>
    public string CameraId { get; init; } = string.Empty;

    /// <summary>Whether camera auto-exposure is on for this profile.</summary>
    public bool AutoExposure { get; init; } = true;

    /// <summary>Manual exposure in microseconds (used when <see cref="AutoExposure"/> is off).</summary>
    public double ExposureUs { get; init; }

    /// <summary>Lighting device id (empty = none).</summary>
    public string LightingDevice { get; init; } = string.Empty;

    /// <summary>Lighting intensity applied across the device (0 = off).</summary>
    public int LightingIntensity { get; init; }

    /// <summary>
    /// Ordered pre-process filter chain applied before detection (e.g.
    /// binary → canny). Empty = use the raw grayscale image.
    /// </summary>
    public IReadOnlyList<VisionFilter> Filters { get; init; } = Array.Empty<VisionFilter>();

    /// <summary>Detection method id: <c>ncc</c>, <c>circle</c> or <c>line</c>.</summary>
    public string DetectionMethod { get; init; } = "ncc";

    /// <summary>Minimum NCC score (0..1) for a match to count.</summary>
    public double MinScore { get; init; } = 0.7;

    /// <summary>
    /// NCC rotation search half-range in degrees (0 = translation only, 180 = full circle).
    /// When &gt; 0 the detector returns the matched instance's angle.
    /// </summary>
    public double NccAngleRange { get; init; } = 180;

    /// <summary>
    /// Optional rectangular region the detector searches within (null = whole frame).
    /// Lets a profile look only where the feature is expected.
    /// </summary>
    public RoiSpec? SearchRoi { get; init; }

    /// <summary>Circle-detector parameters (used when <see cref="DetectionMethod"/> is <c>circle</c>).</summary>
    public CircleParams Circle { get; init; } = new();

    /// <summary>Line-detector parameters (used when <see cref="DetectionMethod"/> is <c>line</c>).</summary>
    public LineParams Line { get; init; } = new();

    /// <summary>The current template, or null if none has been taught yet (NCC only).</summary>
    public TemplateRef? Template { get; init; }
}

/// <summary>
/// Fired after a named vision profile runs (<c>RunVisionProfileByNameAsync</c>) so a live
/// front-end can flash the detection on its feed. Carries the profile's camera, its name, and
/// the method-agnostic result (line endpoints / centre live in <see cref="VisionDetectResult.Values"/>).
/// </summary>
public sealed record VisionProfileDetection(string CameraId, string ProfileName, VisionDetectResult Result);

/// <summary>
/// A reference to a taught NCC template. The model (<c>.ncm</c>) and the template
/// image (<c>.png</c>) live under <c>vision\templates\{Uid}\</c>; retaking the
/// template mints a new <see cref="Uid"/>. The anchor is template-local (defaults
/// to the template centre) and is added to the match centre when reporting.
/// </summary>
public sealed record TemplateRef
{
    public string Uid { get; init; } = string.Empty;
    public RoiSpec Roi { get; init; } = new(0, 0, 0, 0);
    public double AnchorX { get; init; }
    public double AnchorY { get; init; }

    /// <summary>Anchor reference orientation in degrees (default 0); added to the NCC match angle.</summary>
    public double Rotation { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
}
