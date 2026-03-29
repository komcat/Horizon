namespace Horizon.Camera.Models;

public record ExposureSettings
{
    public double ExposureTimeUs { get; init; } = 10000.0;
    public double Gain { get; init; } = 0.0;
    public bool AutoExposure { get; init; }
    public bool AutoGain { get; init; }
    public double FrameRate { get; init; } = 10.0;
}
