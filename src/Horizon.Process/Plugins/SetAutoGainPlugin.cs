using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class SetAutoGainPlugin : IProcessPlugin
{
    public string Id => "vision.set_auto_gain";
    public string DisplayName => "Set Auto Gain";
    public string Category => "Vision";
    public string Description => "Enable or disable auto gain for a camera.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("camera_name", "Camera", PluginParamType.CameraDevice,
            Category: "Target"),
        new PluginParamDef("value", "Auto Gain", PluginParamType.Enum,
            Category: "Settings", Default: "Off", Options: new[] { "On", "Off" })
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var camera = paramValues.GetValueOrDefault("camera_name") ?? "";
        var value = paramValues.GetValueOrDefault("value") ?? "Off";
        var boolValue = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase) ? "True" : "False";

        builder.AddAction(camera, "setexposure", new Dictionary<string, string>
        {
            ["autoGain"] = boolValue
        });
    }
}
