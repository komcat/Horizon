using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.Workflow;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The Register Gripped Object block's <b>Outline helper</b>: a live view on which the engineer
/// sets the snapshot box around the crosshair (default 300 × 300 px) and the filter chain (the
/// Vision helper's binary threshold / canny rows), and sees the result two ways — black / white
/// in the side pane, and its white pixels painted yellow over the live view, as the block will
/// draw them. <b>Save to block</b> stores the box size + filters. See <c>docs/beacon-design.md</c>.
/// </summary>
public partial class GrippedOutlineHelperWindow : Window
{
    private readonly EngineHost _host;
    private readonly string _camera;
    private readonly Action<int, int, string> _save;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _previewTimer;
    private readonly object _frameLock = new();
    private CameraFrame? _pendingFrame;
    private bool _renderScheduled;
    private CameraFrame? _lastFrame;
    private WriteableBitmap? _bmp;
    private RoiSpec? _shownRoi;     // the box the yellow image currently shows
    private bool _leased;
    private bool _closed;
    private bool _previewBusy;
    private bool _loading = true;
    private readonly System.Windows.Shapes.Rectangle[] _handles = new System.Windows.Shapes.Rectangle[4];   // TL, TR, BL, BR
    private const double HandleSize = 12;

    /// <param name="camera">The camera the block will snapshot (the tool registration's).</param>
    /// <param name="save">Receives box width, box height and the filters JSON on Save to block.</param>
    public GrippedOutlineHelperWindow(
        EngineHost host, string camera, int roiWidth, int roiHeight, string filtersJson, Action<int, int, string> save)
    {
        _host = host;
        _camera = camera;
        _save = save;
        _dispatcher = Dispatcher.CurrentDispatcher;
        InitializeComponent();

        CameraText.Text = camera;
        WidthBox.Text = roiWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = roiHeight.ToString(CultureInfo.InvariantCulture);
        var filters = GrippedOutlineOptions.ParseFilters(filtersJson);
        Filter1.Value = filters.Count > 0 ? filters[0] : new VisionFilter();
        Filter2.Value = filters.Count > 1 ? filters[1] : new VisionFilter();
        Filter3.Value = filters.Count > 2 ? filters[2] : new VisionFilter();
        _loading = false;
        CreateHandles();

        // The preview follows the live image: one filter pass at a time, a few times a second.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += (_, _) => RunPreview();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host.Engine.Camera.FrameReady += OnFrameReady;
        _previewTimer.Start();
        try
        {
            var r = await _host.Engine.Camera.StartGrabbingAsync(_camera);
            _leased = r.IsSuccess;
            if (!r.IsSuccess) NoVideo.Text = $"Camera {_camera}: {r.Message}";
        }
        catch (Exception ex) { NoVideo.Text = $"Camera {_camera}: {ex.Message}"; }

        // Closed while the grab was starting — give the lease straight back.
        if (_closed && _leased)
        {
            _leased = false;
            _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _previewTimer.Stop();
        _host.Engine.Camera.FrameReady -= OnFrameReady;
        if (_leased)
        {
            _leased = false;
            _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
    }

    // --- feed ---

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
        if (frame == null || _closed) return;

        PixelFormat? format = frame.Channels switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Bgr24,
            4 => PixelFormats.Bgra32,
            _ => null,
        };
        if (format == null || frame.Width <= 0 || frame.Height <= 0) return;

        bool sizeChanged = _lastFrame == null || _lastFrame.Width != frame.Width || _lastFrame.Height != frame.Height;
        if (_bmp == null || _bmp.PixelWidth != frame.Width || _bmp.PixelHeight != frame.Height || _bmp.Format != format.Value)
        {
            _bmp = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format.Value, null);
            FeedImage.Source = _bmp;
        }
        try { _bmp.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, frame.Width * frame.Channels, 0); }
        catch (ArgumentException) { return; }   // malformed buffer — skip the frame
        _lastFrame = frame;
        NoVideo.Visibility = Visibility.Collapsed;
        if (sizeChanged) LayoutOverlay();
    }

    // --- settings ---

    private static int ParseSize(TextBox box)
        => int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, GrippedOutlineOptions.MinSize, GrippedOutlineOptions.MaxSize)
            : GrippedOutlineOptions.DefaultSize;

    private IReadOnlyList<VisionFilter> Filters()
        => new[] { Filter1.Value, Filter2.Value, Filter3.Value }
            .Where(f => f.Kind != VisionFilterKind.None)
            .ToArray();

    private GrippedOutlineOptions Options() => new(ParseSize(WidthBox), ParseSize(HeightBox), Filters());

    private void OnBoxSizeChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        LayoutOverlay();
    }

    private void OnDefaultSize(object sender, RoutedEventArgs e)
    {
        WidthBox.Text = HeightBox.Text = GrippedOutlineOptions.DefaultSize.ToString(CultureInfo.InvariantCulture);
    }

    private void OnOverlayToggled(object sender, RoutedEventArgs e)
        => YellowImage.Visibility = OverlayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => LayoutOverlay();

    // --- preview ---

    /// <summary>Filters the box of the newest frame and shows it black / white and as the yellow overlay.</summary>
    private async void RunPreview()
    {
        if (_previewBusy || _closed || _lastFrame is not { } frame) return;
        _previewBusy = true;
        try
        {
            var options = Options();
            var r = await _host.Process.PreviewGrippedOutlineAsync(frame, options);
            if (_closed) return;
            if (!r.IsSuccess)
            {
                StatusText.Text = $"Filter failed: {r.Message}";
                return;
            }

            var outline = r.Value;
            var mono = BitmapSource.Create(outline.Width, outline.Height, 96, 96, PixelFormats.Gray8, null, outline.Mask, outline.Width);
            mono.Freeze();
            ResultImage.Source = mono;
            YellowImage.Source = ToolOverlayView.OutlineImage(outline);
            _shownRoi = options.RoiFor(frame.Width, frame.Height);
            LayoutOverlay();

            int white = outline.Mask.Count(b => b != 0);
            double percent = outline.Mask.Length > 0 ? 100.0 * white / outline.Mask.Length : 0;
            StatusText.Text = options.Filters.Count == 0
                ? $"No filter chosen — pick Binary threshold or Canny edge. Box {outline.Width} × {outline.Height} px."
                : $"Box {outline.Width} × {outline.Height} px — {percent:0.0}% white.";
        }
        catch (Exception ex) { StatusText.Text = $"Preview error: {ex.Message}"; }
        finally { _previewBusy = false; }
    }

    /// <summary>Places the dashed box (from the size fields) and the yellow image (from the last preview) over the feed.</summary>
    private void LayoutOverlay()
    {
        if (_lastFrame is not { } frame || OverlayCanvas.ActualWidth <= 0 || OverlayCanvas.ActualHeight <= 0) return;
        double scale = Math.Min(OverlayCanvas.ActualWidth / frame.Width, OverlayCanvas.ActualHeight / frame.Height);
        if (scale <= 0) return;
        double offX = (OverlayCanvas.ActualWidth - frame.Width * scale) / 2;
        double offY = (OverlayCanvas.ActualHeight - frame.Height * scale) / 2;

        void Place(FrameworkElement element, RoiSpec roi)
        {
            element.Width = Math.Max(1, roi.Width * scale);
            element.Height = Math.Max(1, roi.Height * scale);
            Canvas.SetLeft(element, offX + roi.X * scale);
            Canvas.SetTop(element, offY + roi.Y * scale);
        }

        var box = Options().RoiFor(frame.Width, frame.Height);
        Place(BoxRect, box);
        BoxRect.Visibility = Visibility.Visible;

        // Corner handles: TL, TR, BL, BR.
        double left = offX + box.X * scale, top = offY + box.Y * scale;
        double right = left + box.Width * scale, bottom = top + box.Height * scale;
        var corners = new[] { (left, top), (right, top), (left, bottom), (right, bottom) };
        for (int i = 0; i < _handles.Length; i++)
        {
            Canvas.SetLeft(_handles[i], corners[i].Item1 - HandleSize / 2);
            Canvas.SetTop(_handles[i], corners[i].Item2 - HandleSize / 2);
            _handles[i].Visibility = Visibility.Visible;
        }
        if (_shownRoi is { } shown) Place(YellowImage, shown);
    }

    // --- corner handles: drag to resize (the box stays centred on the crosshair) ---

    private void CreateHandles()
    {
        for (int i = 0; i < _handles.Length; i++)
        {
            var handle = new System.Windows.Shapes.Rectangle
            {
                Width = HandleSize, Height = HandleSize,
                Fill = (Brush)FindResource("Roi"), Stroke = Brushes.Black, StrokeThickness = 1,
                // TL / BR drag along one diagonal, TR / BL along the other.
                Cursor = i is 0 or 3 ? System.Windows.Input.Cursors.SizeNWSE : System.Windows.Input.Cursors.SizeNESW,
                Visibility = Visibility.Collapsed,
                ToolTip = "Drag to resize the box",
            };
            handle.MouseLeftButtonDown += OnHandleDown;
            handle.MouseMove += OnHandleMove;
            handle.MouseLeftButtonUp += OnHandleUp;
            _handles[i] = handle;
            OverlayCanvas.Children.Add(handle);
        }
    }

    private void OnHandleDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not UIElement handle) return;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not UIElement handle || !handle.IsMouseCaptured) return;
        if (_lastFrame is not { } frame || OverlayCanvas.ActualWidth <= 0 || OverlayCanvas.ActualHeight <= 0) return;
        double scale = Math.Min(OverlayCanvas.ActualWidth / frame.Width, OverlayCanvas.ActualHeight / frame.Height);
        if (scale <= 0) return;

        // The box is centred on the image centre (the crosshair), so the corner's distance from the
        // centre is half the box, on each axis.
        var p = e.GetPosition(OverlayCanvas);
        double halfW = Math.Abs(p.X - OverlayCanvas.ActualWidth / 2) / scale;
        double halfH = Math.Abs(p.Y - OverlayCanvas.ActualHeight / 2) / scale;
        int w = Math.Clamp((int)Math.Round(halfW * 2), GrippedOutlineOptions.MinSize, Math.Min(GrippedOutlineOptions.MaxSize, frame.Width));
        int h = Math.Clamp((int)Math.Round(halfH * 2), GrippedOutlineOptions.MinSize, Math.Min(GrippedOutlineOptions.MaxSize, frame.Height));

        _loading = true;   // one layout for both fields
        WidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = h.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        LayoutOverlay();
    }

    private void OnHandleUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is UIElement handle && handle.IsMouseCaptured) handle.ReleaseMouseCapture();
    }

    // --- actions ---

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var options = Options();
        _save(options.RoiWidth, options.RoiHeight, GrippedOutlineOptions.SerializeFilters(options.Filters));
        StatusText.Text = options.Filters.Count == 0
            ? "Saved to the block — but with no filter the snapshot is not black / white. Pick a filter and save again."
            : "Saved to the block, outline switched on. Save the program to keep it.";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
