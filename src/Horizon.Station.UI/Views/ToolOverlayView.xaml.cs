using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Horizon.Workflow.Registration;

namespace Horizon.Station.UI.Views;

/// <summary>
/// Live device-registration overlay for a camera surface: for every registered tool seen
/// by <see cref="Camera"/>, draws where the tool centre is right now (a crosshair marker),
/// its outline rectangle (width × length, mm), and a label. When the tool is outside the
/// visible frame the marker is pinned to the nearest edge with an arrow pointing toward it
/// and the distance in mm, so the operator still knows which way it is.
///
/// Placed over an <see cref="Image"/> the same way the detection-line canvas is — centred,
/// sized to the image's display box — and driven from the engine's cached motion states at
/// a fixed cadence (<see cref="ToolLocation"/> via <c>IProcessService.LocateTool</c>), so it
/// costs no hardware I/O. Works on the Run-page main feed, the Zoom2× inset (whose source
/// is the centre ROI — the offsets are from the centre either way) and the camera pop-out.
/// </summary>
public partial class ToolOverlayView : UserControl
{
    private const double MarkerHalf = 10;     // crosshair half-length (display px)
    private const double EdgeInset = 16;      // how far inside the box a pinned marker sits
    private static readonly Brush ToolBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));   // orange over video
    private static readonly Brush OutlineBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)); // grey outline box
    private static readonly Brush ObjectBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x70, 0xFF));  // purple: gripped object marker
    private static readonly Brush ObjectOutlineBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76)); // green: gripped object outline
    private static readonly Brush PartBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));         // cyan: part fiducials + local objects
    private static readonly Brush PartTaughtBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0xE5, 0xFF)); // dimmer: taught, not measured
    private static readonly Brush LabelBg = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));

    private EngineHost? _host;
    private Image? _image;
    private bool _cover;
    private string? _camera;
    private bool _active;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, ToolGlyph> _glyphs = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ToolRegistration> _regs = Array.Empty<ToolRegistration>();
    private IReadOnlyList<GrippedObject> _objects = Array.Empty<GrippedObject>();
    private IReadOnlyList<PartAlignment> _parts = Array.Empty<PartAlignment>();
    private readonly Canvas _partLayer = new() { IsHitTestVisible = false };   // parts are rebuilt per tick (few shapes)
    private readonly Canvas _beaconLayer = new() { IsHitTestVisible = false }; // beacons are rebuilt per tick (≤ 10, with their lines)
    private readonly Canvas _detectLayer = new() { IsHitTestVisible = false }; // recent Detect Object results, rebuilt per tick (≤ 8)
    private static readonly Brush DetectBrush = Frozen(0xFF, 0xE6, 0x00);      // yellow: a Detect Object result

    // A beacon is a small + mark: the vertical arm a little longer than the horizontal one.
    private const double BeaconArmV = 9;      // half-length of the vertical arm (display px)
    private const double BeaconArmH = 6;      // half-length of the horizontal arm (display px)

    /// <summary>The 10 beacon colours, cycled in planting order (<see cref="Horizon.Workflow.Beacon.ColorIndex"/>).</summary>
    private static readonly Brush[] BeaconBrushes =
    {
        Frozen(0xFF, 0x3B, 0x30), Frozen(0x34, 0xC7, 0x59), Frozen(0x0A, 0x84, 0xFF), Frozen(0xFF, 0xD6, 0x0A),
        Frozen(0xFF, 0x2D, 0xF0), Frozen(0x00, 0xE5, 0xFF), Frozen(0xFF, 0x9F, 0x0A), Frozen(0xBF, 0x5A, 0xF2),
        Frozen(0xA8, 0xFF, 0x3E), Frozen(0xFF, 0xFF, 0xFF),
    };

    // Yellow outline images of the beacons that carry one — built once per beacon, dropped with it.
    private readonly Dictionary<Horizon.Workflow.BeaconOutline, BitmapSource> _outlineImages = new();

    /// <summary>A mask (non-zero = white) as a translucent-yellow image; black stays transparent.</summary>
    public static BitmapSource OutlineImage(Horizon.Workflow.BeaconOutline outline)
    {
        int w = outline.Width, h = outline.Height;
        var px = new byte[w * h * 4];
        int n = Math.Min(outline.Mask.Length, w * h);
        for (int i = 0; i < n; i++)
        {
            if (outline.Mask[i] == 0) continue;
            int o = i * 4;
            px[o] = 0; px[o + 1] = 213; px[o + 2] = 255; px[o + 3] = 170;   // BGRA: translucent yellow
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public ToolOverlayView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Redraw();
        Unloaded += (_, _) => { if (_active) _timer.Stop(); };
        Loaded += (_, _) => { if (_active) _timer.Start(); };
    }

    /// <summary>
    /// Binds the overlay to a host and the <paramref name="image"/> it sits over.
    /// <paramref name="cover"/> must match the image's Stretch (false = Uniform, true = UniformToFill).
    /// </summary>
    public void Attach(EngineHost host, Image image, bool cover)
    {
        Detach();
        _host = host;
        _image = image;
        _cover = cover;
        image.SizeChanged += OnImageSizeChanged;
        host.Process.ToolRegistrationsChanged += OnRegistrationsChanged;
        host.Process.GrippedObjectsChanged += OnRegistrationsChanged;   // objects ride on the same redraw
        host.Process.PartAlignmentsChanged += OnRegistrationsChanged;
        if (!Surface.Children.Contains(_partLayer)) Surface.Children.Add(_partLayer);
        if (!Surface.Children.Contains(_beaconLayer)) Surface.Children.Add(_beaconLayer);
        if (!Surface.Children.Contains(_detectLayer)) Surface.Children.Add(_detectLayer);
        ReloadRegistrations();
    }

    /// <summary>Unhooks the host + image events (call when the owning surface goes away).</summary>
    public void Detach()
    {
        if (_image != null) _image.SizeChanged -= OnImageSizeChanged;
        if (_host != null)
        {
            _host.Process.ToolRegistrationsChanged -= OnRegistrationsChanged;
            _host.Process.GrippedObjectsChanged -= OnRegistrationsChanged;
            _host.Process.PartAlignmentsChanged -= OnRegistrationsChanged;
        }
        _image = null;
        _host = null;
    }

    private void OnImageSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    /// <summary>The camera id this surface shows — only tools registered against it are drawn.</summary>
    public string? Camera
    {
        get => _camera;
        set { _camera = value; Redraw(); }
    }

    /// <summary>
    /// True on the teach / registration pages, where the tool and gripped-object glyphs are the
    /// point of the page and must draw regardless of <see cref="Horizon.Common.AppSettings.ShowToolOverlay"/>.
    /// False (default) on the production surfaces, which honour that setting.
    /// </summary>
    public bool AlwaysShow { get; set; }

    /// <summary>Starts / stops the ~10 Hz redraw. Off = nothing drawn and no timer.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) { _timer.Start(); Redraw(); }
        else { _timer.Stop(); Surface.Children.Clear(); _glyphs.Clear(); _partLayer.Children.Clear(); _beaconLayer.Children.Clear(); _detectLayer.Children.Clear(); Surface.Children.Add(_partLayer); Surface.Children.Add(_beaconLayer); Surface.Children.Add(_detectLayer); }
    }

    private void OnRegistrationsChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnRegistrationsChanged); return; }
        ReloadRegistrations();
        Redraw();
    }

    private void ReloadRegistrations()
    {
        _regs = _host?.Process.GetToolRegistrations() ?? Array.Empty<ToolRegistration>();
        _objects = _host?.Process.GetGrippedObjects() ?? Array.Empty<GrippedObject>();
        _parts = _host?.Process.GetPartAlignments() ?? Array.Empty<PartAlignment>();
        // Drop glyphs for tools / objects that no longer exist.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _regs) keep.Add(r.ToolDevice);
        foreach (var o in _objects) keep.Add(ObjectKey(o.Name));
        foreach (var key in new List<string>(_glyphs.Keys))
            if (!keep.Contains(key)) { _glyphs[key].Remove(Surface); _glyphs.Remove(key); }
    }

    /// <summary>Recomputes every glyph from the current motion state and display geometry.</summary>
    public void Redraw()
    {
        if (!_active || _host == null || _image == null) return;
        if (_host.Engine.State == Horizon.Engine.EngineState.ShuttingDown)
        {
            // Facets are being (or have been) torn down — stop for good rather than throw each tick.
            _timer.Stop();
            foreach (var g in _glyphs.Values) g.Hide();
            return;
        }

        double availW = _image.ActualWidth, availH = _image.ActualHeight;
        if (_image.Source is not BitmapSource bmp || availW <= 0 || availH <= 0)
        {
            foreach (var g in _glyphs.Values) g.Hide();
            return;
        }
        double scale = MoveClick.DisplayScale(availW, availH, bmp.PixelWidth, bmp.PixelHeight, _cover);
        if (scale <= 0) return;

        Surface.Width = availW;
        Surface.Height = availH;
        var settings = _host.Settings.Current;

        if (AlwaysShow || settings.ShowToolOverlay)
        {
            foreach (var reg in _regs)
            {
                DrawOne(reg.ToolDevice, reg.Label, "tool", reg.CameraId,
                    () => _host.Process.LocateTool(reg.ToolDevice),
                    reg.OutlineWidthMm, reg.OutlineLengthMm, reg.OutlineOffsetXmm, reg.OutlineOffsetYmm,
                    ToolBrush, OutlineBrush, settings, availW, availH, scale);
            }
            foreach (var obj in _objects)
            {
                DrawOne(ObjectKey(obj.Name), obj.Name, obj.Name, obj.CameraId,
                    () => _host.Process.LocateGrippedObject(obj.Name),
                    obj.OutlineWidthMm, obj.OutlineLengthMm, obj.OutlineOffsetXmm, obj.OutlineOffsetYmm,
                    ObjectBrush, ObjectOutlineBrush, settings, availW, availH, scale);
            }
        }
        else
        {
            // Setting is off on a production surface: keep the glyphs (cheap to re-show) but hide them.
            foreach (var g in _glyphs.Values) g.Hide();
        }

        // Part Alignment is deprecated: its saved fiducials / objects are no longer drawn on the
        // production views (Run feed, zoom inset, camera pop-out) — only on a teach page.
        if (AlwaysShow) DrawParts(settings, availW, availH, scale);
        else _partLayer.Children.Clear();
        DrawBeacons(settings, availW, availH, scale);
        DrawDetections(settings, availW, availH, scale);
    }

    /// <summary>
    /// The recent <c>vision.detectObject</c> results on this camera, in thin yellow: the outline
    /// of what was matched (template box / the two corner edges / line / circle), a ringed cross
    /// at the point found, a pointer along its angle P (0° = up, clockwise +) and a label. Held
    /// in the camera device's axis values, so a mark stays on the object while the camera moves;
    /// outside the picture it is pinned to the nearest edge with its distance. Each fades out
    /// over the last quarter of <see cref="Horizon.Workflow.DetectionMark.Lifetime"/>. Drawn
    /// whatever <see cref="Horizon.Common.AppSettings.ShowToolOverlay"/> says.
    /// </summary>
    private void DrawDetections(Horizon.Common.AppSettings settings, double availW, double availH, double scale)
    {
        _detectLayer.Children.Clear();
        if (_host == null || _camera == null) return;
        _detectLayer.Width = availW;
        _detectLayer.Height = availH;

        var marks = _host.Process.GetDetectionMarks();
        if (marks.Count == 0) return;
        var now = DateTimeOffset.Now;

        foreach (var mark in marks)
        {
            if (!string.Equals(mark.CameraId, _camera, StringComparison.OrdinalIgnoreCase)) continue;
            var cam = _host.Engine.Motion.GetState(mark.Device);
            if (!cam.Valid) continue;
            double dxMm = mark.X - cam.Position.X, dyMm = mark.Y - cam.Position.Y;
            if (ToolOverlayMath.MmToSourcePx(dxMm, dyMm, settings) is not { } off) continue;

            var centre = ToolOverlayMath.ToDisplay(off.DxPx, off.DyPx, availW, availH, scale);
            var at = ToolOverlayMath.ClampToBox(centre, availW, availH, EdgeInset, out bool clamped);
            double opacity = Math.Clamp((1 - mark.Age(now)) * 4, 0, 1);   // full, then fades over the last quarter

            // What was matched, at its true place (the canvas clips whatever is out of the picture).
            foreach (var shape in mark.Outline ?? Array.Empty<IReadOnlyList<Horizon.Workflow.OutlinePoint>>())
            {
                var points = new PointCollection(shape.Count);
                foreach (var v in shape)
                {
                    if (ToolOverlayMath.MmToSourcePx(v.X - cam.Position.X, v.Y - cam.Position.Y, settings) is not { } o) continue;
                    points.Add(ToolOverlayMath.ToDisplay(o.DxPx, o.DyPx, availW, availH, scale));
                }
                if (points.Count >= 2)
                    Add(new Polyline { Points = points, Stroke = DetectBrush, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round }, 0, 0);
            }

            const double ring = 9, arm = 15, pointer = 34;
            Add(new Ellipse { Width = ring * 2, Height = ring * 2, Stroke = DetectBrush, StrokeThickness = 1 }, at.X - ring, at.Y - ring);
            Stroke(at.X - arm, at.Y, at.X + arm, at.Y);
            Stroke(at.X, at.Y - arm, at.X, at.Y + arm);

            // Angle pointer — only for a real detection in the picture (an operator mark has no angle).
            if (!clamped && !mark.Manual)
            {
                double rad = mark.P * Math.PI / 180.0, ux = Math.Sin(rad), uy = -Math.Cos(rad);
                var tip = new Point(at.X + ux * pointer, at.Y + uy * pointer);
                Stroke(at.X + ux * ring, at.Y + uy * ring, tip.X, tip.Y);
                var back = new Point(tip.X - ux * 9, tip.Y - uy * 9);
                Add(new Polygon
                {
                    Fill = DetectBrush,
                    Points = new PointCollection { tip, new(back.X - uy * 4.5, back.Y + ux * 4.5), new(back.X + uy * 4.5, back.Y - ux * 4.5) },
                }, 0, 0);
            }

            string text = clamped
                ? $"{mark.Label}  {Math.Sqrt(dxMm * dxMm + dyMm * dyMm):0.00} mm"
                : mark.Manual
                    ? $"{mark.Label} (marked)  X {mark.X:F4}  Y {mark.Y:F4}"
                    : $"{mark.Label}  X {mark.X:F4}  Y {mark.Y:F4}  P {mark.P:F2}°";
            var label = new Border
            {
                Background = LabelBg, CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock { Text = text, Foreground = DetectBrush, FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lx = at.X + arm + 4, ly = at.Y + arm - 2;
            if (lx + label.DesiredSize.Width > availW) lx = at.X - arm - 4 - label.DesiredSize.Width;
            if (ly + label.DesiredSize.Height > availH) ly = at.Y - arm - label.DesiredSize.Height;
            Add(label, Math.Max(0, lx), Math.Max(0, ly));

            void Add(UIElement element, double left, double top)
            {
                element.Opacity = opacity;
                Canvas.SetLeft(element, left); Canvas.SetTop(element, top);
                _detectLayer.Children.Add(element);
            }

            void Stroke(double x1, double y1, double x2, double y2)
            {
                Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = DetectBrush, StrokeThickness = 1 }, 0, 0);
            }
        }
    }

    /// <summary>
    /// The planted beacons (<c>beacon.plant</c>) and the construction lines between them
    /// (<c>beacon.line</c>). A beacon is a point in the camera device's axis values, placed from
    /// that device's live pose and drawn as a small + (vertical arm longer than the horizontal).
    /// Outside the picture it is pinned to the nearest edge with its distance. A line is drawn
    /// dashed between its two beacons' true places, with its length and angle at the middle.
    /// Drawn whatever <see cref="Horizon.Common.AppSettings.ShowToolOverlay"/> says — a beacon
    /// has its own show / hide flag.
    /// </summary>
    private void DrawBeacons(Horizon.Common.AppSettings settings, double availW, double availH, double scale)
    {
        _beaconLayer.Children.Clear();
        if (_host == null) return;
        _beaconLayer.Width = availW;
        _beaconLayer.Height = availH;

        var beacons = _host.Process.GetBeacons();

        // Forget the outline images of beacons that are gone.
        if (_outlineImages.Count > 0)
        {
            foreach (var stale in _outlineImages.Keys.Where(k => !beacons.Any(b => ReferenceEquals(b.Outline, k))).ToList())
                _outlineImages.Remove(stale);
        }
        if (beacons.Count == 0) return;

        // Every placeable beacon (shown or not) — the lines join them at their true places.
        var placed = new Dictionary<string, (Point At, double X, double Y, Brush Brush)>(StringComparer.OrdinalIgnoreCase);

        foreach (var beacon in beacons)
        {
            var cam = _host.Engine.Motion.GetState(beacon.Device);
            if (!cam.Valid) continue;
            // A beacon that rides on a tool is placed by Layer 2 from the tool's live pose.
            if (_host.Process.LocateBeacon(beacon) is not { } spot) continue;
            double dxMm = spot.X - cam.Position.X, dyMm = spot.Y - cam.Position.Y;
            if (ToolOverlayMath.MmToSourcePx(dxMm, dyMm, settings) is not { } off) continue;

            var brush = BeaconBrushes[((beacon.ColorIndex % BeaconBrushes.Length) + BeaconBrushes.Length) % BeaconBrushes.Length];
            var centre = ToolOverlayMath.ToDisplay(off.DxPx, off.DyPx, availW, availH, scale);
            placed[beacon.Name] = (centre, spot.X, spot.Y, brush);
            if (!beacon.Visible) continue;
            var at = ToolOverlayMath.ClampToBox(centre, availW, availH, EdgeInset, out bool clamped);

            // The snapshot's white pixels, yellow, at the place the snapshot was taken — carried
            // along exactly like the marker (same offset from it as when it was planted).
            if (beacon.Outline is { Width: > 0, Height: > 0 } outline)
            {
                if (!_outlineImages.TryGetValue(outline, out var image))
                    _outlineImages[outline] = image = OutlineImage(outline);
                var shift = ToolOverlayMath.MmToSourcePx(outline.CentreX - beacon.X, outline.CentreY - beacon.Y, settings);
                if (shift is { } s)
                {
                    double iw = outline.Width * scale, ih = outline.Height * scale;
                    var picture = new Image { Source = image, Width = iw, Height = ih, Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
                    Canvas.SetLeft(picture, centre.X + s.DxPx * scale - iw / 2);
                    Canvas.SetTop(picture, centre.Y + s.DyPx * scale - ih / 2);
                    _beaconLayer.Children.Add(picture);
                }
            }

            // The + mark: two plain lines.
            _beaconLayer.Children.Add(new Line { X1 = at.X, Y1 = at.Y - BeaconArmV, X2 = at.X, Y2 = at.Y + BeaconArmV, Stroke = brush, StrokeThickness = 1.5 });
            _beaconLayer.Children.Add(new Line { X1 = at.X - BeaconArmH, Y1 = at.Y, X2 = at.X + BeaconArmH, Y2 = at.Y, Stroke = brush, StrokeThickness = 1.5 });

            string title = beacon.RidesOnTool ? $"{beacon.Name} · {beacon.ToolDevice}" : beacon.Name;
            string text = clamped ? $"{title}  {Math.Sqrt(dxMm * dxMm + dyMm * dyMm):0.00} mm" : title;
            AddLabel(text, brush, at.X + 8, at.Y - 20, at.X - 8, at.Y + 8);
        }

        foreach (var line in _host.Process.GetBeaconLines())
        {
            if (!line.Visible) continue;
            if (!placed.TryGetValue(line.From, out var a) || !placed.TryGetValue(line.To, out var b)) continue;

            // Between the true places — the canvas clips whatever is outside the picture.
            _beaconLayer.Children.Add(new Line
            {
                X1 = a.At.X, Y1 = a.At.Y, X2 = b.At.X, Y2 = b.At.Y, Stroke = a.Brush, StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 6, 4 },
            });

            double mx = (a.At.X + b.At.X) / 2.0, my = (a.At.Y + b.At.Y) / 2.0;
            if (mx < 0 || my < 0 || mx > availW || my > availH) continue;   // middle out of view: no label
            var m = Horizon.Workflow.BeaconLine.Measure(a.X, a.Y, b.X, b.Y, settings);
            _beaconLayer.Children.Add(new Line { X1 = mx - 3, Y1 = my, X2 = mx + 3, Y2 = my, Stroke = a.Brush, StrokeThickness = 1.2 });
            _beaconLayer.Children.Add(new Line { X1 = mx, Y1 = my - 3, X2 = mx, Y2 = my + 3, Stroke = a.Brush, StrokeThickness = 1.2 });
            AddLabel($"{line.Name}  {m.Length:0.0000} mm  {m.AngleDeg:0.00}°", a.Brush, mx + 8, my + 6, mx - 8, my - 22);
        }

        // A label at (lx, ly); when it would leave the picture on the right it ends at altRight
        // instead, and when it would leave at the top it moves to altTop.
        void AddLabel(string text, Brush brush, double lx, double ly, double altRight, double altTop)
        {
            var label = new Border
            {
                Background = LabelBg, CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock { Text = text, Foreground = brush, FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (lx + label.DesiredSize.Width > availW) lx = altRight - label.DesiredSize.Width;
            if (ly < 0 || ly + label.DesiredSize.Height > availH) ly = altTop;
            Canvas.SetLeft(label, lx); Canvas.SetTop(label, ly);
            _beaconLayer.Children.Add(label);
        }
    }

    /// <summary>
    /// Saved part alignments: fiducials (diamonds E1 / E2), local objects (a circle, or a line
    /// between its two points) and, when an object has an offset, a small cross at the offset
    /// target. Fixed in the gantry frame — they do not ride on the hexapod — so they are placed
    /// from the camera device's live pose. Dimmer when taught-only / not detected by the last run.
    /// </summary>
    private void DrawParts(Horizon.Common.AppSettings settings, double availW, double availH, double scale)
    {
        _partLayer.Children.Clear();
        if (_host == null) return;
        _partLayer.Width = availW;
        _partLayer.Height = availH;

        foreach (var part in _parts)
        {
            if (_camera == null || !string.Equals(part.CameraId, _camera, StringComparison.OrdinalIgnoreCase)) continue;
            var cam = _host.Engine.Motion.GetState(part.CameraDevice);
            if (!cam.Valid) continue;
            var snap = _host.Process.GetPartSnapshot(part.Name);
            if (!snap.IsSuccess) continue;

            Point? Place(double gx, double gy)
            {
                var px = ToolOverlayMath.MmToSourcePx(gx - cam.Position.X, gy - cam.Position.Y, settings);
                return px is { } o ? ToolOverlayMath.ToDisplay(o.DxPx, o.DyPx, availW, availH, scale) : null;
            }

            var fidBrush = snap.Value.FromMeasurement ? PartBrush : PartTaughtBrush;
            if (snap.Value.E1 is { } e1) Diamond(Place, e1.X, e1.Y, $"{part.Name} E1", fidBrush);
            if (snap.Value.E2 is { } e2) Diamond(Place, e2.X, e2.Y, $"{part.Name} E2", fidBrush);

            foreach (var o in snap.Value.Objects)
            {
                var brush = o.Measured ? PartBrush : PartTaughtBrush;
                var pts = o.Points.Select(p => Place(p.X, p.Y)).ToArray();
                if (o.IsLine && pts.Length == 2 && pts[0] is { } a && pts[1] is { } b)
                {
                    _partLayer.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = 2 });
                    Circle(a, brush); Circle(b, brush);
                    Label(a, o.Name + (o.Measured ? "" : " (taught)"), brush);
                }
                else if (pts.Length >= 1 && pts[0] is { } p0)
                {
                    Circle(p0, brush);
                    Label(p0, o.Name + (o.Measured ? "" : " (taught)"), brush);
                }
                if ((o.Target.X != o.Reference.X || o.Target.Y != o.Reference.Y) && Place(o.Target.X, o.Target.Y) is { } t)
                {
                    _partLayer.Children.Add(new Line { X1 = t.X - 6, X2 = t.X + 6, Y1 = t.Y, Y2 = t.Y, Stroke = brush, StrokeThickness = 1.5 });
                    _partLayer.Children.Add(new Line { X1 = t.X, X2 = t.X, Y1 = t.Y - 6, Y2 = t.Y + 6, Stroke = brush, StrokeThickness = 1.5 });
                }
            }
        }

        void Diamond(Func<double, double, Point?> place, double gx, double gy, string label, Brush brush)
        {
            if (place(gx, gy) is not { } d) return;
            _partLayer.Children.Add(new Polygon
            {
                Stroke = brush, StrokeThickness = 2, Fill = Brushes.Transparent,
                Points = new PointCollection { new(d.X, d.Y - 7), new(d.X + 7, d.Y), new(d.X, d.Y + 7), new(d.X - 7, d.Y) },
            });
            Label(d, label, brush);
        }

        void Circle(Point d, Brush brush)
        {
            var e = new Ellipse { Width = 9, Height = 9, Stroke = brush, StrokeThickness = 2, Fill = Brushes.Transparent };
            Canvas.SetLeft(e, d.X - 4.5); Canvas.SetTop(e, d.Y - 4.5);
            _partLayer.Children.Add(e);
        }

        void Label(Point d, string text, Brush brush)
        {
            var box = new Border
            {
                Background = LabelBg, CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock { Text = text, Foreground = brush, FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
            Canvas.SetLeft(box, d.X + 8); Canvas.SetTop(box, d.Y - 16);
            _partLayer.Children.Add(box);
        }
    }

    private static string ObjectKey(string name) => "obj:" + name;

    /// <summary>Draws (or hides) one glyph — a registered tool or a gripped object — from its live location.</summary>
    private void DrawOne(string key, string label, string zSubject, string cameraId,
                         Func<Horizon.Common.Result<ToolLocation>> locate,
                         double widthMm, double lengthMm, double offXmm, double offYmm,
                         Brush markerBrush, Brush outlineBrush,
                         Horizon.Common.AppSettings settings, double availW, double availH, double scale)
    {
        if (!_glyphs.TryGetValue(key, out var glyph))
        {
            glyph = new ToolGlyph(markerBrush, outlineBrush);
            glyph.Add(Surface);
            _glyphs[key] = glyph;
        }

        if (_camera == null || !string.Equals(cameraId, _camera, StringComparison.OrdinalIgnoreCase))
        {
            glyph.Hide();
            return;
        }

        var loc = locate();
        var px = loc.IsSuccess ? ToolOverlayMath.MmToSourcePx(loc.Value.DxMm, loc.Value.DyMm, settings) : null;
        if (px is not { } off)
        {
            glyph.Hide();
            return;
        }

        var centre = ToolOverlayMath.ToDisplay(off.DxPx, off.DyPx, availW, availH, scale);
        double wPx = settings.MmPerPixel > 0 ? widthMm / settings.MmPerPixel * scale : 0;
        double hPx = settings.MmPerPixel > 0 ? lengthMm / settings.MmPerPixel * scale : 0;
        // The outline box is anchored to the point but need not be centred on it.
        var boxOff = ToolOverlayMath.MmToSourcePx(offXmm, offYmm, settings);
        var boxCentre = boxOff is { } bo
            ? new Point(centre.X + bo.DxPx * scale, centre.Y + bo.DyPx * scale)
            : centre;
        var pinned = ToolOverlayMath.ClampToBox(centre, availW, availH, EdgeInset, out bool clamped);

        // Z read-out: Z − CameraZ in the camera device frame (gantry Z− is up), so a
        // negative delta means the subject sits above the focus plane.
        string zText = loc.Value.HasZ
            ? (loc.Value.DzMm < 0 ? $"{zSubject} higher {loc.Value.DzMm:0.00}" : $"{zSubject} lower {loc.Value.DzMm:+0.00}")
            : "";

        glyph.Show(label, zText, centre, boxCentre, pinned, clamped, wPx, hPx, loc.Value.DistanceMm, availW, availH);
    }

    /// <summary>The drawable pieces for one tool — reused across redraws (no per-tick allocation).</summary>
    private sealed class ToolGlyph
    {
        private readonly Rectangle _outline;
        private readonly Line _h;
        private readonly Line _v;
        private readonly Ellipse _dot;
        private readonly Polygon _arrow;
        private readonly Border _labelBox;
        private readonly TextBlock _label;

        public ToolGlyph(Brush marker, Brush outline)
        {
            _outline = new Rectangle
            {
                Stroke = outline, StrokeThickness = 1.5, Fill = Brushes.Transparent,
                StrokeDashArray = new DoubleCollection { 6, 3 },
            };
            _h = new Line { Stroke = marker, StrokeThickness = 1.5 };
            _v = new Line { Stroke = marker, StrokeThickness = 1.5 };
            _dot = new Ellipse { Width = 6, Height = 6, Fill = marker };
            _arrow = new Polygon { Fill = marker, Visibility = Visibility.Collapsed };
            _label = new TextBlock { Foreground = marker, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            _labelBox = new Border
            {
                Background = LabelBg, CornerRadius = new CornerRadius(2),
                Padding = new Thickness(5, 1, 5, 1), Child = _label,
            };
        }

        public void Add(Canvas c)
        {
            c.Children.Add(_outline); c.Children.Add(_h); c.Children.Add(_v);
            c.Children.Add(_dot); c.Children.Add(_arrow); c.Children.Add(_labelBox);
        }

        public void Remove(Canvas c)
        {
            c.Children.Remove(_outline); c.Children.Remove(_h); c.Children.Remove(_v);
            c.Children.Remove(_dot); c.Children.Remove(_arrow); c.Children.Remove(_labelBox);
        }

        public void Hide()
        {
            _outline.Visibility = _h.Visibility = _v.Visibility = _dot.Visibility =
                _arrow.Visibility = _labelBox.Visibility = Visibility.Collapsed;
        }

        /// <param name="zText">Focus-height read-out ("tool higher −1.00"), or empty without a focus Z.</param>
        /// <param name="centre">Where the tool really is (display px, may be off the box).</param>
        /// <param name="boxCentre">Centre of the outline box (the anchor plus the registration's offset).</param>
        /// <param name="pinned">Where to draw the marker (centre, or clamped to the edge).</param>
        /// <param name="clamped">True when the tool is outside the visible frame.</param>
        public void Show(string label, string zText, Point centre, Point boxCentre, Point pinned, bool clamped,
                         double wPx, double hPx, double distMm, double boxW, double boxH)
        {
            // Crosshair marker + dot at the pinned point.
            _h.X1 = pinned.X - MarkerHalf; _h.X2 = pinned.X + MarkerHalf; _h.Y1 = _h.Y2 = pinned.Y;
            _v.Y1 = pinned.Y - MarkerHalf; _v.Y2 = pinned.Y + MarkerHalf; _v.X1 = _v.X2 = pinned.X;
            Canvas.SetLeft(_dot, pinned.X - 3); Canvas.SetTop(_dot, pinned.Y - 3);
            _h.Visibility = _v.Visibility = _dot.Visibility = Visibility.Visible;

            // Outline rectangle at its TRUE place (the canvas clips whatever spills out),
            // so a partially visible gripper still shows the edge that is in frame.
            if (wPx > 0 && hPx > 0)
            {
                _outline.Width = wPx; _outline.Height = hPx;
                Canvas.SetLeft(_outline, boxCentre.X - wPx / 2); Canvas.SetTop(_outline, boxCentre.Y - hPx / 2);
                _outline.Visibility = Visibility.Visible;
            }
            else _outline.Visibility = Visibility.Collapsed;

            if (clamped)
            {
                // Arrow from the pinned marker toward the real location.
                var dx = centre.X - pinned.X; var dy = centre.Y - pinned.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-6)
                {
                    double ux = dx / len, uy = dy / len;
                    var tip = new Point(pinned.X + ux * (MarkerHalf + 12), pinned.Y + uy * (MarkerHalf + 12));
                    var baseC = new Point(pinned.X + ux * (MarkerHalf + 2), pinned.Y + uy * (MarkerHalf + 2));
                    var nx = -uy * 5; var ny = ux * 5;
                    _arrow.Points = new PointCollection
                    {
                        tip, new Point(baseC.X + nx, baseC.Y + ny), new Point(baseC.X - nx, baseC.Y - ny),
                    };
                    _arrow.Visibility = Visibility.Visible;
                }
                _label.Text = $"{label}  {distMm:0.00} mm  {Direction(dx, dy)}" + (zText.Length > 0 ? $"  ·  {zText}" : "");
            }
            else
            {
                _arrow.Visibility = Visibility.Collapsed;
                _label.Text = zText.Length > 0 ? $"{label}  ·  {zText}" : label;
            }

            // Label sits just below-right of the marker, pulled back inside the box when pinned.
            _labelBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var ls = _labelBox.DesiredSize;
            double lx = pinned.X + MarkerHalf + 4, ly = pinned.Y + MarkerHalf + 2;
            if (clamped)
            {
                if (pinned.X + ls.Width + MarkerHalf + 8 > boxW) lx = pinned.X - MarkerHalf - 4 - ls.Width;
                if (pinned.Y + ls.Height + MarkerHalf + 6 > boxH) ly = pinned.Y - MarkerHalf - 2 - ls.Height;
            }
            Canvas.SetLeft(_labelBox, lx); Canvas.SetTop(_labelBox, ly);
            _labelBox.Visibility = Visibility.Visible;
        }

        private static string Direction(double dx, double dy)
        {
            // Screen-direction glyph: which way to look / jog to find the tool.
            bool right = dx > 1, left = dx < -1, down = dy > 1, up = dy < -1;
            return (up, down, left, right) switch
            {
                (true, _, true, _) => "↖", (true, _, _, true) => "↗",
                (_, true, true, _) => "↙", (_, true, _, true) => "↘",
                (true, _, _, _) => "↑", (_, true, _, _) => "↓",
                (_, _, true, _) => "←", (_, _, _, true) => "→",
                _ => "",
            };
        }
    }
}
