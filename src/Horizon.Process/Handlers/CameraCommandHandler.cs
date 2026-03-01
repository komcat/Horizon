using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class CameraCommandHandler : ICommandHandler
{
    public string ManagerType => "camera";

    public bool ExecuteCommand(
        object? manager,
        string deviceName,
        string methodName,
        Dictionary<string, string> parameters,
        out string errorMessage,
        ProcessContext context)
    {
        errorMessage = string.Empty;

        if (manager == null)
        {
            errorMessage = "Camera manager is null";
            return false;
        }

        var managerType = manager.GetType();

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "capturetofile":
                {
                    var filename = ResolveParam(parameters, "filename", context);
                    bool result;
                    if (!string.IsNullOrEmpty(filename))
                    {
                        var method = managerType.GetMethod("CaptureImage",
                            new[] { typeof(string), typeof(string) });
                        result = method?.Invoke(manager, new object[] { deviceName, filename }) is true;
                    }
                    else
                    {
                        result = managerType.GetMethod("CaptureImage", new[] { typeof(string) })
                            ?.Invoke(manager, new object[] { deviceName }) is true;
                    }

                    if (result)
                    {
                        var contextKey = ResolveParam(parameters, "contextKey", context);
                        if (!string.IsNullOrEmpty(contextKey))
                        {
                            var path = managerType.GetMethod("GetLastCapturedImagePath")
                                ?.Invoke(manager, Array.Empty<object>()) as string;
                            if (path != null)
                                context.SetValue(contextKey, path);
                        }
                    }
                    return result;
                }

                case "startgrabbing":
                {
                    var result = managerType.GetMethod("StartGrabbingWithBroadcast")
                        ?.Invoke(manager, new object[] { deviceName });
                    return result is true;
                }

                case "stopgrabbing":
                {
                    managerType.GetMethod("StopGrabbing")?.Invoke(manager, new object[] { deviceName });
                    return true;
                }

                case "connect":
                {
                    var result = managerType.GetMethod("ConnectCamera")
                        ?.Invoke(manager, new object[] { deviceName });
                    return result is true;
                }

                case "disconnect":
                {
                    var result = managerType.GetMethod("DisconnectCamera")
                        ?.Invoke(manager, new object[] { deviceName });
                    return result is true;
                }

                case "setexposure":
                {
                    // Build ExposureSettings via reflection (Horizon.Process doesn't reference Horizon.Camera)
                    var applyMethod = managerType.GetMethod("ApplyExposureSettings");
                    if (applyMethod == null) { errorMessage = "ApplyExposureSettings not found"; return false; }

                    var settingsType = applyMethod.GetParameters()[1].ParameterType;
                    var settings = Activator.CreateInstance(settingsType)!;
                    var sType = settings.GetType();

                    void SetProp(string prop, string paramKey, double fallback)
                    {
                        var val = ResolveParam(parameters, paramKey, context);
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                            sType.GetProperty(prop)?.SetValue(settings, d);
                        else
                            sType.GetProperty(prop)?.SetValue(settings, fallback);
                    }

                    void SetBoolProp(string prop, string paramKey, bool fallback)
                    {
                        var val = ResolveParam(parameters, paramKey, context);
                        if (bool.TryParse(val, out var b))
                            sType.GetProperty(prop)?.SetValue(settings, b);
                        else
                            sType.GetProperty(prop)?.SetValue(settings, fallback);
                    }

                    SetProp("ExposureTimeUs", "exposureTime", 10000.0);
                    SetProp("Gain", "gain", 1.0);
                    SetBoolProp("AutoExposure", "autoExposure", false);
                    SetBoolProp("AutoGain", "autoGain", false);
                    SetProp("FrameRate", "frameRate", 30.0);

                    var result = applyMethod.Invoke(manager, new[] { deviceName, settings });
                    return result is true;
                }

                case "getexposure":
                {
                    var getMethod = managerType.GetMethod("GetExposureSettings");
                    if (getMethod == null) { errorMessage = "GetExposureSettings not found"; return false; }

                    var settings = getMethod.Invoke(manager, new object[] { deviceName });
                    if (settings == null) { errorMessage = "Failed to get exposure settings"; return false; }

                    var prefix = ResolveParam(parameters, "contextKeyPrefix", context);
                    if (string.IsNullOrEmpty(prefix)) prefix = "exposure";

                    var sType = settings.GetType();
                    var expTime = sType.GetProperty("ExposureTimeUs")?.GetValue(settings);
                    var gain = sType.GetProperty("Gain")?.GetValue(settings);
                    var autoExp = sType.GetProperty("AutoExposure")?.GetValue(settings);
                    var autoGain = sType.GetProperty("AutoGain")?.GetValue(settings);
                    var frameRate = sType.GetProperty("FrameRate")?.GetValue(settings);

                    if (expTime is double et) context.SetValue($"{prefix}.exposureTime", (float)et);
                    if (gain is double g) context.SetValue($"{prefix}.gain", (float)g);
                    if (autoExp is bool ae) context.SetValue($"{prefix}.autoExposure", ae);
                    if (autoGain is bool ag) context.SetValue($"{prefix}.autoGain", ag);
                    if (frameRate is double fr) context.SetValue($"{prefix}.frameRate", (float)fr);

                    return true;
                }

                case "setoutputdirectory":
                {
                    var path = ResolveParam(parameters, "path", context);
                    if (string.IsNullOrEmpty(path)) { errorMessage = "path is required"; return false; }
                    managerType.GetMethod("SetImageOutputDirectory")?.Invoke(manager, new object[] { path });
                    return true;
                }

                case "getstatus":
                {
                    var getMethod = managerType.GetMethod("GetCameraStatus");
                    if (getMethod == null) { errorMessage = "GetCameraStatus not found"; return false; }

                    var status = getMethod.Invoke(manager, new object[] { deviceName });
                    if (status == null) { errorMessage = "Failed to get camera status"; return false; }

                    var prefix = ResolveParam(parameters, "contextKeyPrefix", context);
                    if (string.IsNullOrEmpty(prefix)) prefix = "camera";

                    var sType = status.GetType();
                    var connected = sType.GetProperty("Connected")?.GetValue(status);
                    var grabbing = sType.GetProperty("Grabbing")?.GetValue(status);
                    var model = sType.GetProperty("ModelName")?.GetValue(status);

                    if (connected is bool c) context.SetValue($"{prefix}.connected", c);
                    if (grabbing is bool gb) context.SetValue($"{prefix}.grabbing", gb);
                    if (model is string m) context.SetValue($"{prefix}.model", m);

                    return true;
                }

                default:
                    errorMessage = $"Unknown camera command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Camera command failed: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    private static string ResolveParam(Dictionary<string, string> parms, string key, ProcessContext context)
    {
        if (!parms.TryGetValue(key, out var value)) return string.Empty;
        if (value.StartsWith("context:", StringComparison.OrdinalIgnoreCase))
        {
            var contextKey = value[8..];
            return context.GetString(contextKey);
        }
        return value;
    }
}
