using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class IOCommandHandler : ICommandHandler
{
    public string ManagerType => "io";

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
            errorMessage = "IO manager is null";
            return false;
        }

        var managerType = manager.GetType();

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "setoutput":
                {
                    var pinStr = ResolveParam(parameters, "pin", context);
                    var stateStr = ResolveParam(parameters, "state", context);
                    if (!int.TryParse(pinStr, out var pin)) { errorMessage = "Invalid pin number"; return false; }
                    var state = stateStr.Equals("true", StringComparison.OrdinalIgnoreCase) || stateStr == "1";
                    var method = managerType.GetMethod("SetOutput", new[] { typeof(string), typeof(int), typeof(bool) });
                    var result = method?.Invoke(manager, new object[] { deviceName, pin, state });
                    return result?.ToString() == "Success";
                }

                case "setoutputbyname":
                {
                    var pinName = ResolveParam(parameters, "pinName", context);
                    var stateStr = ResolveParam(parameters, "state", context);
                    var state = stateStr.Equals("true", StringComparison.OrdinalIgnoreCase) || stateStr == "1";
                    var method = managerType.GetMethod("SetOutputByName");
                    var result = method?.Invoke(manager, new object[] { deviceName, pinName, state });
                    return result?.ToString() == "Success";
                }

                case "clearoutputbyname":
                {
                    var pinName = ResolveParam(parameters, "pinName", context);
                    var method = managerType.GetMethod("SetOutputByName");
                    var result = method?.Invoke(manager, new object[] { deviceName, pinName, false });
                    return result?.ToString() == "Success";
                }

                case "readinput":
                {
                    var pinStr = ResolveParam(parameters, "pin", context);
                    if (!int.TryParse(pinStr, out var pin)) { errorMessage = "Invalid pin number"; return false; }

                    // Read inputs via reflection
                    uint inputs = 0;
                    var readMethod = managerType.GetMethod("ReadInputs", new[] { typeof(string), typeof(uint).MakeByRefType() });
                    var args = new object[] { deviceName, inputs };
                    readMethod?.Invoke(manager, args);
                    inputs = (uint)args[1];

                    var state = (inputs & (1u << pin)) != 0;
                    var contextKey = parameters.GetValueOrDefault("contextKey", $"{deviceName}.input.{pin}");
                    context.SetValue(contextKey, state);
                    return true;
                }

                default:
                    errorMessage = $"Unknown IO command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"IO command failed: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    private static string ResolveParam(Dictionary<string, string> parms, string key, ProcessContext context)
    {
        if (!parms.TryGetValue(key, out var value)) return string.Empty;
        if (value.StartsWith("context:", StringComparison.OrdinalIgnoreCase))
            return context.GetString(value[8..]);
        if (value.StartsWith("expr:", StringComparison.OrdinalIgnoreCase))
        {
            var result = ExpressionEvaluator.Evaluate(value[5..], context);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return value;
    }
}
