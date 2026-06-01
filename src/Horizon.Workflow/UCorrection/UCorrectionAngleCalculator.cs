using Horizon.Engine;

namespace Horizon.Workflow.UCorrection;

/// <summary>
/// Pure geometry for the FAU↔PIC "U correction": the signed angle between the
/// PIC line (P1→P2) and the FAU line (F1→F2), in the gantry XY plane. The hexapod
/// U axis is rotated by this angle to make the two parallel.
///
/// Ported verbatim from <c>acs_solution_saa</c>'s <c>UCorrectionAngleCalculator</c>
/// — coordinate-only, no hardware, no UI, fully unit-testable headless.
/// </summary>
public static class UCorrectionAngleCalculator
{
    /// <summary>
    /// Returns (angle of F-line) − (angle of P-line) in degrees, wrapped to
    /// [-180, 180]. Only the X/Y components of each point are used. A positive
    /// result means the FAU line is rotated counter-clockwise relative to the PIC
    /// line (before the controller-convention <c>Sign</c> is applied).
    /// </summary>
    public static double ComputeAngleDegrees(
        MotionPosition p1, MotionPosition p2,
        MotionPosition f1, MotionPosition f2)
    {
        double angleP = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        double angleF = Math.Atan2(f2.Y - f1.Y, f2.X - f1.X);
        double diff = (angleF - angleP) * 180.0 / Math.PI;

        while (diff > 180.0) diff -= 360.0;
        while (diff < -180.0) diff += 360.0;
        return diff;
    }
}
