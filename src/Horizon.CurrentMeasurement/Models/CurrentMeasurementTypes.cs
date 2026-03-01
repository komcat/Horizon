namespace Horizon.CurrentMeasurement.Models;

public enum CurrentMeasurementError
{
    Success,
    DeviceNotFound,
    Disconnected,
    InvalidChannel,
    OperationFailed,
    CommunicationError,
    ParseError
}

public record CurrentMeasurementDeviceConfig
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string VisaResource { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int ChannelCount { get; init; } = 2;
    public List<int> EnabledChannels { get; init; } = new();
}

public record CurrentMeasurementStatus
{
    public double[] ChannelReadings { get; init; } = Array.Empty<double>();
    public string Unit { get; init; } = "A";
    public bool Valid { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
