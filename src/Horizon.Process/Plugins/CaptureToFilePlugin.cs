using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class CaptureToFilePlugin : IProcessPlugin
{
    public string Id => "vision.capture_to_file";
    public string DisplayName => "Capture to File";
    public string Category => "Vision";
    public string Description => "Capture an image from a camera and save it to a file.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("camera_name", "Camera", PluginParamType.CameraDevice,
            Category: "Target"),
        new PluginParamDef("filename", "Filename", PluginParamType.Text,
            Category: "Output", Required: false, Default: "")
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var camera = paramValues.GetValueOrDefault("camera_name") ?? "";
        var filename = paramValues.GetValueOrDefault("filename") ?? "";

        var parms = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(filename))
            parms["filename"] = filename;

        builder.AddAction(camera, "capturetofile", parms);
    }
}
