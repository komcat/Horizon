using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class SetAutoExposurePlugin : IProcessPlugin
{
    public string Id => "vision.set_auto_exposure";
    public string DisplayName => "Set Auto Exposure";
    public string Category => "Vision";
    public string Description => "Enable or disable auto exposure for a camera.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("camera_name", "Camera", PluginParamType.CameraDevice,
            Category: "Target"),
        new PluginParamDef("value", "Auto Exposure", PluginParamType.Enum,
            Category: "Settings", Default: "Off", Options: new[] { "On", "Off" })
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var camera = paramValues.GetValueOrDefault("camera_name") ?? "";
        var value = paramValues.GetValueOrDefault("value") ?? "Off";
        var boolValue = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase) ? "True" : "False";

        builder.AddAction(camera, "setexposure", new Dictionary<string, string>
        {
            ["autoExposure"] = boolValue
        });
    }
}
