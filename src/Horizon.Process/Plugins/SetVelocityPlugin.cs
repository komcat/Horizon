using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class SetVelocityPlugin : IProcessPlugin
{
    public string Id => "motion.set_velocity";
    public string DisplayName => "Set Velocity";
    public string Category => "Motion Control";
    public string Description => "Set the motion speed for a device. Applied to subsequent moves.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("device", "Device", PluginParamType.MotionDevice,
            Category: "Target"),
        new PluginParamDef("speed", "Speed", PluginParamType.Number,
            Category: "Settings", Default: "10", UnitSuffix: "mm/s",
            Min: 0.1, Max: 70, DecimalPlaces: 1)
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var device = paramValues.GetValueOrDefault("device") ?? "";
        var speed = paramValues.GetValueOrDefault("speed") ?? "10";

        builder.AddAction(device, "setvelocity", new Dictionary<string, string>
        {
            ["speed"] = speed
        });
    }
}
