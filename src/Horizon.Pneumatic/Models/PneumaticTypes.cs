namespace Horizon.Pneumatic.Models;

public enum PneumaticPosition
{
    Extended,
    Retracted,
    Extending,
    Retracting,
    Unknown,
    HardwareError,
    Timeout
}

public enum PneumaticError
{
    Success,
    SlideNotFound,
    IOManagerNull,
    DeviceNotFound,
    PinNotFound,
    Timeout,
    AlreadyAtPosition,
    HardwareError,
    OperationFailed
}

public record IOPinReference
{
    public string DeviceName { get; init; } = string.Empty;
    public string PinName { get; init; } = string.Empty;
}

public record PneumaticConfig
{
    public string Name { get; init; } = string.Empty;
    public IOPinReference Output { get; init; } = new();
    public IOPinReference ExtendedSensor { get; init; } = new();
    public IOPinReference RetractedSensor { get; init; } = new();
    public int TimeoutMs { get; init; } = 5000;
}

public record PneumaticStatus
{
    public PneumaticPosition Position { get; init; }
    public bool OutputActive { get; init; }
    public bool ExtendedSensorActive { get; init; }
    public bool RetractedSensorActive { get; init; }
    public long LastMoveTimeMs { get; init; }
    public bool Valid { get; init; }
}
