using Horizon.Process.Interfaces;

namespace Horizon.Process.Handlers;

public sealed class SystemCommandHandler : ICommandHandler
{
    public string ManagerType => "system";

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
                case "wait":
                {
                    var msStr = parameters.GetValueOrDefault("ms", "0");
                    if (!int.TryParse(msStr, out var ms) || ms <= 0)
                    {
                        errorMessage = "Invalid wait time";
                        return false;
                    }
                    // Respect cancellation during wait
                    if (context.CancellationToken.WaitHandle.WaitOne(ms))
                        return false; // Cancelled
                    return true;
                }

                case "log":
                {
                    var message = parameters.GetValueOrDefault("message", "");
                    // Resolve context references in message
                    if (message.Contains("{") && message.Contains("}"))
                    {
                        foreach (var key in context.GetAllKeys())
                        {
                            message = message.Replace($"{{{key}}}", context.GetString(key));
                        }
                    }
                    // Log will be captured by OnStepStarted callback
                    return true;
                }

                default:
                    errorMessage = $"Unknown system command: {methodName}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"System command failed: {ex.Message}";
            return false;
        }
    }
}
