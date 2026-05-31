using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// The Run page — a security-camera-style wall. The "main" camera plays large;
/// the rest are live thumbnails. Click a thumbnail to promote it to main and
/// demote the previous main back to the strip.
///
/// Every configured camera grabs continuously while the page is active. A single
/// <see cref="ICameraFacet.FrameReady"/> subscription routes each frame (by
/// <see cref="CameraFrame.Camera"/>) into that camera's reused
/// <see cref="WriteableBitmap"/>; the main <see cref="Image"/> and each thumbnail
/// just point their Source at the relevant bitmap, so promoting is a Source swap
/// with no pipeline disruption.
/// </summary>
public partial class RunView : UserControl
{
    private const double ThumbWidth = 150;
    private const double ThumbHeight = 104;

    /// <summary>Side length of the centre ROI sampled for the Zoom2x inset, in source pixels.</summary>
    private const int RoiSize = 200;
    private const int MaxLogLines = 200;

    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyList<string> _cameras;

    private readonly object _frameLock = new();
    private readonly Dictionary<string, CameraFrame> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WriteableBitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _thumbImages = new(StringComparer.OrdinalIgnoreCase);
    private bool _renderScheduled;

    private WriteableBitmap? _zoomBitmap;
    private readonly Queue<string> _logLines = new();

    private string? _mainCamera;
    private bool _active;
    private bool _subscribed;
    private bool _logSubscribed;

    public RunView(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        InitializeComponent();

        _cameras = host.Engine.Camera.CameraIds;
        _mainCamera = _cameras.Count > 0 ? _cameras[0] : null;
        MainLabel.Text = _mainCamera ?? "no cameras";
        BuildThumbnails();

        StepsPanel.Attach(host);   // process panel logs + STOP via the engine host
        StepsPanel.InspectMotionRequested += OnInspectMotionRequested;
    }

