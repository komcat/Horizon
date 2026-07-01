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
    private readonly ConcurrentDictionary<string, CameraFrame> _frames = new(StringComparer.Ordinal);

    // All HALCON work runs on this one dedicated thread. Using Task.Run instead would
    // spread ops across thread-pool threads, and HALCON keeps a per-thread temporary
    // memory cache that is never reclaimed — so memory would climb with every detect.
    private readonly BlockingCollection<Action> _halconWork = new();
    private readonly Thread _halconThread;

    public bool HalconAvailable { get; }
    public string HalconVersion { get; }

    public VisionFacet(ICameraFacet camera, Action<LogLevel, string> log)
    {
        _camera = camera;
        _log = log;
        _camera.FrameReady += OnFrame;

        _halconThread = new Thread(HalconLoop) { IsBackground = true, Name = "Horizon.Vision.HALCON" };
        _halconThread.Start();

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

    /// <summary>Pumps queued HALCON work items on the single dedicated thread until shutdown.</summary>
    private void HalconLoop()
    {
        foreach (var work in _halconWork.GetConsumingEnumerable())
        {
            try { work(); }
            catch { /* each item already guards itself; never let one kill the pump */ }
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

    public Task<Result<NccCrop>> ApplyFilterAsync(
        CameraFrame frame, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi)
        => RunFrameAsync<NccCrop>(frame, f =>
        {
            using var image = ToHImage(f);
            using var gray = ToGray(image, f.Channels);
            // ROI first, then filters — so the foreground only exists inside the ROI.
            using var reduced = ReduceToRoi(gray, searchRoi, f.Width, f.Height);
            using var processed = ApplyFilterChain(reduced, filters, f.Width, f.Height);
            return Result<NccCrop>.Ok(GrayBytes(processed));
        });

    public Task<Result<NccTemplateInfo>> CreateNccModelAsync(
        CameraFrame frame, RoiSpec roi, IReadOnlyList<VisionFilter> filters,
        string modelFilePath, string templatePngPath, double angleRangeDeg)
        => RunFrameAsync<NccTemplateInfo>(frame, f =>
        {
            var clamped = ClampRoi(roi, f.Width, f.Height);
            if (clamped.Width < 2 || clamped.Height < 2)
                return Result<NccTemplateInfo>.Fail(ErrorCode.OperationFailed, "ROI is too small.");

            using var image = ToHImage(f);
            using var gray = ToGray(image, f.Channels);
            using var processed = ApplyFilterChain(gray, filters, f.Width, f.Height);
            using var template = processed.CropRectangle1(
                clamped.Y, clamped.X, clamped.Y + clamped.Height - 1, clamped.X + clamped.Width - 1);

            // Build the NCC model with a rotation search range (0 = translation only), so
            // find_ncc_model returns the matched instance's angle.
            double range = Math.Clamp(angleRangeDeg, 0, 180) * Math.PI / 180.0;
            HOperatorSet.CreateNccModel(template, "auto", -range, 2 * range, "auto", "use_polarity", out HTuple modelId);
            try
            {
                HOperatorSet.WriteNccModel(modelId, modelFilePath);
            }
            finally
            {
                HOperatorSet.ClearNccModel(modelId);
            }

            // Save the cropped template image alongside the model (HALCON appends .png).
            using var crop = gray.CropRectangle1(
                clamped.Y, clamped.X, clamped.Y + clamped.Height - 1, clamped.X + clamped.Width - 1);
            string baseNoExt = templatePngPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? templatePngPath[..^4] : templatePngPath;
            HOperatorSet.WriteImage(crop, "png", 0, baseNoExt);

            return Result<NccTemplateInfo>.Ok(new NccTemplateInfo(
                clamped.Width, clamped.Height, clamped.Width / 2.0, clamped.Height / 2.0));
        });

    public Task<Result<NccFindResult>> FindNccModelAsync(
        CameraFrame frame, string modelFilePath, IReadOnlyList<VisionFilter> filters,
        double minScore, int cropSize, RoiSpec? searchRoi, double angleRangeDeg)
        => RunFrameAsync<NccFindResult>(frame, f =>
        {
            if (!File.Exists(modelFilePath))
                return Result<NccFindResult>.Fail(ErrorCode.OperationFailed,
                    $"NCC model file not found: {modelFilePath}");

            using var image = ToHImage(f);
            using var gray = ToGray(image, f.Channels);
            using var search = ReduceFilterReduce(gray, searchRoi, filters, f.Width, f.Height);

            double range = Math.Clamp(angleRangeDeg, 0, 180) * Math.PI / 180.0;
            HOperatorSet.ReadNccModel(modelFilePath, out HTuple modelId);
            HTuple rows, cols, angles, scores;
            try
            {
                HOperatorSet.FindNccModel(search, modelId, -range, 2 * range, minScore, 0, 0.5, "true", 0,
                    out rows, out cols, out angles, out scores);
            }
            finally
            {
                HOperatorSet.ClearNccModel(modelId);
            }

            int n = scores?.Length ?? 0;
            if (n == 0)
                return Result<NccFindResult>.Fail(ErrorCode.OperationFailed,
                    $"No NCC match at or above score {minScore:0.00}.");

            // Pick the match whose centre is closest to the search centre.
            var (sx, sy) = RoiCenter(searchRoi, f.Width, f.Height);
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double dx = cols[i].D - sx, dy = rows[i].D - sy;
                double dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = i; }
            }

            double centerX = cols[best].D, centerY = rows[best].D;
            double angleDeg = angles[best].D * 180.0 / Math.PI;
            var match = new NccMatch(centerX, centerY, angleDeg, scores[best].D, centerX, centerY);
            return Result<NccFindResult>.Ok(new NccFindResult(match, CropAround(gray, centerX, centerY, cropSize, f.Width, f.Height)));
        });

    public Task<Result<NccFindResult>> FindNccModelAsync(
        string cameraId, string modelFilePath, IReadOnlyList<VisionFilter> filters,
        double minScore, int cropSize, RoiSpec? searchRoi, double angleRangeDeg)
        => WithCachedFrame(cameraId, f => FindNccModelAsync(f, modelFilePath, filters, minScore, cropSize, searchRoi, angleRangeDeg));

    public Task<Result<VisionDetectResult>> FindCircleAsync(
        CameraFrame frame, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, CircleParams p)
        => RunFrameAsync<VisionDetectResult>(frame, f =>
        {
            using var image = ToHImage(f);
            using var gray = ToGray(image, f.Channels);
            using var search = ReduceFilterReduce(gray, searchRoi, filters, f.Width, f.Height);

            // Foreground blobs (filters set the polarity), kept by circularity.
            using var region = search.Threshold(128.0, 255.0);
            using var conn = region.Connection();
            using var circ = conn.SelectShape("circularity", "and", p.MinCircularity, 1.0);
            HOperatorSet.SmallestCircle(circ, out HTuple rows, out HTuple cols, out HTuple radii);
            HOperatorSet.Circularity(circ, out HTuple circularities);

            int n = radii?.Length ?? 0;
            var (sx, sy) = RoiCenter(searchRoi, f.Width, f.Height);
            int best = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double rad = radii[i].D;
                if (rad < p.MinRadius || rad > p.MaxRadius) continue;
                double dx = cols[i].D - sx, dy = rows[i].D - sy;
                double dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            if (best < 0)
                return Result<VisionDetectResult>.Ok(new VisionDetectResult(false, Array.Empty<VisionResultValue>(), null));

            double cx = cols[best].D, cy = rows[best].D, r = radii[best].D;
            double score = best < (circularities?.Length ?? 0) ? circularities[best].D : 0.0;
            var values = new[]
            {
                new VisionResultValue("x", cx),
                new VisionResultValue("y", cy),
                new VisionResultValue("radius", r),
                new VisionResultValue("score", score),
            };
            return Result<VisionDetectResult>.Ok(new VisionDetectResult(
                true, values, CropAround(gray, cx, cy, (int)(r * 2 + 20), f.Width, f.Height)));
        });

    public Task<Result<VisionDetectResult>> FindCircleAsync(
        string cameraId, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, CircleParams p)
        => WithCachedFrame(cameraId, f => FindCircleAsync(f, filters, searchRoi, p));

    public Task<Result<VisionDetectResult>> FindLineAsync(
        CameraFrame frame, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, LineParams p)
        => RunFrameAsync<VisionDetectResult>(frame, f =>
        {
            using var image = ToHImage(f);
            using var gray = ToGray(image, f.Channels);
            using var search = ReduceFilterReduce(gray, searchRoi, filters, f.Width, f.Height);

            // Extract edge contours (sub-pixel Canny) and fit a straight line to the
            // longest one — i.e. the dominant straight EDGE, not the centre of a blob.
            var notFound = new VisionDetectResult(false, Array.Empty<VisionResultValue>(), null);
            HOperatorSet.EdgesSubPix(search, out HObject edges, "canny", 1.0, 20, 40);
            try
            {
                HOperatorSet.SelectShapeXld(edges, out HObject longEdges, "contlength", "and", p.MinLength, 1e9);
                try
                {
                    HOperatorSet.CountObj(longEdges, out HTuple count);
                    if (count.I == 0) return Result<VisionDetectResult>.Ok(notFound);

                    HOperatorSet.FitLineContourXld(longEdges, "tukey", -1, 0, 5, 2.0,
                        out HTuple rowBegin, out HTuple colBegin, out HTuple rowEnd, out HTuple colEnd,
                        out _, out _, out _);

                    int n = rowBegin?.Length ?? 0;
                    if (n == 0) return Result<VisionDetectResult>.Ok(notFound);

                    // Among edges matching direction + polarity, keep the one chosen by the
                    // scan order (first / last along the scan, or closest to the ROI centre).
                    var (rcx, rcy) = RoiCenter(searchRoi, f.Width, f.Height);
                    int best = -1;
                    double bestKey = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        double ex1 = colBegin[i].D, ey1 = rowBegin[i].D, ex2 = colEnd[i].D, ey2 = rowEnd[i].D;
                        double dx = ex2 - ex1, dy = ey2 - ey1;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < p.MinLength || !MatchesDirection(dx, dy, p.Direction)) continue;

                        bool vertical = Math.Abs(dx) < Math.Abs(dy);   // perpendicular scan is horizontal
                        double mx = (ex1 + ex2) / 2.0, my = (ey1 + ey2) / 2.0;

                        if (p.Polarity != LinePolarity.Any &&
                            SamplePolarity(gray, mx, my, vertical, f.Width, f.Height) != p.Polarity)
                            continue;

                        double pos = vertical ? mx : my;
                        double centre = vertical ? rcx : rcy;
                        double key = p.ScanOrder switch
                        {
                            LineScanOrder.Forward => pos,             // smallest position wins
                            LineScanOrder.Backward => -pos,           // largest position wins
                            _ => Math.Abs(pos - centre),              // closest to centre wins
                        };
                        if (key < bestKey) { bestKey = key; best = i; }
                    }
                    if (best < 0) return Result<VisionDetectResult>.Ok(notFound);

                    double x1 = colBegin[best].D, y1 = rowBegin[best].D;
                    double x2 = colEnd[best].D, y2 = rowEnd[best].D;
                    double cx = (x1 + x2) / 2.0, cy = (y1 + y2) / 2.0;
                    double bestLen = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                    // A line has no direction, and HALCON's endpoint order is arbitrary,
                    // so fold the orientation into Q1 [0,180): horizontal (rightmost) = 0,
                    // vertical = 90. This keeps near-vertical stable (91 and -89 both ~90).
                    double angle = Math.Atan2(-(y2 - y1), x2 - x1) * 180.0 / Math.PI;
                    angle = ((angle % 180.0) + 180.0) % 180.0;
                    var values = new[]
                    {
                        new VisionResultValue("x", cx),
                        new VisionResultValue("y", cy),
                        new VisionResultValue("x1", x1),
                        new VisionResultValue("y1", y1),
                        new VisionResultValue("x2", x2),
                        new VisionResultValue("y2", y2),
                        new VisionResultValue("angle", angle),
                        new VisionResultValue("length", bestLen),
                    };
                    return Result<VisionDetectResult>.Ok(new VisionDetectResult(
                        true, values, CropAround(gray, cx, cy, (int)(bestLen + 20), f.Width, f.Height)));
                }
                finally { longEdges.Dispose(); }
            }
            finally { edges.Dispose(); }
        });

    public Task<Result<VisionDetectResult>> FindLineAsync(
        string cameraId, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, LineParams p)
        => WithCachedFrame(cameraId, f => FindLineAsync(f, filters, searchRoi, p));

    public void Dispose()
    {
        _camera.FrameReady -= OnFrame;
        _halconWork.CompleteAdding();
        _halconThread.Join(TimeSpan.FromSeconds(2));
        _halconWork.Dispose();
    }

    // --- internals ---

    private void OnFrame(CameraFrame frame) => _frames[frame.Camera] = frame;

    private Task<Result<T>> RunAsync<T>(string cameraId, Func<CameraFrame, Result<T>> op)
    {
        if (!_frames.TryGetValue(cameraId, out var frame) || !frame.Data.Any())
            return Task.FromResult(Result<T>.Fail(ErrorCode.OperationFailed,
                $"No frame from camera '{cameraId}' — start grabbing first."));
        return RunFrameAsync(frame, op);
    }

    private Task<Result<T>> RunFrameAsync<T>(CameraFrame frame, Func<CameraFrame, Result<T>> op)
    {
        if (!HalconAvailable)
            return Task.FromResult(Result<T>.Fail(ErrorCode.NotSupported, "HALCON is not available."));
        if (frame.Data is null || frame.Data.Length == 0)
            return Task.FromResult(Result<T>.Fail(ErrorCode.OperationFailed, "Frame has no pixel data."));
        return QueueHalcon(() => op(frame));
    }

    /// <summary>Runs <paramref name="op"/> on the dedicated HALCON thread and awaits its result.</summary>
    private Task<Result<T>> QueueHalcon<T>(Func<Result<T>> op)
    {
        var tcs = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _halconWork.Add(() =>
            {
                try { tcs.SetResult(op()); }
                catch (Exception ex)
                {
                    tcs.SetResult(Result<T>.Fail(ErrorCode.OperationFailed, $"HALCON error: {ex.Message}"));
                }
            });
        }
        catch (Exception ex)   // queue completed (facet disposed)
        {
            tcs.SetResult(Result<T>.Fail(ErrorCode.OperationFailed, $"Vision is shutting down: {ex.Message}"));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Runs an ordered chain of pre-process filters, each consuming the previous
    /// one's output. Returns a new owned grayscale image (a copy of the input if the
    /// chain is empty). Intermediate images are disposed as the chain advances.
    /// </summary>
    private static HImage ApplyFilterChain(HImage gray, IReadOnlyList<VisionFilter>? filters, int width, int height)
    {
        HImage current = gray.CopyImage();
        if (filters == null) return current;
        foreach (var f in filters)
        {
            if (f.Kind == VisionFilterKind.None) continue;
            var next = ApplyOneFilter(current, f, width, height);
            current.Dispose();
            current = next;
        }
        return current;
    }

    /// <summary>Applies one filter, returning a new owned grayscale image.</summary>
    private static HImage ApplyOneFilter(HImage gray, VisionFilter f, int width, int height)
    {
        switch (f.Kind)
        {
            case VisionFilterKind.Gaussian:
                int mask = f.GaussMask >= 5 ? 5 : 3;
                return gray.GaussImage(mask);

            case VisionFilterKind.BinaryThreshold:
                using (var region = gray.Threshold((double)f.ThresholdMin, (double)f.ThresholdMax))
                    return region.RegionToBin(255, 0, width, height);

            case VisionFilterKind.CannyEdge:
            {
                // HALCON Canny: edge amplitude (NMS + hysteresis), converted to a byte
                // edge image so it can chain / feed NCC. Min/Max = low/high thresholds.
                double alpha = f.Alpha > 0 ? f.Alpha : 1.0;
                using var amp = gray.EdgesImage(out HImage dir, "canny", alpha, "nms",
                    (double)f.ThresholdMin, (double)f.ThresholdMax);
                dir.Dispose();
                return amp.ConvertImageType("byte");
            }

            default:
                return gray.CopyImage();
        }
    }

    /// <summary>Copies a single-channel HALCON image into a byte buffer, tagged with its source origin.</summary>
    private static NccCrop GrayBytes(HImage gray, int originX = 0, int originY = 0)
    {
        HOperatorSet.GetImagePointer1(gray, out HTuple pointer, out HTuple _, out HTuple width, out HTuple height);
        int w = width.I, h = height.I;
        var buf = new byte[(long)w * h];
        Marshal.Copy(pointer.IP, buf, 0, buf.Length);
        return new NccCrop(w, h, 1, buf, originX, originY);
    }

    /// <summary>Runs an op against a camera's latest cached frame (or fails if none).</summary>
    private Task<Result<T>> WithCachedFrame<T>(string cameraId, Func<CameraFrame, Task<Result<T>>> op)
    {
        if (!_frames.TryGetValue(cameraId, out var frame) || frame.Data is null || frame.Data.Length == 0)
            return Task.FromResult(Result<T>.Fail(ErrorCode.OperationFailed,
                $"No frame from camera '{cameraId}' — start grabbing first."));
        return op(frame);
    }

    /// <summary>
    /// ROI-first processing: reduces to the search ROI, applies the filter chain on it,
    /// then re-reduces — so filters only touch the ROI and detection stays inside it.
    /// </summary>
    private static HImage ReduceFilterReduce(
        HImage gray, RoiSpec? roi, IReadOnlyList<VisionFilter> filters, int width, int height)
    {
        using var reduced = ReduceToRoi(gray, roi, width, height);
        using var filtered = ApplyFilterChain(reduced, filters, width, height);
        return ReduceToRoi(filtered, roi, width, height);
    }

    /// <summary>Reduces an image's domain to a rectangular ROI (returns an owned copy if no ROI).</summary>
    private static HImage ReduceToRoi(HImage img, RoiSpec? roi, int width, int height)
    {
        if (roi == null) return img.CopyImage();
        var c = ClampRoi(roi, width, height);
        if (c.Width < 2 || c.Height < 2) return img.CopyImage();
        using var rect = new HRegion(
            (double)c.Y, (double)c.X, (double)(c.Y + c.Height - 1), (double)(c.X + c.Width - 1));
        return img.ReduceDomain(rect);
    }

    /// <summary>Whether a line segment (dx,dy) matches the expected orientation (split at 45°).</summary>
    private static bool MatchesDirection(double dx, double dy, LineDirection dir)
    {
        if (dir == LineDirection.Any) return true;
        bool horizontal = Math.Abs(dx) >= Math.Abs(dy);   // closer to horizontal than vertical
        return dir == LineDirection.Horizontal ? horizontal : !horizontal;
    }

    /// <summary>
    /// Classifies an edge's polarity by sampling the grayscale a few px to each side of its
    /// midpoint (left/right for a vertical edge, top/bottom for a horizontal one).
    /// </summary>
    private static LinePolarity SamplePolarity(HImage gray, double mx, double my, bool vertical, int width, int height)
    {
        const int delta = 6;
        double a, b;   // a = left/top side, b = right/bottom side
        if (vertical)
        {
            a = Grayval(gray, my, mx - delta, width, height);
            b = Grayval(gray, my, mx + delta, width, height);
        }
        else
        {
            a = Grayval(gray, my - delta, mx, width, height);
            b = Grayval(gray, my + delta, mx, width, height);
        }
        return b >= a ? LinePolarity.DarkToBright : LinePolarity.BrightToDark;
    }

    /// <summary>Reads one grayscale value (clamped into the image), 0 on failure.</summary>
    private static double Grayval(HImage gray, double row, double col, int width, int height)
    {
        int r = (int)Math.Clamp(Math.Round(row), 0, height - 1);
        int c = (int)Math.Clamp(Math.Round(col), 0, width - 1);
        try
        {
            HOperatorSet.GetGrayval(gray, r, c, out HTuple g);
            return g.Length > 0 ? g.D : 0.0;
        }
        catch { return 0.0; }
    }

    /// <summary>Centre (x,y) of a search ROI, or the frame centre when unset.</summary>
    private static (double X, double Y) RoiCenter(RoiSpec? roi, int width, int height)
        => roi == null
            ? (width / 2.0, height / 2.0)
            : (roi.X + roi.Width / 2.0, roi.Y + roi.Height / 2.0);

    /// <summary>Crops a square window of side <paramref name="size"/> around (cx,cy).</summary>
    private static NccCrop CropAround(HImage gray, double cx, double cy, int size, int width, int height)
    {
        int half = Math.Max(2, size / 2);
        var roi = ClampRoi(new RoiSpec(
            (int)Math.Round(cx) - half, (int)Math.Round(cy) - half, half * 2, half * 2), width, height);
        using var crop = gray.CropRectangle1(roi.Y, roi.X, roi.Y + roi.Height - 1, roi.X + roi.Width - 1);
        return GrayBytes(crop, roi.X, roi.Y);
    }

    private static RoiSpec ClampRoi(RoiSpec roi, int width, int height)
    {
        int x = Math.Clamp(roi.X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(roi.Y, 0, Math.Max(0, height - 1));
        int w = Math.Clamp(roi.Width, 0, width - x);
        int h = Math.Clamp(roi.Height, 0, height - y);
        return new RoiSpec(x, y, w, h);
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
