using System.Text.Json.Serialization;

namespace Horizon.Core;

public record CalibrationPosition
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }
}

public record NeedleCameraCalibrationConfig
{
    [JsonPropertyName("motionDevice")]
    public string MotionDevice { get; init; } = string.Empty;

    [JsonPropertyName("pneumaticSlide")]
    public string PneumaticSlide { get; init; } = string.Empty;

    [JsonPropertyName("estimateOffsetX")]
    public double EstimateOffsetX { get; init; }

    [JsonPropertyName("estimateOffsetY")]
    public double EstimateOffsetY { get; init; }
}

public record NeedleCameraCalibration
{
    [JsonPropertyName("config")]
    public NeedleCameraCalibrationConfig Config { get; init; } = new();

    [JsonPropertyName("cameraPosition")]
    public CalibrationPosition? CameraPosition { get; init; }

    [JsonPropertyName("needleXYPosition")]
    public CalibrationPosition? NeedleXYPosition { get; init; }

    [JsonPropertyName("needleZPosition")]
    public CalibrationPosition? NeedleZPosition { get; init; }

    [JsonPropertyName("markPosition")]
    public CalibrationPosition? MarkPosition { get; init; }

    [JsonPropertyName("offsetX")]
    public double OffsetX { get; init; }

    [JsonPropertyName("offsetY")]
    public double OffsetY { get; init; }

    [JsonPropertyName("offsetZ")]
    public double OffsetZ { get; init; }

    [JsonPropertyName("startingZ")]
    public double StartingZ { get; init; }

    [JsonPropertyName("calibratedAt")]
    public string CalibratedAt { get; init; } = string.Empty;

    [JsonPropertyName("valid")]
    public bool Valid { get; init; }
}
