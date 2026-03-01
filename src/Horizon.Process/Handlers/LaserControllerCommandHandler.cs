using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class LaserControllerCommandHandler : ICommandHandler
{
    public string ManagerType => "lasercontroller";

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
            errorMessage = "Laser controller manager is null";
            return false;
        }

        var managerType = manager.GetType();

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "set_temperature":
                {
                    var tempStr = ResolveParam(parameters, "temperature", context);
                    if (!double.TryParse(tempStr, out var temp)) { errorMessage = "Invalid temperature value"; return false; }

                    var method = managerType.GetMethod("SetTemperature");
                    var result = method?.Invoke(manager, new object[] { deviceName, temp });

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"SetTemperature failed: {result}";
                        return false;
                    }
                    return true;
                }

                case "read_temperature":
                {
                    double temp = 0;
                    var method = managerType.GetMethod("ReadTemperature");
                    var args = new object[] { deviceName, temp };
                    var result = method?.Invoke(manager, args);
                    temp = (double)args[1];

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"ReadTemperature failed: {result}";
                        return false;
                    }

                    var contextKey = parameters.GetValueOrDefault("contextKey", $"{deviceName}.temperature");
                    context.SetValue(contextKey, (float)temp);
                    return true;
                }

                case "tec_on":
                {
                    var method = managerType.GetMethod("TECOn");
                    var result = method?.Invoke(manager, new object[] { deviceName });

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"TECOn failed: {result}";
                        return false;
                    }
                    return true;
                }

                case "tec_off":
                {
                    var method = managerType.GetMethod("TECOff");
                    var result = method?.Invoke(manager, new object[] { deviceName });

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"TECOff failed: {result}";
                        return false;
                    }
                    return true;
                }

                case "set_laser_current":
                {
                    var currentStr = ResolveParam(parameters, "current", context);
                    if (!double.TryParse(currentStr, out var current)) { errorMessage = "Invalid current value"; return false; }

                    var method = managerType.GetMethod("SetLaserCurrent");
                    var result = method?.Invoke(manager, new object[] { deviceName, current });

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"SetLaserCurrent failed: {result}";
                        return false;
                    }
                    return true;
                }

                case "read_laser_current":
                {
                    double current = 0;
                    var method = managerType.GetMethod("ReadLaserCurrent");
                    var args = new object[] { deviceName, current };
                    var result = method?.Invoke(manager, args);
                    current = (double)args[1];

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"ReadLaserCurrent failed: {result}";
                        return false;
                    }

                    var contextKey = parameters.GetValueOrDefault("contextKey", $"{deviceName}.laser_current");
                    context.SetValue(contextKey, (float)current);
                    return true;
                }

                case "laser_on":
                {
                    var method = managerType.GetMethod("LaserOn");
                    var result = method?.Invoke(manager, new object[] { deviceName });

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"LaserOn failed: {result}";
                        return false;
                    }
                    return true;
                }

                case "laser_off":
                {
                    var method = managerType.GetMethod("LaserOff");
                    var result = method?.Invoke(manager, new object[] { deviceName });

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"LaserOff failed: {result}";
                        return false;
                    }
                    return true;
                }

                default:
                    errorMessage = $"Unknown laser controller command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Laser controller command failed: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    private static string ResolveParam(Dictionary<string, string> parms, string key, ProcessContext context)
    {
        if (!parms.TryGetValue(key, out var value)) return string.Empty;
        if (value.StartsWith("context:", StringComparison.OrdinalIgnoreCase))
            return context.GetString(value[8..]);
        return value;
    }
}
