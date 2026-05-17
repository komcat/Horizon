namespace Horizon.Engine;

/// <summary>Static description of a source-measure unit.</summary>
public sealed record SmuDeviceInfo(string Name, string ControllerType);

/// <summary>
/// Cached state of a source-measure unit. <see cref="Valid"/> is false until
/// the first poll. <see cref="SourceMode"/> is "voltage" or "current".
/// </summary>
public sealed record SmuState(
    bool Valid,
    bool Connected,
    bool OutputOn,
    string SourceMode,
    double Voltage,
    double Current,
    string LastError)
{
    public static readonly SmuState Unknown =
        new(false, false, false, "voltage", 0, 0, string.Empty);
}

/// <summary>Raised on <see cref="ISmuFacet.StateChanged"/> when a unit's polled state changes.</summary>
public sealed record SmuStateChanged(string Device, SmuState State);

/// <summary>A single on-demand measurement from one SMU channel (1-based).</summary>
public sealed record SmuReading(int Channel, double Current, double Voltage, DateTime Timestamp);
