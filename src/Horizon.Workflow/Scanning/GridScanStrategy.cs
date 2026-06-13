using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Scanning;

/// <summary>
/// Raster (XY-grid) scan: sweeps a fixed lattice of points about the start
/// position and records the signal at each node, leaving the global peak for the
/// orchestrator to return to. Unlike <see cref="CoarseToFineStrategy"/> it does
/// no hill-climbing — it visits <em>every</em> node, so it maps the whole field
/// and cannot be fooled by a local maximum, at the cost of more moves.
///
/// <para>The grid is driven entirely by the existing <see cref="ScanParameters"/>,
/// so it plugs into <see cref="HexapodScanEngine"/>/<see cref="ScanContext"/>
/// unchanged:</para>
/// <list type="bullet">
///   <item><see cref="ScanParameters.AxesToScan"/>[0], [1] — the two grid axes
///         (e.g. <c>X</c>, <c>Y</c>). Only the first two are used.</item>
///   <item><see cref="ScanParameters.StepSizes"/>[0] — the spacing between
///         adjacent grid nodes (mm). This is the "step size between each grid
///         point". Any further entries are ignored (a grid is single-pitch).</item>
///   <item><see cref="ScanParameters.MaxTotalDistanceMm"/> — the half-extent per
///         axis: the grid spans the start ± this on each axis.</item>
///   <item><see cref="ScanParameters.MotionSettleMs"/>,
///         <see cref="ScanParameters.AveragingCount"/> — settle + averaging at
///         each node, as elsewhere.</item>
/// </list>
///
/// <para>The sweep is serpentine (boustrophedon): each row reverses direction so
/// consecutive nodes are one step apart, minimising travel. Nodes are addressed
/// in <em>absolute</em> coordinates relative to the captured start, so there is
/// no cumulative relative-move drift across the field.</para>
/// </summary>
public sealed class GridScanStrategy : IScanStrategy
{
    public string Name => "GridScan";

    /// <summary>
    /// Hard cap on the node count — a bounded-loop guard (Power-of-10: no
    /// runaway sweep). A 2 mm field at 5 µm pitch is ~801×801; the cap forces an
    /// explicit coarser step or a tighter extent before that point.
    /// </summary>
    private const int MaxGridNodes = 4096;

    public async Task<Result> ExecuteAsync(ScanContext context, CancellationToken ct)
    {
        var p = context.Parameters;

        if (p.AxesToScan.Count < 2)
            return Result.Fail(ErrorCode.InvalidParameter,
                "Grid scan needs two axes — set AxesToScan[0] and AxesToScan[1] (e.g. X, Y).");
        if (p.StepSizes.Count == 0)
            return Result.Fail(ErrorCode.InvalidParameter,
                "Grid scan needs a step size — set StepSizes[0].");

        string axisX = p.AxesToScan[0];
        string axisY = p.AxesToScan[1];
        double step = p.StepSizes[0];
        double half = p.MaxTotalDistanceMm;

        // Nodes each side of centre, and the (2n+1) count per axis.
        int n = (int)Math.Floor(half / step);
        int perAxis = (2 * n) + 1;
        long totalNodes = (long)perAxis * perAxis;
        if (totalNodes > MaxGridNodes)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"Grid {perAxis}×{perAxis} = {totalNodes} nodes exceeds the {MaxGridNodes}-node cap — " +
                "increase StepSizes[0] or reduce MaxTotalDistanceMm.");

        // Capture the centre once; every node is an absolute target derived from it.
        var centre = await context.GetCurrentPositionAsync(ct).ConfigureAwait(false);
        double centreX = GetAxis(centre, axisX);
        double centreY = GetAxis(centre, axisY);

        context.CurrentStepSize = step;
        context.CurrentAxis = $"{axisX}{axisY}";
        context.Log(LogLevel.Info,
            $"Grid scan {perAxis}×{perAxis} on {axisX}/{axisY} — {step * 1000:0.###} µm pitch, " +
            $"±{half:F4} mm about {PosText(centre)} ({totalNodes} nodes).");

