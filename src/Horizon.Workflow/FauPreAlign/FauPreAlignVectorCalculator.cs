using Horizon.Engine;

namespace Horizon.Workflow.FauPreAlign;

/// <summary>
/// Pure geometry for FAU pre-align. The output position <c>Xo</c> is the midpoint
/// of the two output/PIC points (Out1, Out2); the fiber position <c>Xf</c> is the
/// midpoint of the two FAU fiber points (Fiber1, Fiber2). The correction is the
/// gantry-XY vector that brings the current fiber→output offset (<c>Xf − Xo</c>)
/// onto the desired "master" offset.
///
/// Ported from <c>acs_solution_saa</c>'s <c>FauPreAlignVectorCalculator</c> —
/// coordinate-only, no hardware, no UI.
/// </summary>
public static class FauPreAlignVectorCalculator
{
    /// <summary>Midpoint of two points' X/Y.</summary>
    public static (double X, double Y) Midpoint(MotionPosition a, MotionPosition b)
        => ((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

    /// <summary>
    /// The master (target) offset in mm, from a target distance (µm) and angle (deg):
    /// <c>xMm = µm·0.001</c>, <c>yMm = −xMm·tan(angle)</c>. Matches the legacy
    /// <c>GetMasterVectorMm</c>.
    /// </summary>
    public static (double X, double Y) MasterVectorMm(double targetUm, double angleDeg)
    {
        double xMm = targetUm * 0.001;
        double yMm = -xMm * Math.Tan(angleDeg * Math.PI / 180.0);
        return (xMm, yMm);
    }

    /// <summary>
    /// <c>correction = master − (Xf − Xo)</c> in gantry XY — how far the FAU must move
    /// so the fiber→output offset equals the master vector.
    /// </summary>
    public static (double X, double Y) ComputeCorrectionXy(
        MotionPosition out1, MotionPosition out2,
        MotionPosition fiber1, MotionPosition fiber2,
        double masterXmm, double masterYmm)
    {
        var xo = Midpoint(out1, out2);
        var xf = Midpoint(fiber1, fiber2);
        double currentX = xf.X - xo.X;
        double currentY = xf.Y - xo.Y;
        return (masterXmm - currentX, masterYmm - currentY);
    }
}
