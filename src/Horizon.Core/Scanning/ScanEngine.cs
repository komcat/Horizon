using Horizon.Motion;

namespace Horizon.Core.Scanning;

/// <summary>
/// Coarse-to-fine hexapod scanning engine.
/// Scans each axis bidirectionally at progressively finer step sizes,
/// tracking peak measurement values. Faithful port of CoarseToFineStrategy.
/// </summary>
public sealed class ScanEngine
{
    private readonly UnifiedMotionManager _motion;
    private readonly MeasurementHub _hub;
    private readonly ILogSink? _log;

    public event Action<double, string>? ProgressChanged;
    public event Action<ScanDataPoint>? DataPointRecorded;
    public event Action<string>? StatusChanged;

    public ScanEngine(UnifiedMotionManager motion, MeasurementHub hub, ILogSink? log = null)
    {
        _motion = motion;
        _hub = hub;
        _log = log;
    }

    /// <summary>
    /// Execute the scan. Runs synchronously — caller should wrap in Task.Run.
    /// </summary>
    public ScanResults Execute(string deviceName, string channelKey,
                                ScanParameters parameters, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new ScanResults();
        int stepIndex = 0;

        // Register immediate motor stop on cancellation
        using var stopReg = ct.Register(() => _motion.StopAll(deviceName));

        // Record baseline measurement
        results.BaselineValue = GetAveragedMeasurement(channelKey, parameters.AveragingSamples, ct);
        double globalPeakValue = results.BaselineValue;

        var startPos = _motion.GetCurrentPosition(deviceName);
        if (startPos != null)
        {
            results.PeakX = startPos.X;
            results.PeakY = startPos.Y;
            results.PeakZ = startPos.Z;
        }

        int totalSteps = parameters.StepSizes.Length * parameters.Axes.Length;
        int completedSteps = 0;

        try
        {
            // Outer loop: for each step size (coarse → fine)
            foreach (var stepSize in parameters.StepSizes)
            {
                ct.ThrowIfCancellationRequested();

                // Middle loop: for each axis
                foreach (var axis in parameters.Axes)
                {
                    ct.ThrowIfCancellationRequested();

                    double progress = (double)completedSteps / totalSteps;
                    StatusChanged?.Invoke($"Scanning {axis} axis @ {stepSize:F3}mm step");
                    ProgressChanged?.Invoke(progress, $"{axis} @ {stepSize:F3}mm");

                    var (bestValue, bestOffset) = ScanSingleAxis(
                        deviceName, channelKey, axis, stepSize, parameters, results, ref stepIndex, ct);

                    // Update global peak if this axis found something better
                    if (bestValue > globalPeakValue)
                    {
                        globalPeakValue = bestValue;
                        var pos = _motion.GetCurrentPosition(deviceName);
                        if (pos != null)
                        {
                            results.PeakX = pos.X;
                            results.PeakY = pos.Y;
                            results.PeakZ = pos.Z;
                        }
                    }

                    results.PeakValue = globalPeakValue;
                    completedSteps++;
                }
            }

            results.Completed = true;
        }
        catch (OperationCanceledException)
        {
            _motion.StopAll(deviceName);
            _log?.Warn($"HexScan: Cancelled — motors stopped on {deviceName}");
            results.Cancelled = true;
        }

        results.PeakValue = globalPeakValue;
        results.Elapsed = sw.Elapsed;

        StatusChanged?.Invoke(results.Cancelled ? "Scan cancelled" : "Scan complete");
        ProgressChanged?.Invoke(1.0, results.Cancelled ? "Cancelled" : "Done");

        return results;
    }

    /// <summary>
    /// Scan a single axis bidirectionally, then move to the best position found.
    /// </summary>
    private (double bestValue, double bestOffset) ScanSingleAxis(
        string deviceName, string channelKey, string axis, double stepSize,
        ScanParameters parameters, ScanResults results, ref int stepIndex, CancellationToken ct)
    {
        // Record start position for this axis
        var startPos = _motion.GetCurrentPosition(deviceName);
        double startAxisPos = GetAxisValue(startPos, axis);

        _log?.Info($"HexScan: {axis} axis @ {stepSize * 1000:F1}um — start={startAxisPos:F4}");

        // Scan positive direction
        StatusChanged?.Invoke($"Scanning {axis}+ @ {stepSize:F3}mm");
        var (posMaxValue, posMaxPosition) = ScanDirection(
            deviceName, channelKey, axis, stepSize, +1, startAxisPos,
            parameters, results, ref stepIndex, ct);
        _log?.Info($"HexScan: {axis}+ peak={posMaxValue:E3} @ {posMaxPosition:F4}");

        // Return to start
        ct.ThrowIfCancellationRequested();
        MoveAxisAbsolute(deviceName, axis, startAxisPos);
        Thread.Sleep(parameters.SettleTimeMs);

        // Scan negative direction
        StatusChanged?.Invoke($"Scanning {axis}- @ {stepSize:F3}mm");
        var (negMaxValue, negMaxPosition) = ScanDirection(
            deviceName, channelKey, axis, stepSize, -1, startAxisPos,
            parameters, results, ref stepIndex, ct);
        _log?.Info($"HexScan: {axis}- peak={negMaxValue:E3} @ {negMaxPosition:F4}");

        // Return to start
        ct.ThrowIfCancellationRequested();
        MoveAxisAbsolute(deviceName, axis, startAxisPos);
        Thread.Sleep(parameters.SettleTimeMs);

        // Move to the best position found (positive or negative peak)
        double bestValue;
        double bestPosition;
        string bestDir;
        if (posMaxValue >= negMaxValue)
        {
            bestValue = posMaxValue;
            bestPosition = posMaxPosition;
            bestDir = "+";
        }
        else
        {
            bestValue = negMaxValue;
            bestPosition = negMaxPosition;
            bestDir = "-";
        }

        ct.ThrowIfCancellationRequested();
        MoveAxisAbsolute(deviceName, axis, bestPosition);
        Thread.Sleep(parameters.SettleTimeMs);
        _log?.Info($"HexScan: {axis} best={bestDir} — moved to {bestPosition:F4}, value={bestValue:E3}");

        return (bestValue, bestPosition - startAxisPos);
    }

