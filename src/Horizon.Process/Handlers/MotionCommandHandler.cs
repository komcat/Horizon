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

                case "moverelativemulti":
                {
                    // Move multiple axes simultaneously: start all non-blocking, then wait
                    var moveMethod = managerType.GetMethod("MoveRelative",
                        new[] { typeof(string), typeof(string), typeof(double), typeof(bool) });
                    if (moveMethod == null) { errorMessage = "MoveRelative not found"; return false; }

                    var axisNames = new[] { "x", "y", "z", "u", "v", "w" };
                    var movedAxes = new List<string>();

                    // Start all non-blocking
                    foreach (var axisKey in axisNames)
                    {
                        var distStr = ResolveParam(parameters, axisKey, context);
                        if (string.IsNullOrEmpty(distStr)) continue;
                        if (!double.TryParse(distStr, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var dist) || Math.Abs(dist) < 1e-9)
                            continue;

                        var axisName = axisKey.ToUpperInvariant();
                        var ok = moveMethod.Invoke(manager, new object[] { deviceName, axisName, dist, false });
                        if (ok is not true)
                        {
                            errorMessage = $"Failed to start move on axis {axisName}";
                            return false;
                        }
                        movedAxes.Add(axisName);
                    }

                    if (movedAxes.Count == 0) return true; // nothing to move

                    // Wait for all axes to finish
                    var controller = managerType.GetMethod("GetController")?.Invoke(manager, new object[] { deviceName });
                    if (controller == null) { errorMessage = "Controller not found for wait"; return false; }

                    var isOnTargetMethod = controller.GetType().GetMethod("IsOnTarget");
                    if (isOnTargetMethod == null)
                    {
                        // Fallback: wait for IsDeviceInMotion to be false
                        var isMovingMethod = controller.GetType().GetMethod("IsDeviceInMotion");
                        if (isMovingMethod == null) return true; // no way to wait

                        var deadline = Environment.TickCount64 + 60000;
                        while (Environment.TickCount64 < deadline)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            if (isMovingMethod.Invoke(controller, Array.Empty<object>()) is not true)
                                return true;
                            Thread.Sleep(50);
                        }
                        errorMessage = "Timeout waiting for multi-axis move";
                        return false;
                    }

                    // Wait per-axis on target
                    var deadlineMs = Environment.TickCount64 + 60000;
                    while (Environment.TickCount64 < deadlineMs)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();

                        bool allDone = true;
                        foreach (var axis in movedAxes)
                        {
                            if (isOnTargetMethod.Invoke(controller, new object[] { axis }) is not true)
                            {
                                allDone = false;
                                break;
                            }
                        }
                        if (allDone) return true;
                        Thread.Sleep(50);
                    }
                    errorMessage = "Timeout waiting for multi-axis move to complete";
                    return false;
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
                        var u = (double)(posType.GetProperty("U")?.GetValue(posObj) ?? 0.0);
                        var v = (double)(posType.GetProperty("V")?.GetValue(posObj) ?? 0.0);
                        var w = (double)(posType.GetProperty("W")?.GetValue(posObj) ?? 0.0);
                        context.SetValue($"{deviceName}.X", (float)x);
                        context.SetValue($"{deviceName}.Y", (float)y);
                        context.SetValue($"{deviceName}.Z", (float)z);
                        context.SetValue($"{deviceName}.U", (float)u);
                        context.SetValue($"{deviceName}.V", (float)v);
                        context.SetValue($"{deviceName}.W", (float)w);
                    }
                    return true;
                }

                case "movetoabs6":
                {
                    // Build PositionStruct via reflection
                    var moveMethod = managerType.GetMethod("MoveToAbs6");
                    if (moveMethod == null) { errorMessage = "MoveToAbs6 not found"; return false; }

                    var posStructType = moveMethod.GetParameters()[1].ParameterType;
                    var posStruct = Activator.CreateInstance(posStructType)!;
                    var psType = posStruct.GetType();

                    foreach (var axisName in new[] { "x", "y", "z", "u", "v", "w" })
                    {
                        var val = ResolveParam(parameters, axisName, context);
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            psType.GetProperty(axisName.ToUpperInvariant())?.SetValue(posStruct, d);
                        }
                    }

                    var result = moveMethod.Invoke(manager, new[] { deviceName, posStruct, true });
                    return result is true;
                }

                case "stopall":
                {
                    var method = managerType.GetMethod("StopAll", new[] { typeof(string) });
                    if (method == null) { errorMessage = "StopAll not found"; return false; }
                    var result = method.Invoke(manager, new object[] { deviceName });
                    return result is true;
                }

                case "stopalldevices":
                {
                    var method = managerType.GetMethod("StopAllDevices");
                    if (method == null) { errorMessage = "StopAllDevices not found"; return false; }
                    method.Invoke(manager, Array.Empty<object>());
                    return true;
                }

                case "setvelocity":
                {
                    var speedStr = ResolveParam(parameters, "speed", context);
                    if (!double.TryParse(speedStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var speed))
                    {
                        errorMessage = "Invalid speed value";
                        return false;
                    }

                    var controller = managerType.GetMethod("GetController")?.Invoke(manager, new object[] { deviceName });
                    if (controller == null) { errorMessage = "Controller not found"; return false; }

                    // Try SetVelocity on the concrete controller (not in IMotionController interface)
                    var setVelMethod = controller.GetType().GetMethod("SetVelocity");
                    if (setVelMethod != null)
                    {
                        var result = setVelMethod.Invoke(controller, new object[] { speed });
                        return result is not false;
                    }

                    // Fallback: store in context for subsequent moves to reference
                    context.SetValue($"{deviceName}.velocity", (float)speed);
                    return true;
                }

                case "ismoving":
                {
                    var method = managerType.GetMethod("IsMoving", new[] { typeof(string) });
                    if (method == null) { errorMessage = "IsMoving not found"; return false; }
                    var result = method.Invoke(manager, new object[] { deviceName });
                    var moving = result is true;

                    var contextKey = ResolveParam(parameters, "contextKey", context);
                    if (string.IsNullOrEmpty(contextKey)) contextKey = "isMoving";
                    context.SetValue(contextKey, moving);

                    return true;
                }

                case "isontarget":
                {
                    var axis = ResolveParam(parameters, "axis", context);
                    var controller = managerType.GetMethod("GetController")?.Invoke(manager, new object[] { deviceName });
                    if (controller == null) { errorMessage = "Controller not found"; return false; }

                    var result = controller.GetType().GetMethod("IsOnTarget")?.Invoke(controller, new object[] { axis });
                    var onTarget = result is true;

                    var contextKey = ResolveParam(parameters, "contextKey", context);
                    if (string.IsNullOrEmpty(contextKey)) contextKey = "isOnTarget";
                    context.SetValue(contextKey, onTarget);

                    return true;
                }

                case "waitforposition":
                {
                    var axis = ResolveParam(parameters, "axis", context);
                    var timeoutStr = ResolveParam(parameters, "timeoutMs", context);
                    var pollStr = ResolveParam(parameters, "pollIntervalMs", context);

                    if (!int.TryParse(timeoutStr, out var timeoutMs) || timeoutMs <= 0) timeoutMs = 30000;
                    if (!int.TryParse(pollStr, out var pollMs) || pollMs <= 0) pollMs = 50;

                    var controller = managerType.GetMethod("GetController")?.Invoke(manager, new object[] { deviceName });
                    if (controller == null) { errorMessage = "Controller not found"; return false; }

                    var isOnTargetMethod = controller.GetType().GetMethod("IsOnTarget");
                    if (isOnTargetMethod == null) { errorMessage = "IsOnTarget not found"; return false; }

                    var deadline = Environment.TickCount64 + timeoutMs;
                    while (Environment.TickCount64 < deadline)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();

                        var onTarget = isOnTargetMethod.Invoke(controller, new object[] { axis }) is true;
                        if (onTarget) return true;

                        Thread.Sleep(pollMs);
                    }

                    errorMessage = $"Timeout waiting for axis '{axis}' to reach target ({timeoutMs}ms)";
                    return false;
                }

                default:
                    errorMessage = $"Unknown motion command: {methodName}";
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Motion command cancelled";
            return false;
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
