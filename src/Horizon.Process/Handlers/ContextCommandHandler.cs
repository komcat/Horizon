using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class ContextCommandHandler : ICommandHandler
{
    public string ManagerType => "context";

    public bool ExecuteCommand(
        object? manager,
        string deviceName,
        string methodName,
        Dictionary<string, string> parameters,
        out string errorMessage,
        ProcessContext context)
    {
        errorMessage = string.Empty;

        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "setvalue":
                {
                    var key = parameters.GetValueOrDefault("key", "");
                    var value = parameters.GetValueOrDefault("value", "");
                    if (string.IsNullOrEmpty(key)) { errorMessage = "Key is required"; return false; }

                    if (float.TryParse(value, out var numVal))
                        context.SetValue(key, numVal);
                    else if (bool.TryParse(value, out var boolVal))
                        context.SetValue(key, boolVal);
                    else
                        context.SetValue(key, value);
                    return true;
                }

                case "getvalue":
                {
                    var key = parameters.GetValueOrDefault("key", "");
                    var targetKey = parameters.GetValueOrDefault("targetKey", key);
                    if (string.IsNullOrEmpty(key)) { errorMessage = "Key is required"; return false; }
                    var value = context.GetString(key);
                    context.SetValue(targetKey, value);
                    return true;
                }

                case "calculate":
                {
                    var key = parameters.GetValueOrDefault("key", "");
                    var op = parameters.GetValueOrDefault("op", "add");
                    var valueStr = parameters.GetValueOrDefault("value", "0");
                    if (!float.TryParse(valueStr, out var operand)) { errorMessage = "Invalid operand"; return false; }

                    var current = context.GetNumeric(key);
                    var result = op.ToLowerInvariant() switch
                    {
                        "add" => current + operand,
                        "sub" or "subtract" => current - operand,
                        "mul" or "multiply" => current * operand,
                        "div" or "divide" => operand != 0 ? current / operand : 0,
                        _ => current
                    };
                    context.SetValue(key, result);
                    return true;
                }

                case "evaluate":
                {
                    var expression = parameters.GetValueOrDefault("expression", "");
                    var resultKey = parameters.GetValueOrDefault("result", "");
                    if (string.IsNullOrEmpty(expression)) { errorMessage = "Expression is required"; return false; }
                    if (string.IsNullOrEmpty(resultKey)) { errorMessage = "Result key is required"; return false; }

                    var result = ExpressionEvaluator.Evaluate(expression, context);
                    context.SetValue(resultKey, result);
                    return true;
                }

                case "incrementcounter":
                {
                    var key = parameters.GetValueOrDefault("key", "counter");
                    var current = context.GetNumeric(key);
                    context.SetValue(key, current + 1);
                    return true;
                }

                default:
                    errorMessage = $"Unknown context command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Context command failed: {ex.Message}";
            return false;
        }
    }
}