    /// <summary>Shows the motion-inspector grid (over the camera column) with the supplied position rows.</summary>
    private void OnInspectMotionRequested(object? sender, IReadOnlyList<MotionInspectRow> rows)
    {
        InspectorGrid.ItemsSource = rows;
        InspectorPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the inspector, returning to the live camera feed.</summary>
    private void OnCloseInspector(object sender, RoutedEventArgs e)
        => InspectorPanel.Visibility = Visibility.Collapsed;

    /// <summary>Per-row ".." button — opens that position's editor (logic owned by the process panel).</summary>
    private void OnEditRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MotionInspectRow row })
            row.Edit?.Invoke();
    }

    /// <summary>
    /// Starts all cameras grabbing and begins routing frames. Called by the shell
    /// when the Run page is shown.
    /// </summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;

        // Fire-and-forget: each camera grabs independently. Already-grabbing
        // cameras are a harmless no-op.
        foreach (var cam in _cameras)
            _ = _host.Engine.Camera.StartGrabbingAsync(cam);

        if (!_subscribed)
        {
            _host.Engine.Camera.FrameReady += OnFrameReady;
            _subscribed = true;
        }

        // Show the current log buffer, then follow new lines live.
        LoadLog();
        if (!_logSubscribed)
        {
            _host.Log.Appended += OnLogAppended;
            _logSubscribed = true;
        }
    }

    /// <summary>
    /// Stops routing frames and stops grabbing on every camera, returning them to
    /// idle. Called when navigating away. Note: this also ends the stream other
    /// frame consumers (e.g. the Vision facet) receive while off the Run page.
    /// </summary>
    public void Deactivate()
    {
        if (!_active) return;   // idempotent — only release the leases this page acquired

        if (_subscribed)
        {
            _host.Engine.Camera.FrameReady -= OnFrameReady;
            _subscribed = false;
        }

        // Fire-and-forget: release this page's grab lease on each camera.
        foreach (var cam in _cameras)
            _ = _host.Engine.Camera.StopGrabbingAsync(cam);

        if (_logSubscribed)
        {
            _host.Log.Appended -= OnLogAppended;
            _logSubscribed = false;
        }

        _active = false;
    }

    // --- frame routing ---

    private void OnFrameReady(CameraFrame frame)
    {
        // Keep only the newest frame per camera; coalesce to one queued render.
        lock (_frameLock)
        {
            _pending[frame.Camera] = frame;
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        _dispatcher.BeginInvoke(RenderPending);
    }

    private void RenderPending()
    {
        List<CameraFrame> frames;
        lock (_frameLock)
        {
            _renderScheduled = false;
            frames = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var frame in frames)
            RenderFrame(frame);
    }

    private void RenderFrame(CameraFrame frame)
    {
        PixelFormat? format = frame.Channels switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Bgr24,
            4 => PixelFormats.Bgra32,
            _ => null,
        };
        if (format == null) return;

        bool created = false;
        if (!_bitmaps.TryGetValue(frame.Camera, out var bmp) ||
            bmp.PixelWidth != frame.Width || bmp.PixelHeight != frame.Height ||
            bmp.Format != format.Value)
        {
            bmp = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format.Value, null);
            _bitmaps[frame.Camera] = bmp;
            created = true;
        }

        try
        {
            bmp.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Data, frame.Width * frame.Channels, 0);
        }
        catch
        {
            return;   // malformed frame — skip
        }

        // First frame for this camera: point its display control at the bitmap.
        if (created)
            AttachSource(frame.Camera);

        // The main camera also drives the Zoom2x inset and the ROI box.
        if (string.Equals(frame.Camera, _mainCamera, StringComparison.OrdinalIgnoreCase))
        {
            UpdateZoom(frame, format.Value);
            UpdateRoiBox(frame);
        }
    }

    /// <summary>Blits the centre <see cref="RoiSize"/>² region of the frame into the zoom bitmap.</summary>
    private void UpdateZoom(CameraFrame frame, PixelFormat format)
    {
        int roiW = Math.Min(RoiSize, frame.Width);
        int roiH = Math.Min(RoiSize, frame.Height);
        if (roiW <= 0 || roiH <= 0) return;

        int x0 = (frame.Width - roiW) / 2;
        int y0 = (frame.Height - roiH) / 2;

        if (_zoomBitmap == null || _zoomBitmap.PixelWidth != roiW ||
            _zoomBitmap.PixelHeight != roiH || _zoomBitmap.Format != format)
        {
            _zoomBitmap = new WriteableBitmap(roiW, roiH, 96, 96, format, null);
            ZoomImage.Source = _zoomBitmap;
        }

        // Copy the sub-rect straight out of the source buffer using its stride —
        // no intermediate allocation.
        _zoomBitmap.WritePixels(
            new Int32Rect(x0, y0, roiW, roiH),
            frame.Data, frame.Width * frame.Channels, 0, 0);
    }

    /// <summary>Sizes the ROI box to <see cref="RoiSize"/>² source px at the feed's current display scale.</summary>
    private void UpdateRoiBox(CameraFrame frame)
    {
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        if (availW <= 0 || availH <= 0 || frame.Width <= 0 || frame.Height <= 0)
            return;   // not laid out yet — next frame will size it

        // Uniform-stretch scale: source px → display px.
        double scale = Math.Min(availW / frame.Width, availH / frame.Height);
        RoiBox.Width = Math.Min(RoiSize, frame.Width) * scale;
        RoiBox.Height = Math.Min(RoiSize, frame.Height) * scale;
        RoiBox.Visibility = Visibility.Visible;
    }

    /// <summary>Points whichever control currently shows <paramref name="camera"/> at its bitmap.</summary>
    private void AttachSource(string camera)
    {
        if (!_bitmaps.TryGetValue(camera, out var bmp)) return;

        if (string.Equals(camera, _mainCamera, StringComparison.OrdinalIgnoreCase))
        {
            MainImage.Source = bmp;
            NoVideo.Visibility = Visibility.Collapsed;
        }
        else if (_thumbImages.TryGetValue(camera, out var img))
        {
            img.Source = bmp;
        }
    }

    // --- layout / swap ---

    private void BuildThumbnails()
    {
        ThumbStrip.Children.Clear();
        _thumbImages.Clear();

        foreach (var cam in _cameras)
        {
            if (string.Equals(cam, _mainCamera, StringComparison.OrdinalIgnoreCase))
                continue;

            var img = new Image { Stretch = Stretch.Uniform };
            if (_bitmaps.TryGetValue(cam, out var bmp))
                img.Source = bmp;
            _thumbImages[cam] = img;

            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(3),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = cam,
                    Foreground = Brushes.White,   // over the fixed-black video overlay
                    FontSize = 11,
                },
            };

            var tile = new Border
            {
                Width = ThumbWidth,
                Height = ThumbHeight,
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Black,                 // video surface — theme-independent
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = cam,
                Child = new Grid { Children = { img, label } },
            };
            tile.MouseLeftButtonUp += OnThumbClick;

            ThumbStrip.Children.Add(tile);
        }
    }

    private void OnThumbClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string cam)
            SetMain(cam);
    }

    // --- MoveClick cursor crosshair: a 20×20 marker that follows the mouse on
    //     hover while MoveClick is armed (main feed + Zoom2x). ---

    private void OnFeedMouseMove(object sender, System.Windows.Input.MouseEventArgs e) => MoveCursor(MainCursor, e);
    private void OnFeedMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => MainCursor.Visibility = Visibility.Collapsed;
    private void OnZoomMouseMove(object sender, System.Windows.Input.MouseEventArgs e) => MoveCursor(ZoomCursor, e);
    private void OnZoomMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ZoomCursor.Visibility = Visibility.Collapsed;

    /// <summary>Hides both hover cursors when MoveClick is disarmed.</summary>
    private void OnMoveDisarmed(object sender, System.Windows.RoutedEventArgs e)
    {
        MainCursor.Visibility = Visibility.Collapsed;
        ZoomCursor.Visibility = Visibility.Collapsed;
    }

    /// <summary>Centres the 20×20 cursor crosshair on the mouse (only while armed).</summary>
    private void MoveCursor(Grid cursor, System.Windows.Input.MouseEventArgs e)
    {
        if (MoveToggle.IsChecked != true)
        {
            cursor.Visibility = Visibility.Collapsed;
            return;
        }
        // Position is relative to the cursor's parent grid; offset by half its size to centre it.
        var p = e.GetPosition((System.Windows.IInputElement)cursor.Parent);
        cursor.Margin = new Thickness(p.X - cursor.Width / 2, p.Y - cursor.Height / 2, 0, 0);
        cursor.Visibility = Visibility.Visible;
    }

    // --- MoveClick: click the live image to measure (and jog) from crosshair centre ---

    private async void OnFeedClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MoveToggle.IsChecked != true || _mainCamera == null) return;
        if (!_bitmaps.TryGetValue(_mainCamera, out var bmp)) return;
        await DoMoveClickAsync(e.GetPosition(MainImage), MainImage.ActualWidth, MainImage.ActualHeight,
                               bmp.PixelWidth, bmp.PixelHeight, "feed");
    }

    private async void OnZoomClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MoveToggle.IsChecked != true || _zoomBitmap == null) return;
        await DoMoveClickAsync(e.GetPosition(ZoomImage), ZoomImage.ActualWidth, ZoomImage.ActualHeight,
                               _zoomBitmap.PixelWidth, _zoomBitmap.PixelHeight, "zoom");
    }

    /// <summary>
    /// Measures the click offset from the crosshair centre (mm via mm_pixel) on
    /// whichever surface was clicked — full feed or the Zoom2x ROI — then jogs the
    /// configured device by that offset (X+Y simultaneously). Both surfaces are
    /// centred on the same point, so the maths is identical; the Zoom2x ROI just
    /// has far fewer source px per display px, so its clicks are finer.
    /// </summary>
    private async Task DoMoveClickAsync(System.Windows.Point p, double availW, double availH,
                                        int srcW, int srcH, string surface)
    {
        if (availW <= 0 || availH <= 0 || srcW <= 0 || srcH <= 0) return;

        // Uniform-stretch scale: display px → source px → millimetres.
        double scale = Math.Min(availW / srcW, availH / srcH);
        if (scale <= 0) return;

        var s = _host.Settings.Current;
        double dxMm = ((p.X - availW / 2.0) / scale) * s.MmPerPixel;
        double dyMm = ((p.Y - availH / 2.0) / scale) * s.MmPerPixel;
        double dist = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);

        MoveReadout.Text = $"dx {dxMm:+0.000;-0.000}  dy {dyMm:+0.000;-0.000}  d {dist:0.000} mm";
        MoveReadoutBox.Visibility = Visibility.Visible;
        Log($"MoveClick ({surface}): dx {dxMm:+0.000;-0.000} dy {dyMm:+0.000;-0.000} d {dist:0.000} mm");

        // Jog by the measured offset. A single coordinated move drives X and Y
        // SIMULTANEOUSLY — MoveRelativeAsync settles one axis before the next
        // (per-device lock); MoveToPositionAsync moves all axes together.
        if (string.IsNullOrWhiteSpace(s.MoveClickDevice)) return;
        double xJog = dxMm * s.MoveClickXSign;
        double yJog = dyMm * s.MoveClickYSign;
        try
        {
            var posResult = await _host.Engine.Motion.GetPositionAsync(s.MoveClickDevice);
            if (!posResult.IsSuccess)
            {
                Log($"MoveClick jog — position read failed ({s.MoveClickDevice}): {posResult.Message}");
                return;
            }

            var target = AddAxis(AddAxis(posResult.Value, s.MoveClickXAxis, xJog), s.MoveClickYAxis, yJog);
            var move = await _host.Engine.Motion.MoveToPositionAsync(s.MoveClickDevice, target);
            Log(move.IsSuccess
                ? $"MoveClick jog {s.MoveClickDevice}: {s.MoveClickXAxis} {xJog:+0.000;-0.000}, {s.MoveClickYAxis} {yJog:+0.000;-0.000} mm (simultaneous)"
                : $"MoveClick jog failed ({s.MoveClickDevice}): {move.Message}");
        }
        catch (Exception ex)
        {
            Log($"MoveClick jog error: {ex.Message}");
        }
    }

    /// <summary>Returns a copy of <paramref name="p"/> with <paramref name="delta"/> added to the named axis component.</summary>
    private static MotionPosition AddAxis(MotionPosition p, string axis, double delta) => axis?.Trim().ToUpperInvariant() switch
    {
        "X" => p with { X = p.X + delta },
        "Y" => p with { Y = p.Y + delta },
        "Z" => p with { Z = p.Z + delta },
        "U" => p with { U = p.U + delta },
        "V" => p with { V = p.V + delta },
        "W" => p with { W = p.W + delta },
        _ => p,
    };

    private void SetMain(string camera)
    {
        _mainCamera = camera;
        MainLabel.Text = camera;

        if (_bitmaps.TryGetValue(camera, out var bmp))
        {
            MainImage.Source = bmp;
            NoVideo.Visibility = Visibility.Collapsed;
        }
        else
        {
            MainImage.Source = null;
            NoVideo.Visibility = Visibility.Visible;
        }

        // The zoom inset / ROI box follow the main camera — rebuild on the next frame.
        _zoomBitmap = null;
        ZoomImage.Source = null;
        RoiBox.Visibility = Visibility.Collapsed;

        BuildThumbnails();   // rebuild the strip without the new main
        Log($"Main camera → {camera}");
    }

    // --- log area ---

    private void LoadLog()
    {
        _logLines.Clear();
        foreach (var entry in _host.Log.Snapshot())
            _logLines.Enqueue(Format(entry));
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        FlushLog();
    }

    private void OnLogAppended(LogEntry entry)
    {
        // Fires on engine threads — marshal to the UI thread.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnLogAppended(entry));
            return;
        }
        _logLines.Enqueue(Format(entry));
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        FlushLog();
    }

    /// <summary>Writes a UI-side note into the shared engine log (and thus this view).</summary>
    private void Log(string message)
        => _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Run", message));

    private void FlushLog()
    {
        LogBox.Text = string.Join(Environment.NewLine, _logLines);
        LogBox.ScrollToEnd();
    }

    private static string Format(LogEntry e)
        => $"{e.Timestamp:HH:mm:ss} [{e.Source}] {e.Message}";
}
