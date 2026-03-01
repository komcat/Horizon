using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class MoveRelativePlugin : IProcessPlugin
{
    public string Id => "motion.move_relative";
    public string DisplayName => "Move Relative";
    public string Category => "Motion Control";
    public string Description => "Move a single axis by a relative distance (positive or negative).";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("device", "Device", PluginParamType.MotionDevice,
            Category: "Target"),
        new PluginParamDef("axis", "Axis", PluginParamType.Axis,
            Category: "Target", DependsOnDevice: "device"),
        new PluginParamDef("distance", "Distance", PluginParamType.Number,
            Category: "Movement", Default: "0", UnitSuffix: "mm",
            Min: -1000, Max: 1000, DecimalPlaces: 3)
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var device = paramValues.GetValueOrDefault("device") ?? "";
        var axis = paramValues.GetValueOrDefault("axis") ?? "";
        var distance = paramValues.GetValueOrDefault("distance") ?? "0";

        builder.AddAction(device, "moverelative", new Dictionary<string, string>
        {
            ["axis"] = axis,
            ["distance"] = distance
        });
    }
}
