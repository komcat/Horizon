using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FAAM.UI.Siphog;
using Horizon.Engine;
using Horizon.Workflow;

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

    private DispatcherTimer? _detectionTimer;   // hides the vision-detection flash after a brief moment

    private string? _mainCamera;
    private bool _active;
    private bool _subscribed;
    private bool _logSubscribed;

    /// <summary>
    /// The shared SiPhOG client owned by the Run page's utility host — exposed so other
    /// pages (the Engineer Testing Ground) reuse this one connection instead of opening a
    /// second one. Valid once the constructor has run.
    /// </summary>
    public Siphog.SiphogClient Siphog => AppHost.SiphogClient;

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

        DispensePanel.Attach(host);   // operator point-and-shoot dispense of a saved routine

        // The alignment-utilities grid owns the SiPhOG connection panel (and its client).
        AppHost.Attach(host);
        var siphog = AppHost.SiphogClient;

        // Let the U/FAU capture prompts mirror onto the main-feed overlay (group 2), so the
        // operator can Capture/Apply from the camera view without looking at the side panel.
        // Registered on the shared hub so an open camera pop-out mirrors the same prompt.
        host.CapturePrompts.Add(MainPrompt);
        AppHost.SetCapturePromptSurface(host.CapturePrompts);

        // The SiPhOG chart (over the camera) shares that same client/connection.
        SiphogChartControl.Attach(siphog);

        // Enrich jog activities in the DUT log with the live Sagnac value. Installed here —
        // where the SiPhOG client lives — so the generic activity/DUT layer stays signal-agnostic
        // (Sagnac is application-specific to FAAM). Reuses the same SAG-PWR read as the hill
        // climber; yields null when the SiPhOG isn't streaming so no stale value is recorded.
        var sagProbe = new SiphogHillClimbFeedback(siphog);
        host.SetActivitySignalProbe(() => sagProbe.HasSample
            ? new KeyValuePair<string, double>("sagnacV", sagProbe.Read())
            : (KeyValuePair<string, double>?)null);

        // Let app.* workflow steps drive the utility panels (start/stop a panel by id).
        host.Process.SetUtilityAppHost(AppHost);

        // Make that same SiPhOG signal available to hillclimb.* workflow steps (Engineer page).
        host.Process.SetHillClimbFeedbackProvider(() => new SiphogHillClimbFeedback(siphog));

        // Publish the live SiPhOG frame into the unified signal.read catalog so Engineer
        // blocks can pull siphog.* values (alongside the built-in SMU/PSU/Measurement/…).
        host.Process.AddSignalSource(new SiphogSignalSource(siphog));

        // Let siphog.updateSheet workflow steps write a staged SAG-PWR measurement to the
        // SiPhOG server's Google Sheet (V11 UPDATE_SAG) over that same client/connection.
        host.Process.SetSiphogSheetSink(new SiphogSheetSink(siphog));
    }

    /// <summary>Starts the process timer — called by the shell header when a DUT run begins.</summary>
    public void StartProcessTimer() => StepsPanel.StartProcessTimer();

    /// <summary>Stops the process timer — called by the shell header when a DUT run ends.</summary>
    public void StopProcessTimer() => StepsPanel.StopProcessTimer();

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

        // Re-sync the process-steps deck from disk: a deck edited and saved in the
        // Engineer page (same product, same name) won't have refreshed the Run-page
        // buttons, so reload the selected deck's current Run-page entries on every show.
        StepsPanel.SyncDeck();

        // Fire-and-forget: each camera grabs independently. Already-grabbing
        // cameras are a harmless no-op.
        foreach (var cam in _cameras)
            _ = _host.Engine.Camera.StartGrabbingAsync(cam);

        if (!_subscribed)
        {
            _host.Engine.Camera.FrameReady += OnFrameReady;
            _host.Process.VisionProfileRan += OnVisionProfileRan;   // flash detections on the feed
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
            _host.Process.VisionProfileRan -= OnVisionProfileRan;
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

    // --- vision detection flash (yellow line over the main feed) ---

    /// <summary>
    /// Briefly draws the just-detected facet line over the main feed. Only reacts to the camera the
    /// main feed shows and to Line results (needs x1/y1/x2/y2); other methods (NCC/circle) are ignored.
    /// The event may arrive on a background thread, so marshal first.
    /// </summary>
    private void OnVisionProfileRan(VisionProfileDetection d)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => OnVisionProfileRan(d)); return; }
        if (!_active || _mainCamera == null) return;
        if (!string.Equals(d.CameraId, _mainCamera, StringComparison.OrdinalIgnoreCase)) return;
        if (!d.Result.Found) return;

        double? x1 = Value("x1"), y1 = Value("y1"), x2 = Value("x2"), y2 = Value("y2");
        if (x1 is null || y1 is null || x2 is null || y2 is null) return;   // not a line detection
        ShowDetectedLine(x1.Value, y1.Value, x2.Value, y2.Value);

        double? Value(string key)
        {
            foreach (var v in d.Result.Values)
                if (string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)) return v.Value;
            return null;
        }
    }

    /// <summary>
    /// Maps the source-pixel line endpoints onto the feed's display and shows the yellow overlay,
    /// then arms the auto-hide timer. Uses the same uniform-scale + centre mapping as MoveClick, so
    /// the drawn line lines up with what the click maths would report.
    /// </summary>
    private void ShowDetectedLine(double sx1, double sy1, double sx2, double sy2)
    {
        if (_mainCamera == null || !_bitmaps.TryGetValue(_mainCamera, out var bmp)) return;
        double srcW = bmp.PixelWidth, srcH = bmp.PixelHeight;
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        if (srcW <= 0 || srcH <= 0 || availW <= 0 || availH <= 0) return;

        double scale = Math.Min(availW / srcW, availH / srcH);
        double cx = availW / 2.0, cy = availH / 2.0;

        // Size the overlay to the display box so its coordinates match the mapping below.
        DetectionOverlay.Width = availW;
        DetectionOverlay.Height = availH;

        DetectedLine.X1 = cx + (sx1 - srcW / 2.0) * scale;
        DetectedLine.Y1 = cy + (sy1 - srcH / 2.0) * scale;
        DetectedLine.X2 = cx + (sx2 - srcW / 2.0) * scale;
        DetectedLine.Y2 = cy + (sy2 - srcH / 2.0) * scale;
        DetectedLine.Visibility = Visibility.Visible;

        _detectionTimer ??= CreateDetectionTimer();
        _detectionTimer.Stop();
        _detectionTimer.Start();
    }

    private DispatcherTimer CreateDetectionTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            DetectedLine.Visibility = Visibility.Collapsed;
        };
        return t;
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
        var m = MoveClick.Measure(_host, p, availW, availH, srcW, srcH);
        if (m is not { } meas) return;

        MoveReadout.Text = meas.Readout;
        MoveReadoutBox.Visibility = Visibility.Visible;
        Log($"MoveClick ({surface}): {meas.Readout}");

        await MoveClick.JogAsync(_host, meas, Log);
    }

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
