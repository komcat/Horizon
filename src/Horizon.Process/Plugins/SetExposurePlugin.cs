using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class SetExposurePlugin : IProcessPlugin
{
    public string Id => "vision.set_exposure";
    public string DisplayName => "Set Exposure";
    public string Category => "Vision";
    public string Description => "Set the exposure time for a camera.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("camera_name", "Camera", PluginParamType.CameraDevice,
            Category: "Target"),
        new PluginParamDef("value", "Exposure Time", PluginParamType.Number,
            Category: "Settings", Default: "10000", UnitSuffix: "\u00b5s",
            Min: 1, Max: 10000, DecimalPlaces: 0)
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var camera = paramValues.GetValueOrDefault("camera_name") ?? "";
        var value = paramValues.GetValueOrDefault("value") ?? "10000";

        builder.AddAction(camera, "setexposure", new Dictionary<string, string>
        {
            ["exposureTime"] = value
        });
    }
}
