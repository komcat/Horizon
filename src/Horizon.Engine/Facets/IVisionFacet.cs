using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Vision domain of the engine — HALCON image processing over live camera
/// frames. Minimal bridge: proves HALCON runs headless and exposes basic
/// image analysis. Exposes engine-owned types only; HALCON handles stay inside.
/// </summary>
public interface IVisionFacet
{
    /// <summary>True if the HALCON runtime loaded and is licensed.</summary>
    bool HalconAvailable { get; }

    /// <summary>HALCON version string, or an error note if unavailable.</summary>
    string HalconVersion { get; }

    /// <summary>Whether a frame has been received from a camera (it must be grabbing).</summary>
    bool HasFrame(string cameraId);

    /// <summary>Runs HALCON image statistics on the latest frame from a camera.</summary>
    Task<Result<VisionImageStats>> AnalyzeFrameAsync(string cameraId);

    /// <summary>Threshold + connected-components blob count on the latest frame.</summary>
    Task<Result<VisionBlobResult>> CountBlobsAsync(string cameraId, int thresholdMin, int thresholdMax);
}
