namespace Horizon.LaserController.Models;

public enum LaserControllerError
{
    Success,
    DeviceNotFound,
    Disconnected,
    OperationFailed,
    CommunicationError,
    ParseError
}

public record LaserControllerDeviceConfig
{
    public string Name { get; init; } = string.Empty;
    public string ControllerType { get; init; } = string.Empty;
    public string VisaResource { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public double DefaultTemperature { get; init; } = 25.0;
    public double DefaultLaserCurrent { get; init; } = 50.0;
}

public record LaserControllerStatus
{
    public bool Connected { get; init; }
    public bool TECOn { get; init; }
    public bool LaserOn { get; init; }
    public double CurrentTemperature { get; init; }
    public double SetpointTemperature { get; init; }
    public double CurrentLaserCurrent { get; init; }
    public double SetpointLaserCurrent { get; init; }
    public string LastError { get; init; } = string.Empty;
}
