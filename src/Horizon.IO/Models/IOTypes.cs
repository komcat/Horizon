namespace Horizon.IO.Models;

public enum IOError
{
    Success,
    DeviceNotFound,
    Disconnected,
    InvalidPin,
    OperationFailed,
    NotInitialized
}

/// <summary>
/// "IOModule" = standalone Ezi-IO expansion module (uses FAS_GetInput/FAS_SetOutput).
/// "MotorIO" = EziMOTION Plus-E motor controller built-in IO (uses FAS_GetIOInput/FAS_SetIOOutput).
/// </summary>
public enum EziDeviceType
{
    IOModule,
    MotorIO
}

public record EziIODeviceConfig
{
    public int DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int InputCount { get; init; }
    public int OutputCount { get; init; }
    public bool Enabled { get; init; } = true;
    public EziDeviceType DeviceType { get; init; } = EziDeviceType.IOModule;
}

public record IOPinConfig
{
    public string Name { get; init; } = string.Empty;
    public int Pin { get; init; }
}

public record IODeviceConfig
{
    public EziIODeviceConfig Device { get; init; } = new();
    public List<IOPinConfig> InputPins { get; init; } = new();
    public List<IOPinConfig> OutputPins { get; init; } = new();
}

public record IOStatus
{
    public uint Inputs { get; init; }
    public uint Latch { get; init; }
    public uint Outputs { get; init; }
    public bool Valid { get; init; }
}
