namespace Horizon.Smu.Models;

public enum SmuError
{
    Success,
    DeviceNotFound,
    Disconnected,
    OperationFailed,
    CommunicationError,
    ParseError
}

/// <summary>Which quantity the SMU is sourcing.</summary>
public enum SmuSourceMode
{
    Voltage,
    Current
}

/// <summary>Status snapshot of a source-measure unit.</summary>
public record SmuStatus
{
    public bool Connected { get; init; }
    public bool OutputOn { get; init; }
    public SmuSourceMode SourceMode { get; init; }
    public double Voltage { get; init; }
    public double Current { get; init; }
    public string LastError { get; init; } = string.Empty;
}
