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

/// <summary>State of one lighting channel — read back from the controller when it supports it.</summary>
public record LightingChannelStatus
{
    /// <summary>Front-panel channel number (L1 = 1).</summary>
    public int Channel { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Intensity setpoint in controller-native units.</summary>
    public int Intensity { get; init; }
    /// <summary>Whether the channel output is switched on.</summary>
    public bool On { get; init; }
    /// <summary>Controller lighting-mode code (PD3: "00" continuous, "01".."10" strobe); empty if unknown.</summary>
    public string Mode { get; init; } = string.Empty;
}

/// <summary>Status snapshot of a lighting controller.</summary>
public record LightingStatus
{
    public bool Connected { get; init; }
    public List<LightingChannelStatus> Channels { get; init; } = new();
    public string LastError { get; init; } = string.Empty;
}
