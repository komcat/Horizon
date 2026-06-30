using System;
using System.Collections.Generic;

namespace Horizon.Workflow.Dispensing;

/// <summary>The geometry family of a dispense pattern. New shapes (circle, arc, custom
/// path) are added here + a matching case in <see cref="DispensePattern.Expand"/>.</summary>
public enum DispensePatternKind
{
    /// <summary>A single dot at the anchor.</summary>
    Dot,
    /// <summary>A rectangular array of dots (Columns × Rows at the given pitch).</summary>
    Grid,
    /// <summary>A row of dots along a line (PointCount dots spanning LengthMm).</summary>
    Line,
}

/// <summary>A relative XY offset (mm) of one dispense point from the pattern anchor.</summary>
public readonly record struct DispenseOffset(double Dx, double Dy);

/// <summary>
/// The geometry of a dispense pattern, independent of where it is placed or how each
/// dot is shot. <see cref="Expand"/> turns the parameters into the ordered list of
/// per-dot XY offsets (centred on the anchor, rotated by <see cref="AngleDeg"/>), so the
/// runner is pattern-agnostic — a Dot, a Grid or a Line all execute the same way.
/// </summary>
public sealed record DispensePattern
{
    public DispensePatternKind Kind { get; init; } = DispensePatternKind.Dot;

    // --- Grid ---
    /// <summary>Grid columns (X). Clamped to ≥ 1.</summary>
    public int Columns { get; init; } = 2;
    /// <summary>Grid rows (Y). Clamped to ≥ 1.</summary>
    public int Rows { get; init; } = 2;
    /// <summary>Column spacing (mm).</summary>
    public double PitchXmm { get; init; } = 1.0;
    /// <summary>Row spacing (mm).</summary>
    public double PitchYmm { get; init; } = 1.0;

    // --- Line ---
    /// <summary>Number of dots along the line. Clamped to ≥ 1.</summary>
    public int PointCount { get; init; } = 3;
    /// <summary>Total line length (mm), dots spaced evenly end-to-end.</summary>
    public double LengthMm { get; init; } = 5.0;

    // --- shared ---
    /// <summary>Rotates the whole pattern about the anchor (degrees, CCW).</summary>
    public double AngleDeg { get; init; }

    /// <summary>
    /// Expands the pattern into the ordered list of dot offsets (mm) relative to the
    /// anchor, centred on it and rotated by <see cref="AngleDeg"/>. Always returns at
    /// least one offset (the anchor itself).
    /// </summary>
    public IReadOnlyList<DispenseOffset> Expand()
    {
        var raw = Kind switch
        {
            DispensePatternKind.Grid => Grid(),
            DispensePatternKind.Line => Line(),
            _ => new List<DispenseOffset> { new(0, 0) },
        };
        return Rotate(raw, AngleDeg);
    }

    private List<DispenseOffset> Grid()
    {
        int cols = Math.Max(1, Columns);
        int rows = Math.Max(1, Rows);
        var pts = new List<DispenseOffset>(cols * rows);
        double cx = (cols - 1) / 2.0;
        double cy = (rows - 1) / 2.0;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
                pts.Add(new DispenseOffset((i - cx) * PitchXmm, (j - cy) * PitchYmm));
        return pts;
    }

    private List<DispenseOffset> Line()
    {
        int n = Math.Max(1, PointCount);
        if (n == 1) return new List<DispenseOffset> { new(0, 0) };
        var pts = new List<DispenseOffset>(n);
        double step = LengthMm / (n - 1);
        double start = -LengthMm / 2.0;
        for (int k = 0; k < n; k++)
            pts.Add(new DispenseOffset(start + k * step, 0));
        return pts;
    }

    private static IReadOnlyList<DispenseOffset> Rotate(List<DispenseOffset> pts, double angleDeg)
    {
        if (Math.Abs(angleDeg) < 1e-9) return pts;
        double rad = angleDeg * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            pts[i] = new DispenseOffset(p.Dx * ca - p.Dy * sa, p.Dx * sa + p.Dy * ca);
        }
        return pts;
    }
}

/// <summary>
/// How each dot is dispensed — the shot timing + motion, plus which output fires the
/// epoxy. Per-routine so variations can differ (a fine dot vs a heavy line). Seeded from
/// the machine camera→needle calibration defaults when a routine is created.
/// </summary>
public sealed record DispenseShot
{
    /// <summary>Epoxy window: shot output held ON (ms).</summary>
    public int DwellMs { get; init; } = 500;
    /// <summary>Settle delay on both shot edges (ms).</summary>
    public int OutputDebounceMs { get; init; } = 300;
    /// <summary>Slow lift-off distance after a dot (mm), away from the surface.</summary>
    public double LiftoffMm { get; init; } = 5.0;
    /// <summary>Slow-approach distance before touch (mm): fast to here, then crawl.</summary>
    public double ApproachMm { get; init; } = 10.0;
    /// <summary>Velocity for the slow approach / lift-off Z moves (mm/s).</summary>
    public double SlowZVelocity { get; init; } = 1.0;
    /// <summary>Touch-height tweak (mm): + presses deeper toward the surface, − higher.</summary>
    public double TouchZAdjustMm { get; init; }
    /// <summary>IO device carrying the shot output pin.</summary>
    public string ShotIoDevice { get; init; } = "IOBottom";
    /// <summary>Output pin pulsed to fire the epoxy shot.</summary>
    public string ShotPin { get; init; } = "Dispenser_Shot";
}

/// <summary>
/// A named, reusable dispensing routine — a pattern + per-dot shot settings, scoped to a
/// product. At run time the operator points the camera at the target; the runner uses the
/// machine camera→needle calibration to put the needle there and lays the pattern down.
/// </summary>
public sealed record DispenseRoutine
{
    /// <summary>Unique routine name within the product (also the file name).</summary>
    public string Name { get; init; } = "";

    /// <summary>Gantry carrying the camera + dispenser.</summary>
    public string GantryDevice { get; init; } = "gantry-main";

    /// <summary>Pneumatic slide that lowers/raises the dispenser head (sensor-confirmed).</summary>
    public string DispenserSlide { get; init; } = "Dispenser_Head";

    public DispensePattern Pattern { get; init; } = new();
    public DispenseShot Shot { get; init; } = new();

    /// <summary>When true, after the last dot the gantry returns so the camera frames the
    /// dispensed location (for visual inspection). Off by default.</summary>
    public bool ReturnToView { get; init; }

    /// <summary>Optional free-text note.</summary>
    public string? Notes { get; init; }

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }
}
