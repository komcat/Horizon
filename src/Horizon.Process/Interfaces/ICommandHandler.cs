namespace Horizon.Process.Interfaces;

public interface ICommandHandler
{
    bool ExecuteCommand(
        object? manager,
        string deviceName,
        string methodName,
        Dictionary<string, string> parameters,
        out string errorMessage,
        ProcessContext context);

    string ManagerType { get; }
}
