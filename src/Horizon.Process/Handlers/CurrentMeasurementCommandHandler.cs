using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class CurrentMeasurementCommandHandler : ICommandHandler
{
    public string ManagerType => "currentmeasurement";

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
            errorMessage = "Current measurement manager is null";
            return false;
        }

        var managerType = manager.GetType();

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "read_current":
                {
                    var chStr = ResolveParam(parameters, "channel", context);
                    if (!int.TryParse(chStr, out var channel)) { errorMessage = "Invalid channel number"; return false; }

                    double current = 0;
                    var method = managerType.GetMethod("ReadCurrent");
                    var args = new object[] { deviceName, channel, current };
                    var result = method?.Invoke(manager, args);
                    current = (double)args[2];

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"ReadCurrent failed: {result}";
                        return false;
                    }

                    var contextKey = parameters.GetValueOrDefault("contextKey", $"{deviceName}.current.ch{channel}");
                    context.SetValue(contextKey, (float)current);
                    return true;
                }

                case "read_all":
                {
                    double[] currents = Array.Empty<double>();
                    var method = managerType.GetMethod("ReadAllChannels");
                    var args = new object[] { deviceName, currents };
                    var result = method?.Invoke(manager, args);
                    currents = (double[])args[1];

                    if (result?.ToString() != "Success")
                    {
                        errorMessage = $"ReadAllChannels failed: {result}";
                        return false;
                    }

                    for (int i = 0; i < currents.Length; i++)
                    {
                        var contextKey = $"{deviceName}.current.ch{i + 1}";
                        context.SetValue(contextKey, (float)currents[i]);
                    }
                    return true;
                }

                default:
                    errorMessage = $"Unknown current measurement command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Current measurement command failed: {ex.InnerException?.Message ?? ex.Message}";
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