    /// <summary>
    /// Scan in one direction (positive or negative), tracking peak value.
    /// Returns (maxValue, maxPosition).
    /// </summary>
    private (double maxValue, double maxPosition) ScanDirection(
        string deviceName, string channelKey, string axis, double stepSize, int direction,
        double startAxisPos, ScanParameters parameters, ScanResults results,
        ref int stepIndex, CancellationToken ct)
    {
        double maxValue = double.MinValue;
        double maxPosition = startAxisPos;
        int consecutiveDecreases = 0;
        double totalDistance = 0.0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Move relative by stepSize * direction
            double moveDistance = stepSize * direction;
            bool moveOk = _motion.MoveRelative(deviceName, axis, moveDistance, blocking: true);
            if (!moveOk) break;

            totalDistance += stepSize;
            Thread.Sleep(parameters.SettleTimeMs);

            // Read averaged measurement
            double value = GetAveragedMeasurement(channelKey, parameters.AveragingSamples, ct);

            // Get current position
            var currentPos = _motion.GetCurrentPosition(deviceName);
            double currentAxisPos = GetAxisValue(currentPos, axis);

            bool isPeak = false;

            if (value > maxValue)
            {
                maxValue = value;
                maxPosition = currentAxisPos;
                consecutiveDecreases = 0;
                isPeak = true;
            }
            else
            {
                consecutiveDecreases++;

                // Check for significant drop from peak
                if (maxValue > 0 && (maxValue - value) / maxValue > parameters.SignificantDecreaseThreshold)
                    break;
            }

            // Record data point
            stepIndex++;
            var dataPoint = new ScanDataPoint
            {
                StepIndex = stepIndex,
                Axis = axis,
                Direction = direction,
                Position = currentAxisPos,
                Value = value,
                IsPeak = isPeak,
                StepSize = stepSize
            };
            results.DataPoints.Add(dataPoint);
            DataPointRecorded?.Invoke(dataPoint);

            // Check exit threshold — stop early if measurement reached target
            if (parameters.ExitThreshold.HasValue && value >= parameters.ExitThreshold.Value)
            {
                results.Completed = true;
                break;
            }

            // Check stop conditions
            if (consecutiveDecreases >= parameters.ConsecutiveDecreasesLimit)
                break;

            if (totalDistance >= parameters.MaxTotalDistanceMm)
                break;
        }

        return (maxValue, maxPosition);
    }

    /// <summary>
    /// Read N samples from MeasurementHub with 50ms between, return average.
    /// </summary>
    private double GetAveragedMeasurement(string channelKey, int sampleCount, CancellationToken ct)
    {
        var channel = _hub.GetChannel(channelKey);
        if (channel == null) return 0.0;

        var timestamps = new double[500];
        var values = new double[500];
        double sum = 0.0;
        int collected = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0) Thread.Sleep(50);

            int count = channel.Snapshot(timestamps, values);
            if (count > 0)
            {
                sum += Math.Abs(values[count - 1]);
                collected++;
            }
        }

        return collected > 0 ? sum / collected : 0.0;
    }

    private void MoveAxisAbsolute(string deviceName, string axis, double position)
    {
        _motion.MoveToAbs(deviceName, axis, position, blocking: true);
    }

    private static double GetAxisValue(Motion.Models.PositionStruct? pos, string axis)
    {
        if (pos == null) return 0.0;
        return axis.ToUpperInvariant() switch
        {
            "X" => pos.X,
            "Y" => pos.Y,
            "Z" => pos.Z,
            "U" => pos.U,
            "V" => pos.V,
            "W" => pos.W,
            _ => 0.0
        };
    }
}
