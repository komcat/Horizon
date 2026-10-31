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
/// How each dot is dispensed — FireDog's <c>dispense_run</c> parameters, one group per stage of
/// the needle-motion drawing. Per-routine, because how much glue goes where is a fact about the
/// product. The hardware (gantry, slide, valve) and the touch height come from the machine
/// camera→needle calibration, never from here, so nothing in a routine goes stale when the needle
/// is changed: re-calibrate and every routine still works.
/// </summary>
public sealed record DispenseShot
{
    // 1 — camera over the point, needle over it

    /// <summary>After arriving, the operator nudges X/Y onto this part's point; the dot goes where they
    /// leave it. This run only — nothing is written back. Z stays: the touch height is computed from it.</summary>
    public bool Adjust { get; init; } = true;

    /// <summary>XY traverse velocity (mm/s); 0 = the device's own speed.</summary>
    public double VTravel { get; init; }

    // 2–4 — down to the part

    /// <summary>Ask before extending the slide and before firing.</summary>
    public bool Confirm { get; init; } = true;

    /// <summary>Z down to the safety height (mm/s).</summary>
    public double VApproach { get; init; } = 20.0;

    /// <summary>Fast → slow handover, this far above the stop height (mm).</summary>
    public double SafetyMm { get; init; } = 1.0;

    /// <summary>Z from the safety height to the stop height (mm/s).</summary>
    public double VFinal { get; init; } = 5.0;

    /// <summary>Deliberate standoff ABOVE the touch height (mm) — the bead bridges it. A property of
    /// the glue and the dot size. 0 = stop at the touch.</summary>
    public double GapMm { get; init; }

    /// <summary>Corrects the CALIBRATED touch height (mm, + = closer to the part). Normally 0 with a
    /// verified calibration — not the same correction as <see cref="GapMm"/>.</summary>
    public double ZTrimMm { get; init; }

    // 5–7 — shot and up

    /// <summary>Settle at height before firing (ms).</summary>
    public int BeforeMs { get; init; }

    /// <summary>Valve open time = shot size (ms). No pressure control: the regulator sets the flow.</summary>
    public int ShotMs { get; init; } = 300;

    /// <summary>Dwell after firing — the string break (ms).</summary>
    public int AfterMs { get; init; }

    /// <summary>Z lift-off after the shot, before the slide comes up (mm). 0 = slide first.</summary>
    public double LiftMm { get; init; } = 1.5;

    /// <summary>Z lifting off the dot (mm/s).</summary>
    public double VLiftoff { get; init; } = 5.0;

    // 8 — back and inspect

    /// <summary>Bring the camera back over the pattern after the last dot.</summary>
    public bool VerifyBack { get; init; } = true;

    /// <summary>With <see cref="Confirm"/> off, the "check the dot" prompt continues by itself after
    /// this long (ms) and a Cancel inside it rejects the dot; 0 = no prompt.</summary>
    public int VerifyMs { get; init; } = 3000;
}

/// <summary>
/// A named, reusable dispensing routine — a pattern + per-dot motion/shot settings, scoped to a
/// product. At run time the camera is over the target (the program moved it there); the runner
/// uses the machine camera→needle calibration to put the needle there and lays the pattern down.
/// </summary>
public sealed record DispenseRoutine
{
    /// <summary>Unique routine name within the product (also the file name).</summary>
    public string Name { get; init; } = "";

    public DispensePattern Pattern { get; init; } = new();
    public DispenseShot Shot { get; init; } = new();

    /// <summary>Optional free-text note.</summary>
    public string? Notes { get; init; }

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }
}
