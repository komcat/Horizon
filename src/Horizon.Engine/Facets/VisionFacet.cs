using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HalconDotNet;
using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Vision facet — minimal HALCON bridge. Caches the latest camera frame from
/// <see cref="ICameraFacet.FrameReady"/>, converts it into a HALCON image, and
/// runs basic analysis. HALCON handles never leave this class. Degrades
/// gracefully when the HALCON runtime / license is absent.
/// </summary>
internal sealed class VisionFacet : IVisionFacet, IDisposable
{
    private readonly ICameraFacet _camera;
    private readonly Action<LogLevel, string> _log;
    private readonly SemaphoreSlim _halconLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CameraFrame> _frames = new(StringComparer.Ordinal);

    public bool HalconAvailable { get; }
    public string HalconVersion { get; }

    public VisionFacet(ICameraFacet camera, Action<LogLevel, string> log)
    {
        _camera = camera;
        _log = log;
        _camera.FrameReady += OnFrame;

        try
        {
            HOperatorSet.GetSystem("version", out HTuple version);
            HalconVersion = version.S;
            HalconAvailable = true;
            _log(LogLevel.Info, $"Vision: HALCON {HalconVersion} available.");
        }
        catch (Exception ex)
        {
            HalconAvailable = false;
            HalconVersion = $"unavailable ({ex.GetType().Name})";
            _log(LogLevel.Warning, $"Vision: HALCON unavailable — {ex.Message}");
        }
    }

    public bool HasFrame(string cameraId) => _frames.ContainsKey(cameraId);

    public Task<Result<VisionImageStats>> AnalyzeFrameAsync(string cameraId)
        => RunAsync<VisionImageStats>(cameraId, frame =>
        {
            using var image = ToHImage(frame);
            using var gray = ToGray(image, frame.Channels);
            using var domain = gray.GetDomain();
            gray.MinMaxGray(domain, 0, out HTuple min, out HTuple max, out _);
            double mean = gray.Intensity(domain, out double _);
            return Result<VisionImageStats>.Ok(new VisionImageStats(
                frame.Width, frame.Height, frame.Channels, min.D, mean, max.D));
        });

    public Task<Result<VisionBlobResult>> CountBlobsAsync(string cameraId, int thresholdMin, int thresholdMax)
        => RunAsync<VisionBlobResult>(cameraId, frame =>
        {
            using var image = ToHImage(frame);
            using var gray = ToGray(image, frame.Channels);
            using var region = gray.Threshold((double)thresholdMin, (double)thresholdMax);
            using var connected = region.Connection();
            int count = connected.CountObj();
            double area = count > 0 ? connected.Area.TupleSum().D : 0.0;
            return Result<VisionBlobResult>.Ok(new VisionBlobResult(count, area, thresholdMin, thresholdMax));
        });

    public void Dispose()
    {
        _camera.FrameReady -= OnFrame;
        _halconLock.Dispose();
    }

    // --- internals ---

    private void OnFrame(CameraFrame frame) => _frames[frame.Camera] = frame;

    private async Task<Result<T>> RunAsync<T>(string cameraId, Func<CameraFrame, Result<T>> op)
    {
        if (!HalconAvailable)
            return Result<T>.Fail(ErrorCode.NotSupported, "HALCON is not available.");
        if (!_frames.TryGetValue(cameraId, out var frame) || !frame.Data.Any())
            return Result<T>.Fail(ErrorCode.OperationFailed,
                $"No frame from camera '{cameraId}' — start grabbing first.");

        await _halconLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return op(frame); }
                catch (Exception ex)
                {
                    return Result<T>.Fail(ErrorCode.OperationFailed, $"HALCON error: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _halconLock.Release();
        }
    }

    /// <summary>Pins the raw frame buffer and builds a HALCON image (HALCON copies the data in).</summary>
    private static HImage ToHImage(CameraFrame frame)
    {
        var handle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
        try
        {
            var ptr = new HTuple(handle.AddrOfPinnedObject());
            var image = new HImage();
            if (frame.Channels >= 3)
                image.GenImageInterleaved(ptr, "rgb", frame.Width, frame.Height,
                    -1, "byte", frame.Width, frame.Height, 0, 0, -1, 0);
            else
                image.GenImage1("byte", frame.Width, frame.Height, ptr);
            return image;
        }
        finally
        {
            handle.Free();
        }
    }

    private static HImage ToGray(HImage image, int channels)
        => channels >= 3 ? image.Rgb1ToGray() : image.CopyImage();
}
