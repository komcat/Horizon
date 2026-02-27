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
                    var result = managerType.GetMethod("CaptureImage")
                        ?.Invoke(manager, new object[] { deviceName });
                    return result is true;
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
}
