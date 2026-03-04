using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class HexScanPlugin : IProcessPlugin
{
    public string Id => "motion.hex_scan";
    public string DisplayName => "Hex Scan";
    public string Category => "Motion Control";
    public string Description => "Run a coarse-to-fine hexapod scan on Z/X/Y axes, tracking peak measurement value.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("device", "Hexapod Device", PluginParamType.MotionDevice,
            Category: "Target"),

        new PluginParamDef("channel", "Measurement Channel", PluginParamType.Text,
            Category: "Target", Default: "Keithley_6482.CH1"),

        new PluginParamDef("step_sizes", "Step Sizes (mm)", PluginParamType.Text,
            Category: "Scan", Default: "0.005,0.002,0.001,0.0005"),

        new PluginParamDef("settle_ms", "Settle Time", PluginParamType.Number,
            Category: "Scan", Default: "300", UnitSuffix: "ms",
            Min: 50, Max: 5000, Step: 50, DecimalPlaces: 0),

        new PluginParamDef("max_distance", "Max Distance", PluginParamType.Number,
            Category: "Scan", Default: "5.0", UnitSuffix: "mm",
            Min: 0.1, Max: 20, Step: 0.5, DecimalPlaces: 1),

        new PluginParamDef("avg_samples", "Averaging Samples", PluginParamType.Number,
            Category: "Scan", Default: "3",
            Min: 1, Max: 20, Step: 1, DecimalPlaces: 0),

        new PluginParamDef("exit_threshold", "Exit Threshold", PluginParamType.Text,
            Category: "Scan", Required: false, Default: "",
            UnitSuffix: "A")
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var device = paramValues.GetValueOrDefault("device") ?? "";
        var channel = paramValues.GetValueOrDefault("channel") ?? "Keithley_6482.CH1";
        var stepSizes = paramValues.GetValueOrDefault("step_sizes") ?? "0.005,0.002,0.001,0.0005";
        var settleMs = paramValues.GetValueOrDefault("settle_ms") ?? "300";
        var maxDistance = paramValues.GetValueOrDefault("max_distance") ?? "5.0";
        var avgSamples = paramValues.GetValueOrDefault("avg_samples") ?? "3";
        var exitThreshold = paramValues.GetValueOrDefault("exit_threshold") ?? "";

        var parms = new Dictionary<string, string>
        {
            ["device"] = device,
            ["channel"] = channel,
            ["step_sizes"] = stepSizes,
            ["settle_ms"] = settleMs,
            ["max_distance"] = maxDistance,
            ["avg_samples"] = avgSamples,
            ["timeout"] = "300000"
        };

        if (!string.IsNullOrWhiteSpace(exitThreshold))
            parms["exit_threshold"] = exitThreshold;

        builder.AddAction("scanning", "hexscan", parms);
    }
}
