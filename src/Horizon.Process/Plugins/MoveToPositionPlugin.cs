using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class MoveToPositionPlugin : IProcessPlugin
{
    public string Id => "motion.move_to_position";
    public string DisplayName => "Move to Position";
    public string Category => "Motion Control";
    public string Description => "Move a motion device to a named position (taught position from config).";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("device", "Device", PluginParamType.MotionDevice,
            Category: "Target"),
        new PluginParamDef("target_position", "Target Position", PluginParamType.NamedPosition,
            Category: "Target", DependsOnDevice: "device")
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var device = paramValues.GetValueOrDefault("device") ?? "";
        var position = paramValues.GetValueOrDefault("target_position") ?? "";

        builder.AddAction(device, "movetonamedposition", new Dictionary<string, string>
        {
            ["position"] = position
        });
    }
}
