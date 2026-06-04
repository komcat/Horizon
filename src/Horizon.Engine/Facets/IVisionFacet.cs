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

    /// <summary>
    /// Applies an ordered chain of pre-process filters to a frame — restricted to
    /// <paramref name="searchRoi"/> first (null = whole frame) — and returns the
    /// resulting grayscale image bytes (channels = 1), for a preview pane.
    /// </summary>
    Task<Result<NccCrop>> ApplyFilterAsync(
        CameraFrame frame, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi);

    /// <summary>
    /// Creates an NCC model from the ROI of a frame (after the given pre-process
    /// filter chain) and persists it: the model to <paramref name="modelFilePath"/>
    /// (HALCON <c>write_ncc_model</c>) and the cropped template image to
    /// <paramref name="templatePngPath"/>. The anchor defaults to the ROI centre.
    /// </summary>
    Task<Result<NccTemplateInfo>> CreateNccModelAsync(
        CameraFrame frame, RoiSpec roi, IReadOnlyList<VisionFilter> filters,
        string modelFilePath, string templatePngPath, double angleRangeDeg);

    /// <summary>
    /// Finds the NCC model (read from <paramref name="modelFilePath"/>) in a frame
    /// after the given pre-process filter chain, restricted to <paramref name="searchRoi"/>
    /// (null = whole frame), and returns the match whose centre is closest to the search
    /// centre, with a crop around it (side = <paramref name="cropSize"/> px). Fails if no
    /// match reaches <paramref name="minScore"/>.
    /// </summary>
    Task<Result<NccFindResult>> FindNccModelAsync(
        CameraFrame frame, string modelFilePath, IReadOnlyList<VisionFilter> filters,
        double minScore, int cropSize, RoiSpec? searchRoi, double angleRangeDeg);

    /// <summary>Cached-frame overload of NCC find — for the <c>vision.runProfile</c> step.</summary>
    Task<Result<NccFindResult>> FindNccModelAsync(
        string cameraId, string modelFilePath, IReadOnlyList<VisionFilter> filters,
        double minScore, int cropSize, RoiSpec? searchRoi, double angleRangeDeg);

    /// <summary>
    /// Detects a circle in a frame after the filter chain, within <paramref name="searchRoi"/>
    /// (null = whole frame). Returns the qualifying circle closest to the search centre
    /// (<c>x</c>, <c>y</c>, <c>radius</c>, <c>score</c>) with a crop, or not-found.
    /// </summary>
    Task<Result<VisionDetectResult>> FindCircleAsync(
        CameraFrame frame, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, CircleParams p);

    /// <summary>Cached-frame overload of circle detect.</summary>
    Task<Result<VisionDetectResult>> FindCircleAsync(
        string cameraId, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, CircleParams p);

    /// <summary>
    /// Detects the dominant line/edge in a frame after the filter chain, within
    /// <paramref name="searchRoi"/> (null = whole frame). Returns its endpoints,
    /// centre, angle and length, with a crop, or not-found.
    /// </summary>
    Task<Result<VisionDetectResult>> FindLineAsync(
        CameraFrame frame, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, LineParams p);

    /// <summary>Cached-frame overload of line detect.</summary>
    Task<Result<VisionDetectResult>> FindLineAsync(
        string cameraId, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, LineParams p);
}