        int visited = 0;

        // Serpentine raster: rows along axisY, columns along axisX, flipping the
        // column direction each row so the next node is always one step away.
        for (int iy = -n; iy <= n; iy++)
        {
            ct.ThrowIfCancellationRequested();

            bool forward = (iy + n) % 2 == 0;
            for (int col = 0; col < perAxis; col++)
            {
                ct.ThrowIfCancellationRequested();

                int ix = forward ? (-n + col) : (n - col);
                var target = SetAxis(SetAxis(centre, axisX, centreX + (ix * step)),
                                     axisY, centreY + (iy * step));

                if (!await context.MoveToPositionAsync(target, ct).ConfigureAwait(false))
                {
                    // A plain motion failure skips the node (matches the legacy
                    // per-axis "continue" behaviour); a cancel surfaces below.
                    ct.ThrowIfCancellationRequested();
                    context.Log(LogLevel.Warning, $"Grid move to node ({ix},{iy}) failed — skipping.");
                    continue;
                }

                await context.SettleAsync(ct).ConfigureAwait(false);

                var position = await context.GetCurrentPositionAsync(ct).ConfigureAwait(false);
                double value = await context.GetAveragedMeasurementAsync(p.AveragingCount, ct).ConfigureAwait(false);
                bool isPeak = context.UpdateGlobalPeak(value, position, $"grid node ({ix},{iy})");

                context.RecordDataPoint(new ScanDataPoint
                {
                    Value = value,
                    Position = position,
                    Timestamp = DateTime.Now,
                    Axis = $"{axisX}{axisY}",
                    StepSize = step,
                    Direction = forward ? +1 : -1,
                    IsPeak = isPeak,
                });

                visited++;
                double fraction = totalNodes > 0 ? (double)visited / totalNodes : 0;
                context.ReportPhase(fraction,
                    $"Grid {visited}/{totalNodes} ({ix},{iy}): {value:G4} {context.Unit}{(isPeak ? " (new max)" : "")}");
            }
        }

        // One summary row for the whole raster (parallels CoarseToFine's per-axis rows).
        context.AddAxisScan(new AxisScanInfo
        {
            Axis = $"{axisX}{axisY} grid",
            StepSize = step,
            FinalValue = context.GlobalPeak.Value,
            FinalPosition = context.GlobalPeak.Position,
            TotalMeasurements = visited,
        });

        context.Log(LogLevel.Info,
            $"Grid scan done — {visited} node(s) measured, peak {context.GlobalPeak.Value:G6} {context.Unit}.");

        // The orchestrator computes the σ/SNR verdict and returns to GlobalPeak.
        return Result.Ok();
    }

    /// <summary>Reads one named component (X..W) from a position.</summary>
    private static double GetAxis(MotionPosition p, string axis) => axis.ToUpperInvariant() switch
    {
        "X" => p.X,
        "Y" => p.Y,
        "Z" => p.Z,
        "U" => p.U,
        "V" => p.V,
        "W" => p.W,
        _ => throw new ArgumentException($"Unknown grid axis '{axis}'.", nameof(axis)),
    };

    /// <summary>Returns a copy of <paramref name="p"/> with one named component set.</summary>
    private static MotionPosition SetAxis(MotionPosition p, string axis, double value) => axis.ToUpperInvariant() switch
    {
        "X" => p with { X = value },
        "Y" => p with { Y = value },
        "Z" => p with { Z = value },
        "U" => p with { U = value },
        "V" => p with { V = value },
        "W" => p with { W = value },
        _ => throw new ArgumentException($"Unknown grid axis '{axis}'.", nameof(axis)),
    };

    private static string PosText(MotionPosition p)
        => $"X{p.X:F4} Y{p.Y:F4} Z{p.Z:F4}";
}
