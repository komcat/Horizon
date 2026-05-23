using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Scanning;

/// <summary>
/// Multi-pass hill-climb with progressively finer step sizes. Faithful port of
/// the legacy <c>CoarseToFineStrategy</c>:
/// <list type="number">
///   <item>For each step size, scan every axis.</item>
///   <item>Per axis, hill-climb the +direction, return to start, hill-climb the
///         −direction, then settle on the better of the two.</item>
///   <item>Between axes, return to the global peak if it now beats the reading
///         here by more than <see cref="ScanParameters.ImprovementThreshold"/>.</item>
/// </list>
/// </summary>
public sealed class CoarseToFineStrategy : IScanStrategy
{
    public string Name => "CoarseToFine";

    public async Task<Result> ExecuteAsync(ScanContext context, CancellationToken ct)
    {
        var parameters = context.Parameters;

        for (int stepIndex = 0; stepIndex < parameters.StepSizes.Count; stepIndex++)
        {
            ct.ThrowIfCancellationRequested();

            double stepSize = parameters.StepSizes[stepIndex];
            context.CurrentStepSize = stepSize;

            await ScanAllAxesAtStepSizeAsync(context, stepSize, stepIndex, ct).ConfigureAwait(false);

            // Stop the whole scan if the first axis showed no signal above noise.
            if (context.NoSignalAbort)
                return Result.Ok(context.AbortReason);

            // Small delay between step sizes.
            await context.DelayAsync(100, ct).ConfigureAwait(false);
        }

        return Result.Ok();
    }

