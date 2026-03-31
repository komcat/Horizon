using System.Text.Json.Serialization;

namespace Horizon.Core.Dispensing;

public record DispensePosition
{
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("z")] public double Z { get; init; }
}

public record DispenseProfile
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("motionDevice")] public string MotionDevice { get; init; } = "gantry-main";
    [JsonPropertyName("pneumaticSlide")] public string PneumaticSlide { get; init; } = string.Empty;

    // Positions
    [JsonPropertyName("cameraDispensePosition")] public DispensePosition? CameraDispensePosition { get; init; }
    [JsonPropertyName("dispensePosition")] public DispensePosition? DispensePosition { get; init; }
    [JsonPropertyName("dispenseZHeight")] public double DispenseZHeight { get; init; }

    // Motion parameters
    [JsonPropertyName("velocity")] public double Velocity { get; init; } = 20.0;
    [JsonPropertyName("safetyHeight")] public double SafetyHeight { get; init; } = 5.0;
    [JsonPropertyName("safetySpeed")] public double SafetySpeed { get; init; } = 0.5;

    // Timing
    [JsonPropertyName("delayBeforeMs")] public int DelayBeforeMs { get; init; } = 300;
    [JsonPropertyName("delayAfterMs")] public int DelayAfterMs { get; init; } = 300;
    [JsonPropertyName("dispenseDurationMs")] public int DispenseDurationMs { get; init; } = 300;

    // Lift off
    [JsonPropertyName("liftOffHeight")] public double LiftOffHeight { get; init; } = 3.0;
    [JsonPropertyName("liftOffSpeed")] public double LiftOffSpeed { get; init; } = 1.0;

    // Options
    [JsonPropertyName("skipCameraVerification")] public bool SkipCameraVerification { get; init; }
    [JsonPropertyName("automaticMode")] public bool AutomaticMode { get; init; }
    [JsonPropertyName("reteachZ")] public bool ReteachZ { get; init; }

    // State
    [JsonPropertyName("taught")] public bool Taught { get; init; }
    [JsonPropertyName("taughtAt")] public string TaughtAt { get; init; } = string.Empty;
}
