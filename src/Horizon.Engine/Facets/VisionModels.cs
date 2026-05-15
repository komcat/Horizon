namespace Horizon.Engine;

/// <summary>HALCON image statistics for one analyzed frame.</summary>
public sealed record VisionImageStats(
    int Width,
    int Height,
    int Channels,
    double MinGray,
    double MeanGray,
    double MaxGray);

/// <summary>Result of a threshold + connected-components blob count.</summary>
public sealed record VisionBlobResult(
    int BlobCount,
    double TotalArea,
    int ThresholdMin,
    int ThresholdMax);