    private static async Task ScanAllAxesAtStepSizeAsync(
        ScanContext context, double stepSize, int stepIndex, CancellationToken ct)
    {
        var parameters = context.Parameters;

        for (int axisIndex = 0; axisIndex < parameters.AxesToScan.Count; axisIndex++)
        {
            ct.ThrowIfCancellationRequested();

            string axis = parameters.AxesToScan[axisIndex];
            context.CurrentAxis = axis;

            int currentStep = axisIndex + stepIndex * parameters.AxesToScan.Count;
            double totalSteps = parameters.AxesToScan.Count * parameters.StepSizes.Count;
            double fraction = totalSteps > 0 ? currentStep / totalSteps : 0;
            context.ReportPhase(fraction, $"Scanning {axis} axis with {stepSize * 1000:F3} µm steps");
            context.Log(LogLevel.Info,
                $"--- {axis} axis @ {stepSize * 1000:0.###} µm step ({fraction * 100:F0}%) ---");

            bool ok = await ScanSingleAxisAsync(context, axis, stepSize, ct).ConfigureAwait(false);
            if (!ok)
            {
                // A cancelled scan surfaces as OperationCanceledException; a plain
                // motion failure just skips to the next axis (legacy behaviour).
                ct.ThrowIfCancellationRequested();
                context.Log(LogLevel.Warning, $"Axis {axis} scan failed — continuing with next axis.");
                continue;
            }

            // No-signal early abort: after the first axis of the first pass, give
            // up if nothing has risen clear of the measured noise.
            if (stepIndex == 0 && axisIndex == 0 && context.DataPoints.Count >= 4)
            {
                double snr = context.PeakSnr();
                if (snr < parameters.MinPeakSigma)
                {
                    context.FlagNoSignalAbort(
                        $"no signal above noise after {axis} axis — peak SNR {SnrText(snr)} " +
                        $"< {parameters.MinPeakSigma:0.#} σ");
                    context.Log(LogLevel.Warning, context.AbortReason);
                    return;
                }
                context.Log(LogLevel.Info,
                    $"signal check after {axis} axis: SNR {SnrText(snr)} ≥ {parameters.MinPeakSigma:0.#} σ — continuing");
            }

            // Small delay between axes.
            await context.DelayAsync(100, ct).ConfigureAwait(false);

            // Return to the global peak if it is significantly better than here.
            var peak = context.GlobalPeak;
            if (peak.Valid)
            {
                double current = await context.GetMeasurementAsync(ct).ConfigureAwait(false);
                double relativeImprovement = current > 0 ? (peak.Value - current) / current : 0;
                if (relativeImprovement > parameters.ImprovementThreshold)
                {
                    if (await context.MoveToPositionAsync(peak.Position, ct).ConfigureAwait(false))
                        await context.SettleAsync(ct).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<bool> ScanSingleAxisAsync(
        ScanContext context, string axis, double stepSize, CancellationToken ct)
    {
        int measurementsBefore = context.DataPoints.Count;
        var startPosition = await context.GetCurrentPositionAsync(ct).ConfigureAwait(false);

        // Positive direction.
        var (positiveMax, positiveMaxPos) =
            await ScanDirectionAsync(context, axis, stepSize, +1, ct).ConfigureAwait(false);

        // Return to start before the negative scan.
        if (!await context.MoveToPositionAsync(startPosition, ct).ConfigureAwait(false))
            return false;
        await context.SettleAsync(ct).ConfigureAwait(false);

        // Negative direction.
        var (negativeMax, negativeMaxPos) =
            await ScanDirectionAsync(context, axis, stepSize, -1, ct).ConfigureAwait(false);

        // Settle on the better of the two directions.
        double bestValue = Math.Max(positiveMax, negativeMax);
        var bestPosition = positiveMax > negativeMax ? positiveMaxPos : negativeMaxPos;

        context.UpdateGlobalPeak(bestValue, bestPosition,
            $"{axis} axis scan with {stepSize * 1000:F3} µm steps");

        if (!await context.MoveToPositionAsync(bestPosition, ct).ConfigureAwait(false))
            return false;
        await context.SettleAsync(ct).ConfigureAwait(false);

        context.AddAxisScan(new AxisScanInfo
        {
            Axis = axis,
            StepSize = stepSize,
            FinalValue = bestValue,
            FinalPosition = bestPosition,
            TotalMeasurements = context.DataPoints.Count - measurementsBefore,
        });
        return true;
    }

    /// <summary>
    /// Hill-climbs one axis in one direction. Stops on consecutive non-improving
    /// steps, a significant drop, max travel, a motion error, or cancellation;
    /// then returns to the local maximum found.
    /// </summary>
    private static async Task<(double Value, MotionPosition Position)> ScanDirectionAsync(
        ScanContext context, string axis, double stepSize, int direction, CancellationToken ct)
    {
        var parameters = context.Parameters;
        string tag = $"{axis}{(direction > 0 ? "+" : "-")}";

        var currentPosition = await context.GetCurrentPositionAsync(ct).ConfigureAwait(false);
        double maxValue = await context.GetMeasurementAsync(ct).ConfigureAwait(false);
        var maxPosition = currentPosition;

        context.Log(LogLevel.Debug,
            $"{tag}: begin — start value {maxValue:G6} {context.Unit} at {PosText(currentPosition)}, " +
            $"feedback samples {context.FeedbackSampleCount}");

        double previousValue = maxValue;
        int consecutiveDecreases = 0;
        double totalDistance = 0;
        bool hasMovedFromMax = false;
        int step = 0;
        string stopReason = "consecutive decreases limit";

        while (!ct.IsCancellationRequested
               && consecutiveDecreases < parameters.ConsecutiveDecreasesLimit
               && totalDistance < parameters.MaxTotalDistanceMm)
        {
            step++;
            context.Log(LogLevel.Debug,
                $"{tag} #{step}: moving {direction * stepSize:+0.####;-0.####} mm, then settling {parameters.MotionSettleMs} ms");

            if (!await context.MoveRelativeAsync(axis, direction * stepSize, ct).ConfigureAwait(false))
            {
                stopReason = "motion error";
                break;
            }

            await context.SettleAsync(ct).ConfigureAwait(false);

            currentPosition = await context.GetCurrentPositionAsync(ct).ConfigureAwait(false);
            double currentValue = await context.GetMeasurementAsync(ct).ConfigureAwait(false);
            totalDistance += stepSize;

            double relativeDecrease = previousValue > 0
                ? (previousValue - currentValue) / previousValue
                : 0;
            double gradient = (currentValue - previousValue) / stepSize;
            double relativeChange = previousValue != 0
                ? (currentValue - previousValue) / previousValue
                : 0;

            bool isPeak = false;
            bool significantDrop = false;

            if (currentValue > maxValue)
            {
                maxValue = currentValue;
                maxPosition = currentPosition;
                consecutiveDecreases = 0;
                hasMovedFromMax = false;
                isPeak = true;
            }
            else
            {
                consecutiveDecreases++;
                hasMovedFromMax = true;
                if (relativeDecrease > parameters.SignificantDecreaseThreshold)
                    significantDrop = true;
            }

            context.Log(LogLevel.Debug,
                $"{tag} #{step}: value {currentValue:G6} {context.Unit}{(isPeak ? " (new max)" : "")}, " +
                $"max {maxValue:G6}, decreases {consecutiveDecreases}/{parameters.ConsecutiveDecreasesLimit}, " +
                $"dist {totalDistance:F4}/{parameters.MaxTotalDistanceMm:F4} mm, samples {context.FeedbackSampleCount}");

            // Legacy: a significant drop breaks before recording the point.
            if (significantDrop)
            {
                stopReason = $"significant drop {relativeDecrease:P1}";
                break;
            }

            context.RecordDataPoint(new ScanDataPoint
            {
                Value = currentValue,
                Position = currentPosition,
                Timestamp = DateTime.Now,
                Axis = axis,
                StepSize = stepSize,
                Direction = direction,
                Gradient = gradient,
                RelativeChange = relativeChange,
                IsPeak = isPeak,
            });
            context.ReportSample(
                $"{tag} #{step}  {currentValue:G4} {context.Unit}  (max {maxValue:G4})", currentValue);

            previousValue = currentValue;
        }

        if (ct.IsCancellationRequested)
            stopReason = "halted";
        else if (totalDistance >= parameters.MaxTotalDistanceMm)
            stopReason = "max distance reached";

        context.Log(LogLevel.Debug,
            $"{tag}: done after {step} step(s) — {stopReason}; best {maxValue:G6} {context.Unit}");

        // Return to the local maximum if we climbed past it.
        if (hasMovedFromMax)
        {
            context.Log(LogLevel.Debug, $"{tag}: returning to local maximum at {PosText(maxPosition)}");
            if (await context.MoveToPositionAsync(maxPosition, ct).ConfigureAwait(false))
            {
                await context.SettleAsync(ct).ConfigureAwait(false);
                double verification = await context.GetMeasurementAsync(ct).ConfigureAwait(false);
                if (verification > maxValue)
                    maxValue = verification;
            }
        }

        return (maxValue, maxPosition);
    }

    private static string SnrText(double snr)
        => double.IsPositiveInfinity(snr) ? "∞" : snr.ToString("F1");

    private static string PosText(MotionPosition p)
        => $"X{p.X:F4} Y{p.Y:F4} Z{p.Z:F4}";
}
