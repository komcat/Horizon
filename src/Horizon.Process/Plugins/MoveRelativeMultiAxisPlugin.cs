using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class MoveRelativeMultiAxisPlugin : IProcessPlugin
{
    public string Id => "motion.move_relative_multi";
    public string DisplayName => "Move Relative Multi-Axis";
    public string Category => "Motion Control";
    public string Description => "Move X/Y/Z axes by relative distances simultaneously. Non-zero axes are moved at once.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("device", "Device", PluginParamType.MotionDevice,
            Category: "Target"),
        new PluginParamDef("distance_x", "Distance X", PluginParamType.Number,
            Category: "Distances", Required: false, Default: "0", UnitSuffix: "mm",
            Min: -1000, Max: 1000, DecimalPlaces: 3),
        new PluginParamDef("distance_y", "Distance Y", PluginParamType.Number,
            Category: "Distances", Required: false, Default: "0", UnitSuffix: "mm",
            Min: -1000, Max: 1000, DecimalPlaces: 3),
        new PluginParamDef("distance_z", "Distance Z", PluginParamType.Number,
            Category: "Distances", Required: false, Default: "0", UnitSuffix: "mm",
            Min: -1000, Max: 1000, DecimalPlaces: 3)
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var device = paramValues.GetValueOrDefault("device") ?? "";

        var parms = new Dictionary<string, string>();

        var dx = paramValues.GetValueOrDefault("distance_x") ?? "0";
        var dy = paramValues.GetValueOrDefault("distance_y") ?? "0";
        var dz = paramValues.GetValueOrDefault("distance_z") ?? "0";

        if (!string.IsNullOrEmpty(dx) && dx != "0") parms["x"] = dx;
        if (!string.IsNullOrEmpty(dy) && dy != "0") parms["y"] = dy;
        if (!string.IsNullOrEmpty(dz) && dz != "0") parms["z"] = dz;

        if (parms.Count > 0)
            builder.AddAction(device, "moverelativemulti", parms);
    }
}
