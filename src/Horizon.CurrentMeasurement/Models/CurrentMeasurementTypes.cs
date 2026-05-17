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

    /// <summary>Number of measurement channels (Keithley 6482 = 2).</summary>
    public int ChannelCount { get; init; } = 2;

    /// <summary>Channels (1-based) to surface. Empty = all 1..<see cref="ChannelCount"/>.</summary>
    public List<int> EnabledChannels { get; init; } = new();

    /// <summary>Integration time in power-line cycles (0.01–10). Higher = slower, lower noise.</summary>
    public double MeasurementNplc { get; init; } = 1.0;

    /// <summary>When true the instrument auto-selects the current range.</summary>
    public bool AutoRange { get; init; } = true;

    /// <summary>Fixed current range (A) used when <see cref="AutoRange"/> is false. Ignored if ≤ 0.</summary>
    public double FixedRange { get; init; } = 0.0;
}

public record CurrentMeasurementStatus
{
    public double[] ChannelReadings { get; init; } = Array.Empty<double>();
    public string Unit { get; init; } = "A";
    public bool Valid { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
