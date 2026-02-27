using Horizon.Process.Interfaces;

namespace Horizon.Process;

public sealed class CommandDispatcher
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterHandler(ICommandHandler handler)
    {
        _handlers[handler.ManagerType] = handler;
    }

    public bool Dispatch(
        string managerType,
        object? manager,
        string device,
        string method,
        Dictionary<string, string> parms,
        out string error,
        ProcessContext context)
    {
        if (!_handlers.TryGetValue(managerType, out var handler))
        {
            error = $"No handler registered for manager type '{managerType}'";
            return false;
        }

        return handler.ExecuteCommand(manager, device, method, parms, out error, context);
    }
}
