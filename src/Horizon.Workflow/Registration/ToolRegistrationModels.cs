using Horizon.Engine;

namespace Horizon.Workflow.Registration;

/// <summary>
/// How one camera-frame axis follows the tool device: <c>cameraAxisDelta = Sign × toolAxisDelta</c>.
/// E.g. for the hex-right gripper seen by the gantry camera, camera X ← tool Z × −1
/// and camera Y ← tool Y × −1 (the hexapod is mounted rotated relative to the gantry).
/// </summary>
public sealed record ToolAxisMap(string ToolAxis, double Sign)
{
    /// <summary>True when the map names one of X/Y/Z/U/V/W with a ±1 sign.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ToolAxis)
        && "XYZUVW".Contains(ToolAxis.Trim().ToUpperInvariant(), StringComparison.Ordinal)
        && ToolAxis.Trim().Length == 1
        && (Math.Abs(Sign - 1.0) < 1e-9 || Math.Abs(Sign + 1.0) < 1e-9);
}

/// <summary>One operator-clicked sample of the tool centre, kept for the record.</summary>
public sealed record RegisteredPoint(
    double PixelX, double PixelY,          // where it was clicked in the camera image (source px)
    double CameraFrameX, double CameraFrameY);   // the same point in the camera device's frame (mm)

/// <summary>
/// A persisted <b>device registration</b>: where a tool carried by one motion device
/// (the hex-right gripper) sits in the frame of the motion device that carries the camera
/// (the gantry), so either can be sent to the other. Machine-global — it describes the
/// physical mount and survives until the hardware is changed; re-register then.
///
/// <para>Geometry: at registration the tool device stood at <see cref="ToolPose"/> and the
/// tool centre was measured at (<see cref="ToolX"/>, <see cref="ToolY"/>) in the camera
/// device's coordinate frame (camera-pose XY + the image offset converted through
/// mm/pixel). Any later tool-device move is projected into that frame through
/// <see cref="MapX"/> / <see cref="MapY"/>, so the live tool location is
/// <c>ToolX + MapX.Sign × Δtool[MapX.ToolAxis]</c> (same for Y). Tool rotations (U/V/W)
/// are ignored — the registration is a pure XY translation model.</para>
/// </summary>
public sealed record ToolRegistration
{
    /// <summary>The motion device carrying the tool (the gripper).</summary>
    public string ToolDevice { get; init; } = "hex-right";

    /// <summary>Operator-facing tool name, drawn on the camera overlay.</summary>
    public string Label { get; init; } = "Gripper";

    /// <summary>The motion device carrying the camera.</summary>
    public string CameraDevice { get; init; } = "gantry-main";

    /// <summary>The camera the tool was registered against (overlay only draws on this feed).</summary>
    public string CameraId { get; init; } = "main_camera";

    /// <summary>The tool device's full pose when the registration was taken (the "pose location").</summary>
    public MotionPosition? ToolPose { get; init; }

    /// <summary>The camera device's pose when the tool centre was clicked (for the record).</summary>
    public MotionPosition? CameraPose { get; init; }

    /// <summary>Tool centre in the camera device's frame at <see cref="ToolPose"/> (mm).</summary>
    public double ToolX { get; init; }
    public double ToolY { get; init; }

    /// <summary>
    /// Camera-device Z at which the camera was focused on the tool while it stood at
    /// <see cref="ToolPose"/> (the "focus Z", mm). Null until captured — then the moves can
    /// include Z: camera → tool brings the camera to this height (adjusted for any tool-device
    /// move along <see cref="MapZ"/>), tool → camera brings the tool to the camera's current Z.
    /// </summary>
    public double? CameraZ { get; init; }

    /// <summary>The 2 or 4 operator-clicked samples averaged into <see cref="ToolX"/>/<see cref="ToolY"/>.</summary>
    public RegisteredPoint[] Points { get; init; } = Array.Empty<RegisteredPoint>();

    /// <summary>mm/pixel used to convert the clicks (for the record).</summary>
    public double MmPerPixel { get; init; }

    /// <summary>Tool outline rectangle drawn on the camera: width along image X (mm).</summary>
    public double OutlineWidthMm { get; init; } = 2.0;

    /// <summary>Tool outline rectangle drawn on the camera: length along image Y (mm).</summary>
    public double OutlineLengthMm { get; init; } = 6.0;

    /// <summary>
    /// Where the outline box sits relative to the tool anchor: box centre − tool centre, in the
    /// camera device's frame (mm). Zero = the anchor is the box centre; the anchor may be
    /// anywhere inside (or outside) the box.
    /// </summary>
    public double OutlineOffsetXmm { get; init; }
    public double OutlineOffsetYmm { get; init; }

    /// <summary>Camera-frame X follows this tool axis (hex-right default: tool Z × −1).</summary>
    public ToolAxisMap MapX { get; init; } = new("Z", -1);

    /// <summary>Camera-frame Y follows this tool axis (hex-right default: tool Y × −1).</summary>
    public ToolAxisMap MapY { get; init; } = new("Y", -1);

    /// <summary>Camera-frame Z follows this tool axis (hex-right default: tool X × −1). Only used once <see cref="CameraZ"/> is captured.</summary>
    public ToolAxisMap MapZ { get; init; } = new("X", -1);

    /// <summary>True once the focus Z has been captured and its axis map is usable.</summary>
    public bool HasZ => CameraZ != null && MapZ.IsValid;

    /// <summary>
    /// Default Z offset (mm, camera-device frame) applied by tool → camera when Z is included:
    /// the tool is parked at the camera's focus plane + this. Negative = above focus on the
    /// gantry's Z convention (gantry Z− is up), e.g. −1 keeps the gripper 1 mm high.
    /// Camera → tool ignores it and always goes to the taught focus height.
    /// </summary>
    public double ToolToCameraZOffsetMm { get; init; }

    /// <summary>Hard bound (mm) on a single camera→tool or tool→camera move; longer moves are refused.</summary>
    public double MaxMoveMm { get; init; } = 10.0;

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }

    /// <summary>True once a pose and a measured centre exist and the axis maps are usable.</summary>
    public bool IsComplete =>
        ToolPose != null && MapX.IsValid && MapY.IsValid
        && !string.Equals(MapX.ToolAxis, MapY.ToolAxis, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where a registered tool is right now, in its camera device's frame — computed from
/// the engine's cached motion states (non-blocking, any thread).
/// </summary>
/// <param name="ToolX">Tool centre in the camera device's frame (mm).</param>
/// <param name="CameraX">Camera device pose (mm) — the point the camera is centred on.</param>
/// <param name="DxMm">Tool − camera: the relative camera-device move that centres the camera on the tool.</param>
/// <param name="ToolZ">Camera-device Z that focuses on the tool right now, or null if no focus Z was captured.</param>
/// <param name="DzMm">ToolZ − CameraZ (0 when <paramref name="ToolZ"/> is null).</param>
public sealed record ToolLocation(
    string ToolDevice,
    double ToolX, double ToolY,
    double CameraX, double CameraY,
    double DxMm, double DyMm,
    double? ToolZ = null, double CameraZ = 0, double DzMm = 0)
{
    /// <summary>XY distance between tool and camera (mm).</summary>
    public double DistanceMm => Math.Sqrt(DxMm * DxMm + DyMm * DyMm);

    /// <summary>XYZ distance (mm) — equals <see cref="DistanceMm"/> when no focus Z is known.</summary>
    public double Distance3Mm => Math.Sqrt(DxMm * DxMm + DyMm * DyMm + DzMm * DzMm);

    public bool HasZ => ToolZ != null;
}
