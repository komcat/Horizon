using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class MoveRelativeThetaPlugin : IProcessPlugin
{
    public string Id => "motion.move_relative_theta";
    public string DisplayName => "Move Relative Theta";
    public string Category => "Motion Control";
    public string Description => "Move U/V/W rotational axes by relative distances simultaneously. Non-zero axes are moved at once.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("device", "Device", PluginParamType.MotionDevice,
            Category: "Target"),
        new PluginParamDef("distance_u", "Distance U", PluginParamType.Number,
            Category: "Distances", Required: false, Default: "0", UnitSuffix: "\u00b0",
            Min: -180, Max: 180, DecimalPlaces: 3),
        new PluginParamDef("distance_v", "Distance V", PluginParamType.Number,
            Category: "Distances", Required: false, Default: "0", UnitSuffix: "\u00b0",
            Min: -180, Max: 180, DecimalPlaces: 3),
        new PluginParamDef("distance_w", "Distance W", PluginParamType.Number,
            Category: "Distances", Required: false, Default: "0", UnitSuffix: "\u00b0",
            Min: -180, Max: 180, DecimalPlaces: 3)
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var device = paramValues.GetValueOrDefault("device") ?? "";

        var parms = new Dictionary<string, string>();

        var du = paramValues.GetValueOrDefault("distance_u") ?? "0";
        var dv = paramValues.GetValueOrDefault("distance_v") ?? "0";
        var dw = paramValues.GetValueOrDefault("distance_w") ?? "0";

        if (!string.IsNullOrEmpty(du) && du != "0") parms["u"] = du;
        if (!string.IsNullOrEmpty(dv) && dv != "0") parms["v"] = dv;
        if (!string.IsNullOrEmpty(dw) && dw != "0") parms["w"] = dw;

        if (parms.Count > 0)
            builder.AddAction(device, "moverelativemulti", parms);
    }
}
