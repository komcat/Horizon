using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// The geometry behind <c>vision.detectObject</c>: image pixels → camera-device (gantry) mm, and
/// each detector's angle → the block's <b>P</b> convention (0° = straight up on the image,
/// clockwise positive, −180…+180).
/// </summary>
public static class DetectObjectMath
{
    /// <summary>
    /// Image point (source px) at a camera pose → the camera device's XY, MoveClick semantics:
    /// offset from the image centre × mm/pixel, with the MoveClick axis signs. No rotation term
    /// between camera and gantry.
    /// </summary>
    public static (double X, double Y) ToGantry(
        AppSettings s, MotionPosition camPose, double px, double py, int srcW, int srcH)
    {
        double sx = s.MoveClickXSign < 0 ? -1 : 1, sy = s.MoveClickYSign < 0 ? -1 : 1;
        return (camPose.X + (px - srcW / 2.0) * s.MmPerPixel * sx,
                camPose.Y + (py - srcH / 2.0) * s.MmPerPixel * sy);
    }

    /// <summary>
    /// Turns a detector result into the block's X / Y / P at the pose the frame was taken from.
    /// Returns <see cref="DetectedObject.NotFound"/> when nothing was found or the result has no point.
    /// </summary>
    public static DetectedObject FromResult(
        VisionProfile profile, VisionDetectResult result, MotionPosition camPose, int srcW, int srcH, AppSettings s)
    {
        if (!result.Found) return DetectedObject.NotFound;
        if (Value(result, "x") is not { } cx || Value(result, "y") is not { } cy) return DetectedObject.NotFound;

        double px = cx, py = cy, p = 0;
        double angle = Value(result, "angle") ?? 0;
        switch ((profile.DetectionMethod ?? "ncc").ToLowerInvariant())
        {
            case "circle":
                break;   // a circle has no direction

            case "line":
            case "edgereg":
                // Reported counter-clockwise from +X, modulo 180° → fold to −90…+90.
                p = WrapHalfTurn(90.0 - angle);
                break;

            case "corner":
                p = Wrap(90.0 - angle);   // bisector, counter-clockwise from +X over the full circle
                break;

            default: // ncc
            {
                var t = profile.Template;
                double rotation = t?.Rotation ?? 0;
                // The reported angle is match + anchor rotation; HALCON's match angle is
                // counter-clockwise as seen on the image.
                double match = angle - rotation;
                p = Wrap(rotation - match);
                if (t != null)
                {
                    // Anchor offset from the template centre, turned with the matched instance
                    // (counter-clockwise on a y-down image).
                    double ox = t.AnchorX - t.Width / 2.0, oy = t.AnchorY - t.Height / 2.0;
                    double rad = match * Math.PI / 180.0, c = Math.Cos(rad), sn = Math.Sin(rad);
                    px = cx + ox * c + oy * sn;
                    py = cy - ox * sn + oy * c;
                }
                break;
            }
        }

        if (!double.IsFinite(px) || !double.IsFinite(py) || !double.IsFinite(p)) return DetectedObject.NotFound;
        var (gx, gy) = ToGantry(s, camPose, px, py, srcW, srcH);
        return new DetectedObject(true, gx, gy, p, Value(result, "score") ?? 0, px, py, Manual: false);
    }

    /// <summary>An operator mark → the block's result (P = 0, flagged manual).</summary>
    public static DetectedObject FromMark(MarkedLocation mark, MotionPosition camPose, AppSettings s)
    {
        var (gx, gy) = ToGantry(s, camPose, mark.Px, mark.Py, mark.SrcW, mark.SrcH);
        return new DetectedObject(true, gx, gy, 0, 0, mark.Px, mark.Py, Manual: true);
    }

    /// <summary>
    /// Applies the block's X / Y / P selection: an unselected X or Y becomes the camera's own
    /// axis value at <paramref name="camPose"/> (no correction on that axis), an unselected P becomes 0.
    /// </summary>
    public static DetectedObject Select(DetectedObject obj, MotionPosition camPose, bool useX, bool useY, bool useP)
        => !obj.Found ? obj : obj with
        {
            X = useX ? obj.X : camPose.X,
            Y = useY ? obj.Y : camPose.Y,
            P = useP ? obj.P : 0,
        };

    /// <summary>Wraps degrees into (−180, +180].</summary>
    public static double Wrap(double deg)
    {
        double w = deg % 360.0;
        if (w > 180.0) w -= 360.0;
        else if (w <= -180.0) w += 360.0;
        return w;
    }

    /// <summary>Wraps degrees into (−90, +90] — for undirected lines.</summary>
    public static double WrapHalfTurn(double deg)
    {
        double w = deg % 180.0;
        if (w > 90.0) w -= 180.0;
        else if (w <= -90.0) w += 180.0;
        return w;
    }

    private static double? Value(VisionDetectResult r, string key)
    {
        foreach (var v in r.Values)
            if (string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)) return v.Value;
        return null;
    }
}
