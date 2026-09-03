using Horizon.Engine;

namespace Horizon.Workflow.Registration;

/// <summary>
/// One taught reference point of a gripped object: the camera device pose the point was seen
/// from, where it was marked in the image (source px) and the same point in the camera
/// device's frame (mm). <see cref="Source"/> is <c>manual</c> or <c>vision:&lt;profile&gt;</c>.
/// </summary>
public sealed record GrippedPoint(
    int Index,
    MotionPosition CameraPose,
    double PixelX, double PixelY,
    double CameraFrameX, double CameraFrameY,
    string Source);

/// <summary>
/// Where the camera device stood relative to the tool centre when a point was taught:
/// camera pose − tool location (mm, camera device frame). Z is null when the tool's
/// registration has no focus Z (the camera then keeps its current Z). Lets a later run
/// re-find the point from wherever the tool is: camera → tool, then this offset.
/// </summary>
public sealed record CameraOffset(double X, double Y, double? Z);

/// <summary>
/// A <b>gripped object</b> — something held by a registered tool (the FAU in the hex-right
/// gripper), taught by two reference points seen through the gantry camera. Per-product
/// (<c>{product}\gripped_objects.json</c>): the FAU dimensions belong to the product, the
/// gripper mount does not.
///
/// <para>Geometry: the two points were marked while the tool stood at <see cref="ToolPose"/>;
/// their average is the object <see cref="CentreX"/>/<see cref="CentreY"/> in the camera
/// device's frame. The object rides with the tool, so its live location is that centre shifted
/// by the tool's move since <see cref="ToolPose"/>, projected through the tool's
/// <see cref="ToolRegistration"/> axis maps — same model as the tool anchor itself.</para>
/// </summary>
public sealed record GrippedObject
{
    /// <summary>Object name (also the key) — "FAU" by default.</summary>
    public string Name { get; init; } = "FAU";

    /// <summary>The registered tool device holding the object.</summary>
    public string ToolDevice { get; init; } = "hex-right";

    /// <summary>The camera device (from the tool registration at teach time).</summary>
    public string CameraDevice { get; init; } = "gantry-main";

    /// <summary>The camera the object was taught against (the overlay draws only on this feed).</summary>
    public string CameraId { get; init; } = "main_camera";

    /// <summary>The tool device's pose when the points were taught (the "pose location").</summary>
    public MotionPosition? ToolPose { get; init; }

    // --- camera condition the points were taught under ---

    public bool AutoExposure { get; init; } = true;
    public double ExposureUs { get; init; }
    public double Gain { get; init; }

    /// <summary>Lighting placeholder — free text until the lighting facet is wired in here.</summary>
    public string LightingNote { get; init; } = string.Empty;

    // --- the two reference points ---

    /// <summary>Vision profile that auto-marks point 1 / 2 (empty = manual click).</summary>
    public string Point1VisionProfile { get; init; } = string.Empty;
    public string Point2VisionProfile { get; init; } = string.Empty;

    public GrippedPoint? Point1 { get; init; }
    public GrippedPoint? Point2 { get; init; }

    /// <summary>Camera position for point 1 / 2 relative to the tool centre (see <see cref="CameraOffset"/>).</summary>
    public CameraOffset? Point1CameraOffset { get; init; }
    public CameraOffset? Point2CameraOffset { get; init; }

    /// <summary>Object centre = mean of the two points, camera device frame at <see cref="ToolPose"/> (mm).</summary>
    public double CentreX { get; init; }
    public double CentreY { get; init; }

    /// <summary>Camera-device Z that had the object in focus (mean of the point poses' Z), or null.</summary>
    public double? FocusZ { get; init; }

    // --- outline (drawn green; the marker is purple) ---

    public double OutlineWidthMm { get; init; } = 2.0;
    public double OutlineLengthMm { get; init; } = 6.0;

    /// <summary>Outline box centre − object centre, camera device frame (mm).</summary>
    public double OutlineOffsetXmm { get; init; }
    public double OutlineOffsetYmm { get; init; }

    /// <summary>Settle wait after each move in the detect procedure (ms).</summary>
    public int SettleMs { get; init; } = 100;

    /// <summary>
    /// Default Z offset (mm, camera-device frame) for object → camera when Z is included: the
    /// object's focus plane is parked at the camera's Z + this. Negative = higher (gantry Z− is
    /// up; for hex-right that is tool X+), positive = lower (tool X−).
    /// </summary>
    public double ToCameraZOffsetMm { get; init; }

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }

    /// <summary>True once the pose and both points exist.</summary>
    public bool IsComplete => ToolPose != null && Point1 != null && Point2 != null;
}
