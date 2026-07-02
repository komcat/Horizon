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

    /// <summary>One fitted edge segment (endpoints in source px + its length).</summary>
    private readonly record struct EdgeSeg(double X1, double Y1, double X2, double Y2, double Length);

    public Task<Result<VisionDetectResult>> FindCornerAsync(
        CameraFrame frame, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, CornerParams p)
        => RunFrameAsync<VisionDetectResult>(frame, f =>
        {
            using var image = ToHImage(f);
            using var gray = ToGray(image, f.Channels);
            using var search = ReduceFilterReduce(gray, searchRoi, filters, f.Width, f.Height);

            var notFound = new VisionDetectResult(false, Array.Empty<VisionResultValue>(), null);

            // Foreground (filters set the polarity: die = white), largest blob = the die.
            HOperatorSet.Threshold(search, out HObject fg, 128.0, 255.0);
            try
            {
                HOperatorSet.Connection(fg, out HObject conn);
                try
                {
                    HOperatorSet.CountObj(conn, out HTuple nreg);
                    if (nreg.I == 0) return Result<VisionDetectResult>.Ok(notFound);

                    HOperatorSet.AreaCenter(conn, out HTuple areas, out HTuple rowc, out HTuple colc);
                    int bi = 0;
                    double bestArea = -1;
                    for (int i = 0; i < nreg.I; i++)
                        if (areas[i].D > bestArea) { bestArea = areas[i].D; bi = i; }
                    double centroidX = colc[bi].D, centroidY = rowc[bi].D;

                    HOperatorSet.SelectObj(conn, out HObject die, bi + 1);   // HALCON is 1-based
                    try
                    {
                        // Trace the blob outline and break it into straight segments; the two
                        // real die edges become the two longest non-border segments.
                        HOperatorSet.GenContourRegionXld(die, out HObject contour, "border");
                        try
                        {
                            HOperatorSet.SegmentContoursXld(contour, out HObject segments, "lines", 5, 4, 2);
                            try
                            {
                                var (bx, by, bw, bh) = DetectionBounds(searchRoi, f.Width, f.Height);
                                var edges = CollectEdges(segments, p.MinEdgeLength, bx, by, bw, bh);
                                if (edges.Count < 2) return Result<VisionDetectResult>.Ok(notFound);

                                // Longest edge, then the longest other edge ~perpendicular to it.
                                edges.Sort((a, b) => b.Length.CompareTo(a.Length));
                                var a = edges[0];
                                double aAng = EdgeAngle(a);
                                EdgeSeg? bOpt = null;
                                for (int i = 1; i < edges.Count; i++)
                                {
                                    if (Math.Abs(90.0 - AngleBetween(aAng, EdgeAngle(edges[i]))) <= p.MaxAngleDevDeg)
                                    { bOpt = edges[i]; break; }
                                }
                                if (bOpt is not { } b) return Result<VisionDetectResult>.Ok(notFound);
                                double dev = Math.Abs(90.0 - AngleBetween(aAng, EdgeAngle(b)));

                                if (!LineIntersection(a, b, out double px, out double py))
                                    return Result<VisionDetectResult>.Ok(notFound);

                                // Each edge extends from the corner along the die side: the ray
                                // points to whichever endpoint is farther from the corner.
                                var (axf, ayf) = FarEndpoint(a, px, py);
                                var (bxf, byf) = FarEndpoint(b, px, py);

                                // Orientation from where the filled region sits relative to the corner.
                                bool right = centroidX > px, below = centroidY > py;
                                int orientation = (right, below) switch
                                {
                                    (true, true) => 1,    // interior bottom-right → TopLeft
                                    (false, true) => 2,   // interior bottom-left  → TopRight
                                    (true, false) => 3,   // interior top-right    → BottomLeft
                                    _ => 4,               // interior top-left     → BottomRight
                                };
                                if (p.Expected != CornerOrientation.Any && (int)p.Expected != orientation)
                                    return Result<VisionDetectResult>.Ok(notFound);

                                // Bisector: from the corner toward the interior (continuous 0..360,
                                // math convention with y up), so the four corners read distinctly.
                                double bis = Math.Atan2(-(centroidY - py), centroidX - px) * 180.0 / Math.PI;
                                bis = ((bis % 360.0) + 360.0) % 360.0;
                                double score = Math.Clamp(1.0 - dev / 90.0, 0.0, 1.0);

                                var values = new[]
                                {
                                    new VisionResultValue("x", px),
                                    new VisionResultValue("y", py),
                                    new VisionResultValue("ax", axf),
                                    new VisionResultValue("ay", ayf),
                                    new VisionResultValue("bx", bxf),
                                    new VisionResultValue("by", byf),
                                    new VisionResultValue("angle", bis),
                                    new VisionResultValue("orientation", orientation),
                                    new VisionResultValue("score", score),
                                };
                                int cropSize = (int)(Math.Max(a.Length, b.Length) + 20);
                                return Result<VisionDetectResult>.Ok(new VisionDetectResult(
                                    true, values, CropAround(gray, px, py, cropSize, f.Width, f.Height)));
                            }
                            finally { segments.Dispose(); }
                        }
                        finally { contour.Dispose(); }
                    }
                    finally { die.Dispose(); }
                }
                finally { conn.Dispose(); }
            }
            finally { fg.Dispose(); }
        });

    public Task<Result<VisionDetectResult>> FindCornerAsync(
        string cameraId, IReadOnlyList<VisionFilter> filters, RoiSpec? searchRoi, CornerParams p)
        => WithCachedFrame(cameraId, f => FindCornerAsync(f, filters, searchRoi, p));

    /// <summary>
    /// Fits a line to each contour segment and returns those that are long enough and not lying
    /// flush against a detection-bound edge (those are ROI/frame artefacts, not die edges).
    /// </summary>
    private static List<EdgeSeg> CollectEdges(
        HObject segments, double minLength, double bx, double by, double bw, double bh)
    {
        var list = new List<EdgeSeg>();
        HOperatorSet.CountObj(segments, out HTuple n);
        for (int i = 1; i <= n.I; i++)
        {
            HOperatorSet.SelectObj(segments, out HObject seg, i);
            try
            {
                HOperatorSet.FitLineContourXld(seg, "tukey", -1, 0, 5, 2.0,
                    out HTuple rB, out HTuple cB, out HTuple rE, out HTuple cE, out _, out _, out _);
                if ((rB?.Length ?? 0) == 0) continue;
                double x1 = cB[0].D, y1 = rB[0].D, x2 = cE[0].D, y2 = rE[0].D;
                double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                if (len < minLength) continue;
                if (OnDetectionBorder(x1, y1, x2, y2, bx, by, bw, bh)) continue;
                list.Add(new EdgeSeg(x1, y1, x2, y2, len));
            }
            finally { seg.Dispose(); }
        }
        return list;
    }

    /// <summary>Detection area (x,y,w,h) in source px — the clamped search ROI, or the whole frame.</summary>
    private static (double X, double Y, double W, double H) DetectionBounds(RoiSpec? roi, int width, int height)
    {
        if (roi == null) return (0, 0, width, height);
        var c = ClampRoi(roi, width, height);
        return (c.X, c.Y, c.Width, c.Height);
    }

    /// <summary>True if both endpoints hug the same detection-bound edge (an ROI/frame artefact edge).</summary>
    private static bool OnDetectionBorder(
        double x1, double y1, double x2, double y2, double bx, double by, double bw, double bh)
    {
        const double m = 3.0;
        bool leftBoth = x1 <= bx + m && x2 <= bx + m;
        bool rightBoth = x1 >= bx + bw - 1 - m && x2 >= bx + bw - 1 - m;
        bool topBoth = y1 <= by + m && y2 <= by + m;
        bool botBoth = y1 >= by + bh - 1 - m && y2 >= by + bh - 1 - m;
        return leftBoth || rightBoth || topBoth || botBoth;
    }

    /// <summary>Undirected edge orientation folded into [0,180) — for perpendicularity tests.</summary>
    private static double EdgeAngle(EdgeSeg e)
    {
        double a = Math.Atan2(-(e.Y2 - e.Y1), e.X2 - e.X1) * 180.0 / Math.PI;
        return ((a % 180.0) + 180.0) % 180.0;
    }

    /// <summary>Smallest angle (0..90) between two undirected orientations in [0,180).</summary>
    private static double AngleBetween(double a, double b)
    {
        double d = Math.Abs(a - b) % 180.0;
        return d > 90.0 ? 180.0 - d : d;
    }

    /// <summary>The endpoint of an edge farther from the corner — the ray direction along the die side.</summary>
    private static (double X, double Y) FarEndpoint(EdgeSeg e, double px, double py)
    {
        double d1 = (e.X1 - px) * (e.X1 - px) + (e.Y1 - py) * (e.Y1 - py);
        double d2 = (e.X2 - px) * (e.X2 - px) + (e.Y2 - py) * (e.Y2 - py);
        return d2 >= d1 ? (e.X2, e.Y2) : (e.X1, e.Y1);
    }

    /// <summary>Intersection of the two infinite lines through the edges. False if near-parallel.</summary>
    private static bool LineIntersection(EdgeSeg a, EdgeSeg b, out double x, out double y)
    {
        double denom = (a.X1 - a.X2) * (b.Y1 - b.Y2) - (a.Y1 - a.Y2) * (b.X1 - b.X2);
        if (Math.Abs(denom) < 1e-9) { x = y = 0; return false; }
        double ca = a.X1 * a.Y2 - a.Y1 * a.X2;
        double cb = b.X1 * b.Y2 - b.Y1 * b.X2;
        x = (ca * (b.X1 - b.X2) - (a.X1 - a.X2) * cb) / denom;
        y = (ca * (b.Y1 - b.Y2) - (a.Y1 - a.Y2) * cb) / denom;
        return true;
    }

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
