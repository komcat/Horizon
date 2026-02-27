namespace Horizon.Motion.Models;

public record PositionStruct
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double U { get; init; }
    public double V { get; init; }
    public double W { get; init; }
    public List<string> Waypoints { get; init; } = new();
    public List<string> ExitWaypoints { get; init; } = new();
}

public record MotionDevice
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; } = 701;
    public string ControllerType { get; init; } = string.Empty; // "ACS" or "PI"
    public List<string> InstalledAxes { get; init; } = new();
    public bool Enabled { get; init; } = true;
    public Dictionary<string, PositionStruct> Positions { get; init; } = new();
}

public enum MotionError
{
    Success,
    DeviceNotFound,
    AxisError,
    HomingFailed,
    LimitReached,
    PositionNotFound,
    CommunicationError,
    Timeout,
    NotConnected,
    AlreadyMoving
}

public record MotionStatus
{
    public string DeviceName { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public bool Moving { get; init; }
    public PositionStruct CurrentPosition { get; init; } = new();
}
