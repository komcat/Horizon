namespace Horizon.Lighting.Models;

public enum LightingError
{
    Success,
    DeviceNotFound,
    Disconnected,
    InvalidChannel,
    OperationFailed,
    CommunicationError
}

/// <summary>Last-commanded state of one lighting channel.</summary>
public record LightingChannelStatus
{
    public int Channel { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Intensity { get; init; }
}

/// <summary>Status snapshot of a lighting controller.</summary>
public record LightingStatus
{
    public bool Connected { get; init; }
    public List<LightingChannelStatus> Channels { get; init; } = new();
    public string LastError { get; init; } = string.Empty;
}
