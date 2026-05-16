namespace Horizon.PowerSupply.Models;

public enum PowerSupplyError
{
    Success,
    DeviceNotFound,
    Disconnected,
    InvalidChannel,
    OperationFailed,
    CommunicationError,
    ParseError
}

/// <summary>Live readings for one power-supply output channel.</summary>
public record PowerSupplyChannelStatus
{
    public int Channel { get; init; }
    public double Voltage { get; init; }
    public double Current { get; init; }
    public bool OutputOn { get; init; }
}

/// <summary>Status snapshot of a power supply.</summary>
public record PowerSupplyStatus
{
    public bool Connected { get; init; }
    public List<PowerSupplyChannelStatus> Channels { get; init; } = new();
    public string LastError { get; init; } = string.Empty;
}
