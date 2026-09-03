using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Horizon.Workflow.Registration;

namespace FAAM.UI.Views;

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

    /// <summary>Starts / stops the ~10 Hz redraw. Off = nothing drawn and no timer.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) { _timer.Start(); Redraw(); }
        else { _timer.Stop(); Surface.Children.Clear(); _glyphs.Clear(); }
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
