namespace Horizon.Tec.Models;

public enum TecError
{
    Success,
    DeviceNotFound,
    Disconnected,
    OperationFailed,
    CommunicationError,
    ParseError
}

/// <summary>Status snapshot of a standalone TEC controller.</summary>
public record TecStatus
{
    public bool Connected { get; init; }
    public double CurrentTemperature { get; init; }
    public double SetpointTemperature { get; init; }
    public bool TecOn { get; init; }
    public string LastError { get; init; } = string.Empty;
}
