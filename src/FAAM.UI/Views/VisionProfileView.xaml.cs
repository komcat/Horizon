using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI.Views;

/// <summary>
/// The Vision Profile page — authors a per-position vision recipe (camera + lighting
/// + pre-process filter + NCC template) and runs it. Persistence, template capture
/// and detection go through Layer 2 (<see cref="IProcessService"/>); the live feed,
/// exposure and lighting talk to the engine facets directly.
/// </summary>
public partial class VisionProfileView : UserControl
{
    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<ResultRow> _results = new();

    private readonly object _frameLock = new();
    private CameraFrame? _pendingFrame;
    private bool _renderScheduled;

    private string? _camera;
    private string _device = "gantry-main";
    private bool _live;
    private bool _active;
    private bool _subscribed;
    private bool _snapPending;
    private bool _loadingUi;        // suppress combo events while populating

    private WriteableBitmap? _mainBmp;
    private WriteableBitmap? _filterBmp;
    private WriteableBitmap? _cropBmp;
    private WriteableBitmap? _templateBmp;
    private CameraFrame? _lastFrame;

    private string _uid = string.Empty;   // current profile uid (empty = unsaved/new)
    private TemplateRef? _template;
    private RoiSpec? _searchRoi;          // optional detection region (null = whole frame)

    // overlay state (drawn on the live feed after a detection)
    private NccCrop? _lastFilterCrop;
    private NccCrop? _overlayCrop;          // the matched crop the geometry is drawn on
    private WriteableBitmap? _overlayBmp;
    private byte[]? _overlayPx;             // reused edges-overlay pixel buffer (avoids LOH churn)
    private Dictionary<string, double>? _overlayValues;
    private string _overlayMethod = "ncc";
    private int _overlayTemplateW, _overlayTemplateH;
    private bool _overlayHasFilters;
    private bool _previewBusy;
    private bool _previewDirty;
    private DispatcherTimer? _previewTimer;                    // debounces slider-driven previews
    private TaskCompletionSource<CameraFrame>? _frameWaiter;   // set while Detect waits for a fresh frame

    /// <summary>One key→value row in the results grid (public for WPF data binding).</summary>
    public sealed class ResultRow
    {
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    public VisionProfileView(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        InitializeComponent();

        _loadingUi = true;
        CameraCombo.ItemsSource = _host.Engine.Camera.CameraIds;
        if (_host.Engine.Camera.CameraIds.Count > 0) CameraCombo.SelectedIndex = 0;
        _camera = CameraCombo.SelectedItem as string;

        LightingCombo.ItemsSource = _host.Engine.Lighting.DeviceNames;
        if (_host.Engine.Lighting.DeviceNames.Count > 0) LightingCombo.SelectedIndex = 0;

        RefreshPositions();
        RefreshProfiles();
        _loadingUi = false;

        Filter1.Changed += OnFilterChanged;
        Filter2.Changed += OnFilterChanged;
        Filter3.Changed += OnFilterChanged;

        ResultsGrid.ItemsSource = _results;
        MethodCombo.SelectedIndex = 0;   // NCC; drives the method-panel visibility

        // Debounce filter-slider previews so a drag runs HALCON once, after the pause.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewTimer.Tick += (_, _) => { _previewTimer!.Stop(); SchedulePreview(); };

        TemplateImage.SizeChanged += (_, _) => RedrawAnchorFrame();
    }

    private string MethodKey() => MethodCombo.SelectedIndex switch { 1 => "circle", 2 => "line", _ => "ncc" };

    private void OnMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NccPanel == null) return;   // fires during InitializeComponent
        int idx = MethodCombo.SelectedIndex;
        NccPanel.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        CirclePanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        LinePanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        MethodHeader.Text = idx switch { 1 => "CIRCLE", 2 => "LINE", _ => "TEMPLATE (NCC)" };

