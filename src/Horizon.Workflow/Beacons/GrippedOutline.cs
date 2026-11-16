using System.Text.Json;
using System.Text.Json.Serialization;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// A black / white snapshot of what the camera saw around its centre, kept with a beacon so the
/// live views can paint the white pixels (yellow) where that thing is now. <see cref="Mask"/> is
/// one byte per source pixel, row by row (non-zero = white); (<see cref="CentreX"/>,
/// <see cref="CentreY"/>) is the snapshot's centre in the camera device's frame when it was taken.
/// </summary>
public sealed record BeaconOutline(int Width, int Height, byte[] Mask, double CentreX, double CentreY);

/// <summary>
/// What <c>object.register</c> snapshots before it plants the marker: a
/// <see cref="RoiWidth"/> × <see cref="RoiHeight"/> pixel box centred on the crosshair, run through
/// <see cref="Filters"/> (binary threshold / canny, as in the Vision helper).
/// </summary>
public sealed record GrippedOutlineOptions(int RoiWidth, int RoiHeight, IReadOnlyList<VisionFilter> Filters)
{
    public const int DefaultSize = 300;
    public const int MinSize = 16;
    public const int MaxSize = 2000;

    /// <summary>The box for a frame of the given size: centred, clamped to the size limits and to the frame.</summary>
    public RoiSpec RoiFor(int frameWidth, int frameHeight)
    {
        int w = Math.Min(Math.Clamp(RoiWidth, MinSize, MaxSize), Math.Max(1, frameWidth));
        int h = Math.Min(Math.Clamp(RoiHeight, MinSize, MaxSize), Math.Max(1, frameHeight));
        return new RoiSpec((frameWidth - w) / 2, (frameHeight - h) / 2, w, h);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The filter chain as the compact JSON kept in the block's <c>filters</c> parameter.</summary>
    public static string SerializeFilters(IReadOnlyList<VisionFilter> filters) => JsonSerializer.Serialize(filters, Json);

    /// <summary>Parses a block's <c>filters</c> parameter; empty when blank or unreadable. "None" stages are dropped.</summary>
    public static IReadOnlyList<VisionFilter> ParseFilters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<VisionFilter>();
        try
        {
            var list = JsonSerializer.Deserialize<List<VisionFilter>>(text, Json) ?? new List<VisionFilter>();
            return list.Where(f => f != null && f.Kind != VisionFilterKind.None).ToArray();
        }
        catch (JsonException) { return Array.Empty<VisionFilter>(); }
    }
}

/// <summary>Takes the outline snapshot for a gripped-object marker.</summary>
internal static class GrippedOutlineCapture
{
    private const int FrameWaitMs = 2000;

    /// <summary>
    /// Grabs a fresh frame from <paramref name="cameraId"/>, filters the centred box and returns
    /// its white pixels as a mask anchored at (<paramref name="centreX"/>, <paramref name="centreY"/>)
    /// — the camera device's position, i.e. where the crosshair is. Holds its own grab lease.
    /// </summary>
    public static async Task<Result<BeaconOutline>> CaptureAsync(
        IHorizonEngine engine, string cameraId, GrippedOutlineOptions options, double centreX, double centreY)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || !engine.Camera.IsConnected(cameraId))
            return Result<BeaconOutline>.Fail(ErrorCode.OperationFailed, $"camera '{cameraId}' is not connected.");

        var tcs = new TaskCompletionSource<CameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0;
        void OnFrame(CameraFrame f)
        {
            if (!string.Equals(f.Camera, cameraId, StringComparison.OrdinalIgnoreCase)) return;
            // Skip the first frame: it may have been exposed before the grab was (re)started.
            if (Interlocked.Increment(ref seen) >= 2) tcs.TrySetResult(f);
        }

        engine.Camera.FrameReady += OnFrame;
        var lease = await engine.Camera.StartGrabbingAsync(cameraId).ConfigureAwait(false);
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(FrameWaitMs)).ConfigureAwait(false);
            if (done != tcs.Task)
                return Result<BeaconOutline>.Fail(ErrorCode.OperationFailed, $"no frame from camera '{cameraId}'.");
            var frame = await tcs.Task.ConfigureAwait(false);

            var roi = options.RoiFor(frame.Width, frame.Height);
            var filtered = await engine.Vision.ApplyFilterAsync(frame, options.Filters, roi).ConfigureAwait(false);
            if (!filtered.IsSuccess)
                return Result<BeaconOutline>.Fail(filtered.Error, filtered.Message);
            if (ToMask(filtered.Value, roi) is not { } mask)
                return Result<BeaconOutline>.Fail(ErrorCode.OperationFailed, "the filtered image is not a single-channel image of the frame.");
            return Result<BeaconOutline>.Ok(new BeaconOutline(roi.Width, roi.Height, mask, centreX, centreY));
        }
        finally
        {
            engine.Camera.FrameReady -= OnFrame;
            if (lease.IsSuccess)
                await engine.Camera.StopGrabbingAsync(cameraId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cuts the box out of a filter result (a full-frame grayscale image, or one already cropped to
    /// the box) and thresholds it: 255 where the pixel is white (≥ 128), else 0. Null when the
    /// image does not fit either shape.
    /// </summary>
    public static byte[]? ToMask(NccCrop image, RoiSpec roi)
    {
        if (image.Channels != 1 || image.Width <= 0 || image.Height <= 0) return null;
        if (image.Data.Length < image.Width * image.Height) return null;

        // Where the box starts inside the returned image.
        int x0 = roi.X - image.OriginX, y0 = roi.Y - image.OriginY;
        if (x0 < 0 || y0 < 0 || x0 + roi.Width > image.Width || y0 + roi.Height > image.Height) return null;

        var mask = new byte[roi.Width * roi.Height];
        for (int y = 0; y < roi.Height; y++)
        {
            int src = (y0 + y) * image.Width + x0, dst = y * roi.Width;
            for (int x = 0; x < roi.Width; x++)
                mask[dst + x] = image.Data[src + x] >= 128 ? (byte)255 : (byte)0;
        }
        return mask;
    }
}
