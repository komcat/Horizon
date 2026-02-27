using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class MotionCommandHandler : ICommandHandler
{
    public string ManagerType => "motion";

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
            errorMessage = "Motion manager is null";
            return false;
        }

        // Use reflection to call methods on UnifiedMotionManager
        // This avoids direct dependency on Horizon.Motion
        var managerType = manager.GetType();

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "movetoposition":
                case "movetonamedposition":
                {
                    var posName = ResolveParam(parameters, "position", context);
                    var method = managerType.GetMethod("MoveToNamedPosition");
                    if (method == null) { errorMessage = "MoveToNamedPosition not found"; return false; }
                    var result = method.Invoke(manager, new object[] { deviceName, posName, true });
                    return result is true;
                }

                case "movetoabs":
                {
                    var axis = ResolveParam(parameters, "axis", context);
                    var posStr = ResolveParam(parameters, "position", context);
                    if (!double.TryParse(posStr, out var pos)) { errorMessage = "Invalid position value"; return false; }
                    var method = managerType.GetMethod("MoveToAbs",
                        new[] { typeof(string), typeof(string), typeof(double), typeof(bool) });
                    if (method == null) { errorMessage = "MoveToAbs not found"; return false; }
                    var result = method.Invoke(manager, new object[] { deviceName, axis, pos, true });
                    return result is true;
                }

                case "moverelative":
                {
                    var axis = ResolveParam(parameters, "axis", context);
                    var distStr = ResolveParam(parameters, "distance", context);
                    if (!double.TryParse(distStr, out var dist)) { errorMessage = "Invalid distance value"; return false; }
                    var method = managerType.GetMethod("MoveRelative",
                        new[] { typeof(string), typeof(string), typeof(double), typeof(bool) });
                    if (method == null) { errorMessage = "MoveRelative not found"; return false; }
                    var result = method.Invoke(manager, new object[] { deviceName, axis, dist, true });
                    return result is true;
                }

                case "homeaxis":
                {
                    var axis = ResolveParam(parameters, "axis", context);
                    var controller = managerType.GetMethod("GetController")?.Invoke(manager, new object[] { deviceName });
                    if (controller == null) { errorMessage = "Controller not found"; return false; }
                    var result = controller.GetType().GetMethod("HomeAxis")?.Invoke(controller, new object[] { axis });
                    return result is true;
                }

                case "stopaxis":
                {
                    var axis = ResolveParam(parameters, "axis", context);
                    var controller = managerType.GetMethod("GetController")?.Invoke(manager, new object[] { deviceName });
                    if (controller == null) { errorMessage = "Controller not found"; return false; }
                    var result = controller.GetType().GetMethod("StopAxis")?.Invoke(controller, new object[] { axis });
                    return result is true;
                }

                case "getposition":
                {
                    var posObj = managerType.GetMethod("GetCurrentPosition")?.Invoke(manager, new object[] { deviceName });
                    if (posObj != null)
                    {
                        var posType = posObj.GetType();
                        var x = (double)(posType.GetProperty("X")?.GetValue(posObj) ?? 0.0);
                        var y = (double)(posType.GetProperty("Y")?.GetValue(posObj) ?? 0.0);
                        var z = (double)(posType.GetProperty("Z")?.GetValue(posObj) ?? 0.0);
                        context.SetValue($"{deviceName}.X", (float)x);
                        context.SetValue($"{deviceName}.Y", (float)y);
                        context.SetValue($"{deviceName}.Z", (float)z);
                    }
                    return true;
                }

                default:
                    errorMessage = $"Unknown motion command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Motion command failed: {ex.InnerException?.Message ?? ex.Message}";
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