        // The previous result's geometry no longer matches the new method — drop it.
        _overlayValues = null;
        _overlayCrop = null;
        RedrawOverlayCanvas();
    }

    // --- search ROI ---

    private async void OnSetSearchRoi(object sender, RoutedEventArgs e)
    {
        var frame = await AcquireFreshFrameAsync();
        if (frame is not { } f) { Log("No frame — turn Live on or capture a Single frame."); return; }
        var picker = new RoiSelectWindow(await FrameToBitmapAsync(f), f.Width, f.Height) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true && picker.Roi is { } roi)
        {
            _searchRoi = roi;
            UpdateSearchRoiText();
            RedrawOverlayCanvas();
            Log($"Search ROI set: {roi.Width}×{roi.Height}px.");
        }
    }

    private void OnClearSearchRoi(object sender, RoutedEventArgs e)
    {
        _searchRoi = null;
        UpdateSearchRoiText();
        RedrawOverlayCanvas();
        Log("Search ROI cleared (whole image).");
    }

    private void UpdateSearchRoiText()
        => SearchRoiText.Text = _searchRoi is { } r ? $"x {r.X} y {r.Y}  {r.Width}×{r.Height}" : "(whole image)";

    // --- page lifecycle (called by the shell) ---

    public void Activate()
    {
        if (_active) return;
        _active = true;
        RefreshPositions();
        RefreshProfiles();
        if (!_subscribed)
        {
            _host.Engine.Camera.FrameReady += OnFrameReady;
            _subscribed = true;
        }
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _previewTimer?.Stop();

        if (_subscribed)
        {
            _host.Engine.Camera.FrameReady -= OnFrameReady;
            _subscribed = false;
        }
        if (_camera == null) return;

        // Release exactly the one lease this page holds: unchecking Live routes
        // through OnLiveToggled (a single StopGrabbing); a pending single-frame
        // snap holds its own lease until the frame lands, so release it here.
        if (_live)
            LiveToggle.IsChecked = false;
        else if (_snapPending)
        {
            _snapPending = false;
            _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
    }

    // --- frame routing ---

    private void OnFrameReady(CameraFrame frame)
    {
        if (!string.Equals(frame.Camera, _camera, StringComparison.OrdinalIgnoreCase)) return;
        lock (_frameLock)
        {
            _pendingFrame = frame;
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        _dispatcher.BeginInvoke(RenderPending);
    }

    private void RenderPending()
    {
        CameraFrame? frame;
        lock (_frameLock)
        {
            _renderScheduled = false;
            frame = _pendingFrame;
            _pendingFrame = null;
        }
        if (frame == null) return;

        _lastFrame = frame;
        _mainBmp = Blit(MainImage, _mainBmp, frame.Width, frame.Height, frame.Channels, frame.Data);
        if (_mainBmp != null) NoVideo.Visibility = Visibility.Collapsed;

        // Hand the fresh frame to anything waiting on the next one (e.g. Detect).
        _frameWaiter?.TrySetResult(frame);

        // A pending single-frame snap is satisfied — stop grabbing again unless live.
        if (_snapPending && !_live && _camera != null)
        {
            _snapPending = false;
            _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
    }

    /// <summary>
    /// Waits for the next fresh frame from the selected camera. When not live, briefly
    /// grabs to fetch one. Falls back to the last frame on timeout. Used by Detect so
    /// it always matches against the current scene, not a stale frame.
    /// </summary>
    private async Task<CameraFrame?> AcquireFreshFrameAsync(int timeoutMs = 2000)
    {
        if (_camera == null) return _lastFrame;

        var tcs = new TaskCompletionSource<CameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _frameWaiter = tcs;
        bool startedHere = false;
        try
        {
            if (!_live)
            {
                startedHere = true;
                await _host.Engine.Camera.StartGrabbingAsync(_camera);
            }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return done == tcs.Task ? tcs.Task.Result : _lastFrame;
        }
        catch { return _lastFrame; }
        finally
        {
            _frameWaiter = null;
            if (startedHere && _camera != null)
                _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
    }

    // --- camera controls ---

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        var next = CameraCombo.SelectedItem as string;
        if (string.Equals(next, _camera, StringComparison.OrdinalIgnoreCase)) return;

        // Move any live lease to the newly-selected camera.
        if (_live && _camera != null) _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        _camera = next;
        _mainBmp = null;
        MainImage.Source = null;
        NoVideo.Visibility = Visibility.Visible;
        if (_live && _camera != null) _ = _host.Engine.Camera.StartGrabbingAsync(_camera);
        LoadExposureFromCamera();
    }

    private async void OnLiveToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || _camera == null) return;
        _live = LiveToggle.IsChecked == true;
        try
        {
            if (_live) await _host.Engine.Camera.StartGrabbingAsync(_camera);
            else await _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
        catch (Exception ex) { Log($"Live toggle failed: {ex.Message}"); }
    }

    private async void OnSingleFrame(object sender, RoutedEventArgs e)
    {
        if (_camera == null) { Log("No camera selected."); return; }
        if (_live) return;   // already streaming — the feed is current
        _snapPending = true;
        try { await _host.Engine.Camera.StartGrabbingAsync(_camera); }
        catch (Exception ex) { _snapPending = false; Log($"Single frame failed: {ex.Message}"); }
    }

    private void LoadExposureFromCamera()
    {
        if (_camera == null) return;
        try
        {
            var exp = _host.Engine.Camera.GetState(_camera).Exposure;
            AutoExposureCheck.IsChecked = exp.AutoExposure;
            ExposureBox.Text = exp.ExposureUs.ToString("0", CultureInfo.InvariantCulture);
        }
        catch { /* camera state not available yet */ }
    }

    private async void OnApplyExposure(object sender, RoutedEventArgs e)
    {
        if (_camera == null) { Log("No camera selected."); return; }
        try
        {
            var cur = _host.Engine.Camera.GetState(_camera).Exposure;
            var exp = cur with
            {
                AutoExposure = AutoExposureCheck.IsChecked == true,
                ExposureUs = ParseDouble(ExposureBox, cur.ExposureUs),
            };
            var r = await _host.Engine.Camera.ApplyExposureAsync(_camera, exp);
            Log(r.IsSuccess ? $"Exposure applied to {_camera}." : $"Exposure failed: {r.Message}");
        }
        catch (Exception ex) { Log($"Exposure error: {ex.Message}"); }
    }

    private async void OnApplyLighting(object sender, RoutedEventArgs e)
    {
        if (LightingCombo.SelectedItem is not string device) { Log("No lighting device."); return; }
        int intensity = ParseInt(LightingBox, 0);
        try
        {
            var r = await _host.Engine.Lighting.SetAllAsync(device, intensity);
            Log(r.IsSuccess ? $"Lighting {device} = {intensity}." : $"Lighting failed: {r.Message}");
        }
        catch (Exception ex) { Log($"Lighting error: {ex.Message}"); }
    }

    // --- position move ---

    private void RefreshPositions()
    {
        var names = _host.Process.GetPositionNames(_device);
        var selected = PositionCombo.SelectedItem as string;
        PositionCombo.ItemsSource = names;
        if (selected != null && names.Contains(selected)) PositionCombo.SelectedItem = selected;
    }

    private async void OnMoveTo(object sender, RoutedEventArgs e)
    {
        if (PositionCombo.SelectedItem is not string name) { Log("Pick a position first."); return; }
        try
        {
            Log($"Moving {_device} → {name}…");
            var r = await _host.Process.MoveToNamedPositionAsync(_device, name);
            Log(r.IsSuccess ? $"At {name}." : $"Move failed: {r.Message}");
        }
        catch (Exception ex) { Log($"Move error: {ex.Message}"); }
    }

    // --- pre-process filter chain ---

    // A filter edit restarts the debounce timer instead of running HALCON immediately.
    private void OnFilterChanged()
    {
        _previewTimer?.Stop();
        _previewTimer?.Start();
    }

    /// <summary>Preview button — pulls a fresh frame and runs the chain once on it.</summary>
    private async void OnPreviewFilter(object sender, RoutedEventArgs e)
    {
        var frame = await AcquireFreshFrameAsync();
        if (frame is not { } f) { Log("No frame — turn Live on or capture a Single frame."); return; }
        _lastFrame = f;
        await RunPreviewOnceAsync(f);
    }

    /// <summary>
    /// Runs the filter-chain preview against the latest frame, coalescing rapid
    /// edits (slider drags): at most one HALCON pass runs at a time, and a final
    /// pass runs after the last change.
    /// </summary>
    private async void SchedulePreview()
    {
        if (_lastFrame == null) return;
        if (_previewBusy) { _previewDirty = true; return; }
        _previewBusy = true;
        try
        {
            do
            {
                _previewDirty = false;
                if (_lastFrame is not { } frame) break;
                await RunPreviewOnceAsync(frame);
            } while (_previewDirty);
        }
        catch (Exception ex) { Log($"Filter error: {ex.Message}"); }
        finally { _previewBusy = false; }
    }

    /// <summary>Runs the filter chain once and updates the preview pane.</summary>
    private async Task RunPreviewOnceAsync(CameraFrame frame)
    {
        var r = await _host.Engine.Vision.ApplyFilterAsync(frame, BuildFilters(), _searchRoi);
        if (r.IsSuccess)
        {
            var c = r.Value;
            _lastFilterCrop = c;
            _filterBmp = Blit(FilterPreviewImage, _filterBmp, c.Width, c.Height, c.Channels, c.Data);
        }
        else { Log($"Filter failed: {r.Message}"); }
    }

    // --- live-feed overlay (search ROI + filter edges + detection geometry) ---

    private void OnOverlayCanvasSizeChanged(object sender, SizeChangedEventArgs e) => RedrawOverlayCanvas();

    private void OnOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (OverlayImage == null) return;
        var vis = OverlayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlayImage.Visibility = vis;
        OverlayCanvas.Visibility = vis;
        if (MatchCropOverlay != null) MatchCropOverlay.Visibility = vis;
    }

    private void ClearOverlay()
    {
        _overlayValues = null;
        _overlayCrop = null;
        _lastFilterCrop = null;
        OverlayImage.Source = null;
        OverlayCanvas.Children.Clear();
        MatchCropOverlay.Children.Clear();
    }

    private void RefreshOverlay()
    {
        BuildEdgesOverlay();
        RedrawOverlayCanvas();
    }

    /// <summary>Paints the filtered-image foreground as a translucent yellow layer (aligned via Uniform stretch).</summary>
    private async void BuildEdgesOverlay()
    {
        if (!_overlayHasFilters || _lastFilterCrop is not { Channels: 1 } c)
        {
            OverlayImage.Source = null;
            return;
        }
        int w = c.Width, h = c.Height;
        var data = c.Data;

        // Clip the painted area to the search ROI (filters only run there anyway).
        int x0 = 0, y0 = 0, x1 = w, y1 = h;
        if (_searchRoi is { } roi)
        {
            x0 = Math.Clamp(roi.X, 0, w); y0 = Math.Clamp(roi.Y, 0, h);
            x1 = Math.Clamp(roi.X + roi.Width, 0, w); y1 = Math.Clamp(roi.Y + roi.Height, 0, h);
        }

        // Reuse one buffer across detects (a fresh w*h*4 array each time is ~20 MB on the
        // LOH for multi-MP frames). The per-pixel build runs off the UI thread.
        int needed = w * h * 4;
        if (_overlayPx == null || _overlayPx.Length != needed) _overlayPx = new byte[needed];
        var px = _overlayPx;
        await Task.Run(() =>
        {
            Array.Clear(px, 0, px.Length);
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                for (int x = x0; x < x1; x++)
                {
                    if (data[row + x] < 128) continue;
                    int o = (row + x) * 4;
                    px[o] = 0; px[o + 1] = 213; px[o + 2] = 255; px[o + 3] = 150;   // BGRA: translucent yellow
                }
            }
        });

        if (_overlayBmp == null || _overlayBmp.PixelWidth != w || _overlayBmp.PixelHeight != h)
            _overlayBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        OverlayImage.Source = _overlayBmp;
        _overlayBmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
    }

    private void OnMatchCropSizeChanged(object sender, SizeChangedEventArgs e) => RedrawCropOverlay();

    /// <summary>Redraws the live-feed overlay (search ROI + detection geometry), then the crop overlay.</summary>
    private void RedrawOverlayCanvas()
    {
        OverlayCanvas.Children.Clear();
        if (_lastFrame is { } f && OverlayCanvas.ActualWidth > 0 && OverlayCanvas.ActualHeight > 0
            && f.Width > 0 && f.Height > 0)
        {
            double scale = Math.Min(OverlayCanvas.ActualWidth / f.Width, OverlayCanvas.ActualHeight / f.Height);
            double offX = (OverlayCanvas.ActualWidth - f.Width * scale) / 2;
            double offY = (OverlayCanvas.ActualHeight - f.Height * scale) / 2;
            Point P(double x, double y) => new(offX + x * scale, offY + y * scale);

            if (_searchRoi is { } roi)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(1, roi.Width * scale),
                    Height = Math.Max(1, roi.Height * scale),
                    Stroke = new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                };
                var tl = P(roi.X, roi.Y);
                Canvas.SetLeft(rect, tl.X);
                Canvas.SetTop(rect, tl.Y);
                OverlayCanvas.Children.Add(rect);
            }
            DrawGeometry(OverlayCanvas, P, scale);
        }
        RedrawCropOverlay();
    }

    /// <summary>Draws the detection geometry onto the matched-crop image (source → crop-local → display).</summary>
    private void RedrawCropOverlay()
    {
        MatchCropOverlay.Children.Clear();
        if (_overlayValues == null || _overlayCrop is not { } crop) return;
        double availW = MatchCropOverlay.ActualWidth, availH = MatchCropOverlay.ActualHeight;
        if (availW <= 0 || availH <= 0 || crop.Width <= 0 || crop.Height <= 0) return;

        double scale = Math.Min(availW / crop.Width, availH / crop.Height);
        double offX = (availW - crop.Width * scale) / 2, offY = (availH - crop.Height * scale) / 2;
        Point P(double x, double y) => new(offX + (x - crop.OriginX) * scale, offY + (y - crop.OriginY) * scale);
        DrawGeometry(MatchCropOverlay, P, scale);
    }

    /// <summary>Paints the detection geometry (line / circle / NCC box+arrow) onto a canvas, 1 px crisp.</summary>
    private void DrawGeometry(Canvas canvas, Func<double, double, Point> P, double scale)
    {
        var v = _overlayValues;
        if (v == null) return;
        var red = new SolidColorBrush(Color.FromRgb(255, 60, 60));
        var lime = new SolidColorBrush(Color.FromRgb(80, 255, 80));
        var orange = new SolidColorBrush(Color.FromRgb(255, 170, 0));

        switch (_overlayMethod)
        {
            case "line" when Has(v, "x1", "y1", "x2", "y2"):
            {
                var a = P(v["x1"], v["y1"]);
                var b = P(v["x2"], v["y2"]);
                canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = red, StrokeThickness = 1 });
                if (Has(v, "x", "y")) AddDot(canvas, P(v["x"], v["y"]), red);
                break;
            }
            case "circle" when Has(v, "x", "y", "radius"):
            {
                double rr = v["radius"] * scale;
                var ctr = P(v["x"], v["y"]);
                var el = new Ellipse { Width = 2 * rr, Height = 2 * rr, Stroke = lime, StrokeThickness = 1 };
                Canvas.SetLeft(el, ctr.X - rr);
                Canvas.SetTop(el, ctr.Y - rr);
                canvas.Children.Add(el);
                AddDot(canvas, ctr, lime);
                break;
            }
            default:   // ncc
                if (Has(v, "x", "y"))
                {
                    double ang = v.TryGetValue("angle", out var av) ? av : 0;
                    if (_overlayTemplateW > 0 && _overlayTemplateH > 0)
                    {
                        double bw = _overlayTemplateW * scale, bh = _overlayTemplateH * scale;
                        var box = new Rectangle { Width = bw, Height = bh, Stroke = orange, StrokeThickness = 1 };
                        var tl = P(v["x"] - _overlayTemplateW / 2.0, v["y"] - _overlayTemplateH / 2.0);
                        Canvas.SetLeft(box, tl.X);
                        Canvas.SetTop(box, tl.Y);
                        box.RenderTransform = new RotateTransform(ang, bw / 2, bh / 2);   // about box centre
                        canvas.Children.Add(box);
                    }
                    var anchor = Has(v, "anchorX", "anchorY") ? P(v["anchorX"], v["anchorY"]) : P(v["x"], v["y"]);
                    double rad = ang * Math.PI / 180.0;
                    canvas.Children.Add(new Line
                    {
                        X1 = anchor.X, Y1 = anchor.Y,
                        X2 = anchor.X + 30 * Math.Sin(rad), Y2 = anchor.Y - 30 * Math.Cos(rad),
                        Stroke = orange, StrokeThickness = 1,
                    });
                    AddDot(canvas, anchor, orange);
                }
                break;
        }
    }

    private static bool Has(Dictionary<string, double> v, params string[] keys) => keys.All(v.ContainsKey);

    private static void AddDot(Canvas canvas, Point p, Brush brush)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = brush, Stroke = Brushes.Black, StrokeThickness = 1 };
        Canvas.SetLeft(dot, p.X - 3.5);
        Canvas.SetTop(dot, p.Y - 3.5);
        canvas.Children.Add(dot);
    }

    // --- template ---

    private async void OnCreateTemplate(object sender, RoutedEventArgs e)
    {
        if (_lastFrame is not { } frame) { Log("Capture a frame first (Live or Single frame)."); return; }

        var snapshot = await FrameToBitmapAsync(frame);
        var picker = new RoiSelectWindow(snapshot, frame.Width, frame.Height) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Roi is not { } roi) return;

        try
        {
            var r = await _host.Process.CaptureTemplateAsync(BuildProfile(), frame, roi);
            if (!r.IsSuccess) { Log($"Template failed: {r.Message}"); return; }
            _template = r.Value;
            _loadingUi = true;
            AnchorRotationSlider.Value = 0;   // fresh template → 0° reference
            _loadingUi = false;
            ShowTemplateCrop(frame, roi);
            RedrawAnchorFrame();
            UpdateTemplateInfo();
            Log($"Template captured ({_template.Width}×{_template.Height}). Save the profile to keep it.");
        }
        catch (Exception ex) { Log($"Template error: {ex.Message}"); }
    }

    private void OnTemplateClick(object sender, MouseButtonEventArgs e)
    {
        if (AnchorToggle.IsChecked != true || _template is not { } t) return;

        double availW = TemplateImage.ActualWidth, availH = TemplateImage.ActualHeight;
        if (availW <= 0 || availH <= 0 || t.Width <= 0 || t.Height <= 0) return;

        // The bitmap is centred (uniform stretch); map the click's offset from the
        // control centre into template px around the template centre.
        double scale = Math.Min(availW / t.Width, availH / t.Height);
        if (scale <= 0) return;
        var p = e.GetPosition(TemplateImage);
        double ax = Math.Clamp(t.Width / 2.0 + (p.X - availW / 2.0) / scale, 0, t.Width);
        double ay = Math.Clamp(t.Height / 2.0 + (p.Y - availH / 2.0) / scale, 0, t.Height);
        _template = t with { AnchorX = ax, AnchorY = ay };
        RedrawAnchorFrame();
        UpdateTemplateInfo();
        Log($"Anchor set to ({ax:0},{ay:0}) in template.");
    }

    private void OnClearTemplate(object sender, RoutedEventArgs e)
    {
        _template = null;
        TemplateImage.Source = null;
        _templateBmp = null;
        TemplateOverlay.Children.Clear();
        NoTemplate.Visibility = Visibility.Visible;
        UpdateTemplateInfo();
        Log("Template cleared.");
    }

    private void ShowTemplateCrop(CameraFrame frame, RoiSpec roi)
    {
        var data = CropFrame(frame, roi, out int w, out int h, out int ch);
        _templateBmp = Blit(TemplateImage, _templateBmp, w, h, ch, data);
        NoTemplate.Visibility = Visibility.Collapsed;
    }

    private void OnTemplateOverlaySizeChanged(object sender, SizeChangedEventArgs e) => RedrawAnchorFrame();

    private void OnAnchorRotationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingUi || _template is not { } t) return;
        _template = t with { Rotation = AnchorRotationSlider.Value };
        RedrawAnchorFrame();
        UpdateTemplateInfo();
    }

    /// <summary>Draws the anchor coordinate frame (point + Y/X axes rotated by the anchor angle).</summary>
    private void RedrawAnchorFrame()
    {
        TemplateOverlay.Children.Clear();
        if (_template is not { } t || TemplateImage.Source == null) return;
        double availW = TemplateOverlay.ActualWidth, availH = TemplateOverlay.ActualHeight;
        if (availW <= 0 || availH <= 0 || t.Width <= 0 || t.Height <= 0) return;

        double scale = Math.Min(availW / t.Width, availH / t.Height);
        double offX = (availW - t.Width * scale) / 2, offY = (availH - t.Height * scale) / 2;
        double cx = offX + t.AnchorX * scale, cy = offY + t.AnchorY * scale;

        double rad = t.Rotation * Math.PI / 180.0;
        double s = Math.Sin(rad), c = Math.Cos(rad);
        const double L = 26, Lx = 16;
        var brush = (SolidColorBrush)FindResource("Roi");

        // Y axis (up at 0°) and X axis (right), rotated by the anchor angle.
        TemplateOverlay.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = cx + L * s, Y2 = cy - L * c, Stroke = brush, StrokeThickness = 2 });
        TemplateOverlay.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = cx + Lx * c, Y2 = cy + Lx * s, Stroke = brush, StrokeThickness = 1.5, Opacity = 0.7 });
        var dot = new Ellipse { Width = 10, Height = 10, Stroke = brush, StrokeThickness = 2, Fill = Brushes.Transparent };
        Canvas.SetLeft(dot, cx - 5);
        Canvas.SetTop(dot, cy - 5);
        TemplateOverlay.Children.Add(dot);
    }

    private void UpdateTemplateInfo()
        => TemplateInfoText.Text = _template is { } t
            ? $"template {t.Uid[..Math.Min(t.Uid.Length, 12)]}…  {t.Width}×{t.Height}px  " +
              $"anchor ({t.AnchorX:0},{t.AnchorY:0})  rot {t.Rotation:0}°"
            : "no template";

    // --- detect ---

    private async void OnDetect(object sender, RoutedEventArgs e)
    {
        if (MethodKey() == "ncc" && _template == null) { Log("Teach a template first."); return; }
        DetectButton.IsEnabled = false;
        try
        {
            // Pull the newest frame and re-run the full pipeline so the result reflects
            // the current scene (and the filter preview agrees with what was detected).
            var frame = await AcquireFreshFrameAsync();
            if (frame is not { } f) { Log("No frame — turn Live on or capture a Single frame."); return; }
            _lastFrame = f;
            await RunPreviewOnceAsync(f);

            var r = await _host.Process.RunVisionProfileAsync(BuildProfile(), f);
            _results.Clear();
            if (!r.IsSuccess) { Log($"Detect failed: {r.Message}"); return; }

            var res = r.Value;
            _overlayHasFilters = BuildFilters().Count > 0;
            if (!res.Found)
            {
                MatchCrop1.Source = null;
                _cropBmp = null;
                _overlayValues = null;
                _overlayCrop = null;
                RefreshOverlay();
                Log($"{MethodKey().ToUpperInvariant()}: no detection.");
                return;
            }

            foreach (var v in res.Values)
                _results.Add(new ResultRow { Key = v.Key, Value = v.Value.ToString("0.###", CultureInfo.InvariantCulture) });
            if (res.Crop is { } c)
                _cropBmp = Blit(MatchCrop1, _cropBmp, c.Width, c.Height, c.Channels, c.Data);

            // Capture the result for the live-feed + crop overlays.
            _overlayCrop = res.Crop;
            _overlayValues = res.Values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            _overlayMethod = MethodKey();
            _overlayTemplateW = _template?.Width ?? 0;
            _overlayTemplateH = _template?.Height ?? 0;
            RefreshOverlay();
            Log($"{MethodKey().ToUpperInvariant()} detected — {res.Values.Count} value(s).");
        }
        catch (Exception ex) { Log($"Detect error: {ex.Message}"); }
        finally { DetectButton.IsEnabled = true; }
    }

    // --- profile load / save / new / history ---

    private void RefreshProfiles()
    {
        var names = _host.Process.ListVisionProfiles();
        var text = ProfileCombo.Text;
        ProfileCombo.ItemsSource = names;
        ProfileCombo.Text = text;
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var win = new SerialEntryWindow("New vision profile", "Enter a name for the new profile:", string.Empty)
        {
            Owner = Window.GetWindow(this),
        };
        if (win.ShowDialog() != true) return;
        var name = win.Serial;
        if (string.IsNullOrWhiteSpace(name)) return;

        _uid = string.Empty;
        _template = null;
        ProfileCombo.Text = name;
        OnClearTemplate(sender, e);
        ClearOverlay();
        Log($"New profile '{name}' — set fields, capture a template, then Save.");
    }

    private async void OnLoad(object sender, RoutedEventArgs e)
    {
        var name = (ProfileCombo.SelectedItem as string ?? ProfileCombo.Text)?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { Log("Pick a profile to load."); return; }

        var profile = _host.Process.LoadVisionProfile(name);
        if (profile == null) { Log($"Profile '{name}' not found."); return; }
        ApplyProfileToUi(profile);
        Log($"Loaded vision profile '{name}'.");
        await ApplyProfileHardwareAsync(profile);   // push the saved exposure + lighting to the camera
    }

    /// <summary>Applies a loaded profile's camera exposure + lighting to the hardware.</summary>
    private async Task ApplyProfileHardwareAsync(VisionProfile p)
    {
        if (!string.IsNullOrEmpty(p.CameraId) && _host.Engine.Camera.CameraIds.Contains(p.CameraId))
        {
            try
            {
                var cur = _host.Engine.Camera.GetState(p.CameraId).Exposure;
                var exp = cur with { AutoExposure = p.AutoExposure, ExposureUs = p.ExposureUs };
                var r = await _host.Engine.Camera.ApplyExposureAsync(p.CameraId, exp);
                Log(r.IsSuccess
                    ? $"Profile exposure applied to {p.CameraId} ({(p.AutoExposure ? "auto" : $"{p.ExposureUs:0} µs")})."
                    : $"Exposure failed: {r.Message}");
            }
            catch (Exception ex) { Log($"Exposure error: {ex.Message}"); }
        }

        if (!string.IsNullOrEmpty(p.LightingDevice) && _host.Engine.Lighting.DeviceNames.Contains(p.LightingDevice))
        {
            try
            {
                var r = await _host.Engine.Lighting.SetAllAsync(p.LightingDevice, p.LightingIntensity);
                Log(r.IsSuccess
                    ? $"Profile lighting applied: {p.LightingDevice} = {p.LightingIntensity}."
                    : $"Lighting failed: {r.Message}");
            }
            catch (Exception ex) { Log($"Lighting error: {ex.Message}"); }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Snapshot the camera's live exposure into the UI so the profile records the exact
        // condition it was tuned under (auto flag + µs); the run-time apply then reproduces it.
        LoadExposureFromCamera();

        var profile = BuildProfile();
        if (string.IsNullOrWhiteSpace(profile.Name)) { Log("Give the profile a name first."); return; }

        var r = _host.Process.SaveVisionProfile(profile);
        if (!r.IsSuccess) { Log($"Save failed: {r.Message}"); return; }
        _uid = r.Value.Uid;
        RefreshProfiles();
        ProfileCombo.Text = r.Value.Name;
        Log($"Saved vision profile '{r.Value.Name}'.");
    }

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_uid)) { Log("Save the profile first to start its history."); return; }
        new VisionHistoryWindow(_host, _uid, ProfileCombo.Text?.Trim() ?? string.Empty)
        {
            Owner = Window.GetWindow(this),
        }.ShowDialog();
    }

    private void ApplyProfileToUi(VisionProfile p)
    {
        _loadingUi = true;
        _uid = p.Uid;
        _device = string.IsNullOrWhiteSpace(p.Device) ? _device : p.Device;
        ProfileCombo.Text = p.Name;

        RefreshPositions();
        if (!string.IsNullOrEmpty(p.PositionName)) PositionCombo.SelectedItem = p.PositionName;

        if (!string.IsNullOrEmpty(p.CameraId) && _host.Engine.Camera.CameraIds.Contains(p.CameraId))
        {
            CameraCombo.SelectedItem = p.CameraId;
            _camera = p.CameraId;
        }
        AutoExposureCheck.IsChecked = p.AutoExposure;
        ExposureBox.Text = p.ExposureUs.ToString("0", CultureInfo.InvariantCulture);

        if (!string.IsNullOrEmpty(p.LightingDevice)) LightingCombo.SelectedItem = p.LightingDevice;
        LightingBox.Text = p.LightingIntensity.ToString(CultureInfo.InvariantCulture);

        var filters = p.Filters;
        Filter1.Value = filters.Count > 0 ? filters[0] : new VisionFilter();
        Filter2.Value = filters.Count > 1 ? filters[1] : new VisionFilter();
        Filter3.Value = filters.Count > 2 ? filters[2] : new VisionFilter();

        MethodCombo.SelectedIndex = (p.DetectionMethod ?? "ncc").ToLowerInvariant() switch
        {
            "circle" => 1,
            "line" => 2,
            _ => 0,
        };
        _searchRoi = p.SearchRoi;
        UpdateSearchRoiText();
        CircleMinRadiusBox.Text = p.Circle.MinRadius.ToString("0.##", CultureInfo.InvariantCulture);
        CircleMaxRadiusBox.Text = p.Circle.MaxRadius.ToString("0.##", CultureInfo.InvariantCulture);
        CircleMinCircBox.Text = p.Circle.MinCircularity.ToString("0.##", CultureInfo.InvariantCulture);
        LineMinLengthBox.Text = p.Line.MinLength.ToString("0.##", CultureInfo.InvariantCulture);
        LineDirectionCombo.SelectedIndex = p.Line.Direction switch
        {
            LineDirection.Horizontal => 1,
            LineDirection.Vertical => 2,
            _ => 0,
        };
        LinePolarityCombo.SelectedIndex = p.Line.Polarity switch
        {
            LinePolarity.DarkToBright => 1,
            LinePolarity.BrightToDark => 2,
            _ => 0,
        };
        LineScanCombo.SelectedIndex = p.Line.ScanOrder switch
        {
            LineScanOrder.Backward => 1,
            LineScanOrder.CenterOut => 2,
            _ => 0,
        };

        MinScoreBox.Text = p.MinScore.ToString("0.00", CultureInfo.InvariantCulture);
        NccAngleRangeBox.Text = p.NccAngleRange.ToString("0.##", CultureInfo.InvariantCulture);
        AnchorRotationSlider.Value = Math.Clamp(p.Template?.Rotation ?? 0, -180, 180);

        _template = p.Template;
        LoadTemplateImageFromDisk(p.Template?.Uid);
        UpdateTemplateInfo();
        _loadingUi = false;
    }

    private void LoadTemplateImageFromDisk(string? templateUid)
    {
        _templateBmp = null;
        if (string.IsNullOrEmpty(templateUid))
        {
            TemplateImage.Source = null;
            TemplateOverlay.Children.Clear();
            NoTemplate.Visibility = Visibility.Visible;
            return;
        }

        var path = _host.Process.GetTemplateImagePath(templateUid);
        if (path == null)
        {
            TemplateImage.Source = null;
            NoTemplate.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;   // don't hold a file lock
            img.UriSource = new Uri(path);
            img.EndInit();
            TemplateImage.Source = img;
            NoTemplate.Visibility = Visibility.Collapsed;
            RedrawAnchorFrame();
        }
        catch (Exception ex) { Log($"Template image load failed: {ex.Message}"); }
    }

    // --- builders ---

    /// <summary>The ordered, non-empty filter chain from the three rows.</summary>
    private IReadOnlyList<VisionFilter> BuildFilters()
        => new[] { Filter1.Value, Filter2.Value, Filter3.Value }
            .Where(f => f.Kind != VisionFilterKind.None)
            .ToArray();

    private VisionProfile BuildProfile() => new()
    {
        Uid = _uid,
        Name = ProfileCombo.Text?.Trim() ?? string.Empty,
        Device = _device,
        PositionName = PositionCombo.SelectedItem as string ?? string.Empty,
        CameraId = _camera ?? string.Empty,
        AutoExposure = AutoExposureCheck.IsChecked == true,
        ExposureUs = ParseDouble(ExposureBox, 0),
        LightingDevice = LightingCombo.SelectedItem as string ?? string.Empty,
        LightingIntensity = ParseInt(LightingBox, 0),
        Filters = BuildFilters(),
        DetectionMethod = MethodKey(),
        MinScore = ParseDouble(MinScoreBox, 0.7),
        NccAngleRange = ParseDouble(NccAngleRangeBox, 180),
        SearchRoi = _searchRoi,
        Circle = new CircleParams(
            ParseDouble(CircleMinRadiusBox, 5), ParseDouble(CircleMaxRadiusBox, 200),
            ParseDouble(CircleMinCircBox, 0.7)),
        Line = new LineParams(
            ParseDouble(LineMinLengthBox, 20),
            LineDirectionCombo.SelectedIndex switch
            {
                1 => LineDirection.Horizontal,
                2 => LineDirection.Vertical,
                _ => LineDirection.Any,
            },
            LinePolarityCombo.SelectedIndex switch
            {
                1 => LinePolarity.DarkToBright,
                2 => LinePolarity.BrightToDark,
                _ => LinePolarity.Any,
            },
            LineScanCombo.SelectedIndex switch
            {
                1 => LineScanOrder.Backward,
                2 => LineScanOrder.CenterOut,
                _ => LineScanOrder.Forward,
            }),
        Template = _template,
    };

    // --- helpers ---

    /// <summary>Creates/reuses a WriteableBitmap for an Image and blits raw pixel bytes into it.</summary>
    private static WriteableBitmap? Blit(Image target, WriteableBitmap? bmp, int w, int h, int channels, byte[] data)
    {
        PixelFormat? format = channels switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Bgr24,
            4 => PixelFormats.Bgra32,
            _ => null,
        };
        if (format == null || w <= 0 || h <= 0) return bmp;

        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h || bmp.Format != format.Value)
        {
            bmp = new WriteableBitmap(w, h, 96, 96, format.Value, null);
            target.Source = bmp;
        }
        try { bmp.WritePixels(new Int32Rect(0, 0, w, h), data, w * channels, 0); }
        catch { /* malformed buffer — skip */ }
        return bmp;
    }

    /// <summary>A frozen snapshot of a frame, for the modal ROI picker.</summary>
    private static BitmapSource FrameToBitmap(CameraFrame f)
    {
        PixelFormat format = f.Channels switch
        {
            1 => PixelFormats.Gray8,
            4 => PixelFormats.Bgra32,
            _ => PixelFormats.Bgr24,
        };
        var bmp = BitmapSource.Create(f.Width, f.Height, 96, 96, format, null, f.Data, f.Width * f.Channels);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Builds the frozen snapshot off the UI thread (multi-MP frames are a copy + Freeze).</summary>
    private static Task<BitmapSource> FrameToBitmapAsync(CameraFrame f) => Task.Run(() => FrameToBitmap(f));

    /// <summary>Copies the ROI sub-rect out of a frame buffer (clamped) into a tight byte array.</summary>
    private static byte[] CropFrame(CameraFrame f, RoiSpec roi, out int w, out int h, out int channels)
    {
        channels = f.Channels;
        int x = Math.Clamp(roi.X, 0, Math.Max(0, f.Width - 1));
        int y = Math.Clamp(roi.Y, 0, Math.Max(0, f.Height - 1));
        w = Math.Clamp(roi.Width, 1, f.Width - x);
        h = Math.Clamp(roi.Height, 1, f.Height - y);

        int srcStride = f.Width * channels;
        int dstStride = w * channels;
        var dst = new byte[dstStride * h];
        for (int row = 0; row < h; row++)
            Array.Copy(f.Data, (y + row) * srcStride + x * channels, dst, row * dstStride, dstStride);
        return dst;
    }

    private static int ParseInt(TextBox t, int fallback)
        => int.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDouble(TextBox t, double fallback)
        => double.TryParse(t.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Shows a one-line status under the results grid (full log goes to the app log).</summary>
    private void Log(string message)
    {
        _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Vision", message));
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => StatusText.Text = message); return; }
        StatusText.Text = message;
    }
}
