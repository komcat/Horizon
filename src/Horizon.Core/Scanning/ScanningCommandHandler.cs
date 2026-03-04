using System.Globalization;
using Horizon.Motion;
using Horizon.Process;
using Horizon.Process.Interfaces;

namespace Horizon.Core.Scanning;

public sealed class ScanningCommandHandler : ICommandHandler
{
    private readonly UnifiedMotionManager _motion;
    private readonly MeasurementHub _hub;
    private readonly ILogSink? _log;

    public string ManagerType => "scanning";

    public ScanningCommandHandler(UnifiedMotionManager motion, MeasurementHub hub, ILogSink? log = null)
    {
        _motion = motion;
        _hub = hub;
        _log = log;
    }

    public bool ExecuteCommand(
        object? manager,
        string deviceName,
        string methodName,
        Dictionary<string, string> parameters,
        out string errorMessage,
        ProcessContext context)
    {
        errorMessage = string.Empty;

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "hexscan":
                    return ExecuteHexScan(parameters, context, out errorMessage);

                default:
                    errorMessage = $"Unknown scanning command: {methodName}";
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Scan cancelled";
            _log?.Warn("HexScan: Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"Scan failed: {ex.Message}";
            _log?.Error($"HexScan: {ex.Message}");
            return false;
        }
    }

    private bool ExecuteHexScan(Dictionary<string, string> parameters, ProcessContext context, out string errorMessage)
    {
        errorMessage = string.Empty;

        var device = parameters.GetValueOrDefault("device", "");
        var channel = parameters.GetValueOrDefault("channel", "");

        if (string.IsNullOrEmpty(device))
        {
            errorMessage = "No motion device specified";
            return false;
        }

        if (string.IsNullOrEmpty(channel))
        {
            errorMessage = "No measurement channel specified";
            return false;
        }

        var ctrl = _motion.GetController(device);
        if (ctrl == null || !ctrl.IsConnected)
        {
            errorMessage = $"Device '{device}' not connected";
            return false;
        }

        // Parse step sizes
        var stepSizesStr = parameters.GetValueOrDefault("step_sizes", "0.005,0.002,0.001,0.0005");
        var stepSizes = stepSizesStr.Split(',')
            .Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0)
            .Where(v => v > 0)
            .ToArray();

        if (stepSizes.Length == 0)
        {
            errorMessage = "No valid step sizes specified";
            return false;
        }

        // Parse optional parameters
        var settleMs = int.TryParse(parameters.GetValueOrDefault("settle_ms", "300"), out var s) ? s : 300;
        var maxDist = double.TryParse(parameters.GetValueOrDefault("max_distance", "5.0"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 5.0;
        var samples = int.TryParse(parameters.GetValueOrDefault("avg_samples", "3"), out var n) ? n : 3;

        // Parse optional exit threshold
        double? exitThreshold = null;
        var threshStr = parameters.GetValueOrDefault("exit_threshold", "");
        if (!string.IsNullOrEmpty(threshStr) &&
            double.TryParse(threshStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var thresh))
        {
            exitThreshold = thresh;
        }

        var scanParams = new ScanParameters
        {
            StepSizes = stepSizes,
            Axes = ["Z", "X", "Y"],
            SettleTimeMs = settleMs,
            ConsecutiveDecreasesLimit = 1,
            SignificantDecreaseThreshold = 0.10,
            MaxTotalDistanceMm = maxDist,
            ImprovementThreshold = 0.02,
            AveragingSamples = samples,
            ExitThreshold = exitThreshold
        };

        _log?.Info($"HexScan: Starting on {device}, channel={channel}, steps=[{stepSizesStr}]"
            + (exitThreshold.HasValue ? $", exitThreshold={exitThreshold.Value:E3}" : ""));

        var engine = new ScanEngine(_motion, _hub, _log);

        engine.StatusChanged += msg => _log?.Info($"HexScan: {msg}");

        var results = engine.Execute(device, channel, scanParams, context.CancellationToken);

        // Store results in process context
        context.SetValue("hexscan.peak", (float)results.PeakValue);
        context.SetValue("hexscan.baseline", (float)results.BaselineValue);
        context.SetValue("hexscan.peak_x", (float)results.PeakX);
        context.SetValue("hexscan.peak_y", (float)results.PeakY);
        context.SetValue("hexscan.peak_z", (float)results.PeakZ);
        context.SetValue("hexscan.completed", results.Completed);
        context.SetValue("hexscan.points", (float)results.DataPoints.Count);

        _log?.Info($"HexScan: Done — peak={results.PeakValue:E4}, baseline={results.BaselineValue:E4}, points={results.DataPoints.Count}, elapsed={results.Elapsed.TotalSeconds:F1}s");

        if (results.Cancelled)
        {
            errorMessage = "Scan was cancelled";
            return false;
        }

        return true;
    }
}
