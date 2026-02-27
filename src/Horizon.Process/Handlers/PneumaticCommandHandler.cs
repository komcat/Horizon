using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class PneumaticCommandHandler : ICommandHandler
{
    public string ManagerType => "pneumatic";

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
            errorMessage = "Pneumatic manager is null";
            return false;
        }

        var managerType = manager.GetType();

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "extend":
                {
                    var result = managerType.GetMethod("Extend", new[] { typeof(string) })
                        ?.Invoke(manager, new object[] { deviceName });
                    return result?.ToString() == "Success";
                }

                case "retract":
                {
                    var result = managerType.GetMethod("Retract", new[] { typeof(string) })
                        ?.Invoke(manager, new object[] { deviceName });
                    return result?.ToString() == "Success";
                }

                case "extendandwait":
                {
                    var result = managerType.GetMethod("ExtendAndWait", new[] { typeof(string), typeof(int) })
                        ?.Invoke(manager, new object[] { deviceName, 50 });
                    return result?.ToString() == "Success";
                }

                case "retractandwait":
                {
                    var result = managerType.GetMethod("RetractAndWait", new[] { typeof(string), typeof(int) })
                        ?.Invoke(manager, new object[] { deviceName, 50 });
                    return result?.ToString() == "Success";
                }

                case "getstatus":
                {
                    var slide = managerType.GetMethod("GetSlide")?.Invoke(manager, new object[] { deviceName });
                    if (slide == null) { errorMessage = "Slide not found"; return false; }
                    var status = slide.GetType().GetMethod("GetStatus")?.Invoke(slide, null);
                    if (status != null)
                    {
                        var pos = status.GetType().GetProperty("Position")?.GetValue(status);
                        context.SetValue($"{deviceName}.position", pos?.ToString() ?? "Unknown");
                    }
                    return true;
                }

                default:
                    errorMessage = $"Unknown pneumatic command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Pneumatic command failed: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }
}
