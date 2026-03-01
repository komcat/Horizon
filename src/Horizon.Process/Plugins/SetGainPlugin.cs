using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class SetGainPlugin : IProcessPlugin
{
    public string Id => "vision.set_gain";
    public string DisplayName => "Set Gain";
    public string Category => "Vision";
    public string Description => "Set the gain value for a camera.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("camera_name", "Camera", PluginParamType.CameraDevice,
            Category: "Target"),
        new PluginParamDef("value", "Gain", PluginParamType.Number,
            Category: "Settings", Default: "1.0",
            Min: 0, Max: 10, DecimalPlaces: 1)
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var camera = paramValues.GetValueOrDefault("camera_name") ?? "";
        var value = paramValues.GetValueOrDefault("value") ?? "1.0";

        builder.AddAction(camera, "setexposure", new Dictionary<string, string>
        {
            ["gain"] = value
        });
    }
}
