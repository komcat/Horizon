using Horizon.Engine;

namespace Horizon.Workflow.Registration;

/// <summary>A measured point on the part: where the camera stood, where it was marked in the image, and the result in the camera device's (gantry) frame (mm).</summary>
public sealed record PartPoint(
    MotionPosition CameraPose,
    double PixelX, double PixelY,
    double X, double Y,
    string Source);   // "manual" | "vision:<profile>"

/// <summary>
/// The part's local reference frame: origin at fiducial E1, +X along E1 → E2, +Y = +X rotated
/// 90° counter-clockwise in the gantry XY plane (right-handed), units mm (no scaling).
/// </summary>
public sealed record PartFrame(double OriginX, double OriginY, double AngleRad)
{
    public static PartFrame From(double e1X, double e1Y, double e2X, double e2Y)
        => new(e1X, e1Y, Math.Atan2(e2Y - e1Y, e2X - e1X));

    public double AngleDeg => AngleRad * 180.0 / Math.PI;

    /// <summary>Local → gantry frame.</summary>
    public (double X, double Y) ToGlobal(double lx, double ly)
    {
        double c = Math.Cos(AngleRad), s = Math.Sin(AngleRad);
        return (OriginX + lx * c - ly * s, OriginY + lx * s + ly * c);
    }

    /// <summary>Gantry → local frame.</summary>
    public (double X, double Y) ToLocal(double gx, double gy)
    {
        double c = Math.Cos(AngleRad), s = Math.Sin(AngleRad);
        double dx = gx - OriginX, dy = gy - OriginY;
        return (dx * c + dy * s, -dx * s + dy * c);
    }

    /// <summary>Rotates a local-frame vector (no translation) into the gantry frame.</summary>
    public (double X, double Y) VectorToGlobal(double lx, double ly)
    {
        double c = Math.Cos(AngleRad), s = Math.Sin(AngleRad);
        return (lx * c - ly * s, lx * s + ly * c);
    }
}

/// <summary>A fiducial (E1 / E2): how it is found (profile or manual) and its taught measurement.</summary>
public sealed record PartFiducial
{
    /// <summary>Vision profile that auto-marks it; empty = manual click.</summary>
    public string VisionProfile { get; init; } = string.Empty;

    /// <summary>Camera device pose the fiducial is seen from (taught, absolute).</summary>
    public MotionPosition? CameraPose { get; init; }

    /// <summary>The taught measurement (gantry frame), or null until marked.</summary>
    public PartPoint? Point { get; init; }
}

/// <summary>
/// One taught point of a local object, stored in the part's local frame so it follows the
/// part wherever the fixture puts it: the point itself and the camera position it is seen
/// from (both local XY, camera Z absolute).
/// </summary>
public sealed record PartObjectPoint(
    double LocalX, double LocalY,
    double CamLocalX, double CamLocalY, double CamZ,
    string VisionProfile,
    string Source,
    double PixelX, double PixelY);

/// <summary>A local object on the part: a point (one taught point) or a line (two), plus a manual XY offset in the local frame.</summary>
public sealed record PartObject
{
    public string Name { get; init; } = "Object";

    /// <summary><c>point</c> or <c>line</c>.</summary>
    public string Kind { get; init; } = "point";

    public PartObjectPoint[] Points { get; init; } = Array.Empty<PartObjectPoint>();

    /// <summary>Manual offset from the object's reference point (a point itself; a line's midpoint), local frame (mm).</summary>
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }

    public bool IsLine => string.Equals(Kind, "line", StringComparison.OrdinalIgnoreCase);
    public int RequiredPoints => IsLine ? 2 : 1;
    public bool IsComplete => Points.Length >= RequiredPoints;
}

/// <summary>An object as re-measured by a run: its points in the gantry frame, or the taught ones through the new frame when detection failed.</summary>
public sealed record MeasuredPartObject(string Name, PartPoint[] Points, bool Measured);

/// <summary>The outcome of one alignment run — the re-found fiducials, the frame they define, and every object.</summary>
public sealed record PartMeasurement(
    PartPoint E1, PartPoint E2, PartFrame Frame,
    MeasuredPartObject[] Objects,
    string At);

/// <summary>
/// A <b>part alignment</b> profile (per product): two fiducials that fix the part's local frame on
/// the working fixture, and the local objects (points / lines) taught in that frame. A run
/// re-finds E1 / E2, rebuilds the frame and re-measures the objects; the camera can then be sent
/// to a fiducial or to an object (+ its offset) wherever the part was loaded.
/// </summary>
public sealed record PartAlignment
{
    public string Name { get; init; } = "Part";
    public string CameraDevice { get; init; } = "gantry-main";
    public string CameraId { get; init; } = "main_camera";

    public bool AutoExposure { get; init; } = true;
    public double ExposureUs { get; init; }
    public double Gain { get; init; }

    /// <summary>Settle wait after each move in the run (ms).</summary>
    public int SettleMs { get; init; } = 100;

    public PartFiducial E1 { get; init; } = new();
    public PartFiducial E2 { get; init; } = new();

    public PartObject[] Objects { get; init; } = Array.Empty<PartObject>();

    /// <summary>The last run's result, or null if the part has only been taught.</summary>
    public PartMeasurement? Measured { get; init; }

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }

    /// <summary>The frame from the taught fiducials, or null until both are marked.</summary>
    public PartFrame? TaughtFrame
        => E1.Point != null && E2.Point != null ? PartFrame.From(E1.Point.X, E1.Point.Y, E2.Point.X, E2.Point.Y) : null;

    /// <summary>The frame to use now: the last run's, else the taught one.</summary>
    public PartFrame? EffectiveFrame => Measured?.Frame ?? TaughtFrame;
}

/// <summary>
/// What the part looks like right now in the gantry frame, for drawing and for the camera moves:
/// the effective fiducials and objects (measured when a run has happened, taught otherwise) and,
/// per object, the offset target the camera move goes to.
/// </summary>
public sealed record PartSnapshot(
    string Name,
    (double X, double Y)? E1,
    (double X, double Y)? E2,
    PartFrame? Frame,
    IReadOnlyList<PartSnapshotObject> Objects,
    bool FromMeasurement);

public sealed record PartSnapshotObject(
    string Name, bool IsLine, bool Measured,
    (double X, double Y)[] Points,
    (double X, double Y) Reference,
    (double X, double Y) Target);
