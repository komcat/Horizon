using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Camera;
using Horizon.Camera.Models;
using Horizon.Common;
using Horizon.Core;
using Horizon.CurrentMeasurement;
using Horizon.LaserController;
using Horizon.Motion;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Horizon.App;

public partial class OverlayWindow : Window
{
    private readonly CameraManager _cameraManager;
    private readonly CurrentMeasurementManager? _cmeasManager;
    private readonly LaserControllerManager? _laserManager;
    private MeasurementHub? _hub;
    private List<string>? _channelKeys;
    private Func<double, string>? _chartFormatter;

    private LiveVideoSubscriber? _subscriber;
    private string? _activeCameraId;
    private string? _activeCameraName;
    private Dictionary<string, string> _nameToId = new(StringComparer.OrdinalIgnoreCase);
    private bool _running;
    private bool _showChart = true;

    private DispatcherTimer? _renderTimer;

    // Pre-allocated frame buffer (reused across frames)
    private SKBitmap? _frameBitmap;
    private int _lastFrameW;
    private int _lastFrameH;
    private byte[]? _rgbBuffer;

    // Pre-allocated chart snapshot arrays
    private const int MaxChartSamples = 500;
    private const int MaxChannels = 2;
    private const double TimeWindow = 10.0;
    private readonly double[][] _snapTimestamps;
    private readonly double[][] _snapValues;

    // Chart colors
    private static readonly SKColor ChartBg = new(0, 0, 0, 0x90);
    private static readonly SKColor[] ChannelColors = { new(0, 255, 255), new(255, 180, 0) };
    private static readonly SKColor GridColor = new(128, 128, 128, 60);
    private static readonly SKColor LabelColor = new(200, 200, 200);

    // Crosshair + Click-to-Move fields
    private readonly UnifiedMotionManager? _motionManager;
    private readonly ClickToMoveConfig? _clickToMoveConfig;
    private bool _showCrosshair;
    private bool _clickToMoveEnabled;
    private Point? _lastMouseFramePos;
    private bool _isMoving;

    // Z Jog
    private static readonly (string Label, double Mm)[] JogSteps =
    [
        ("1 um",    0.001),
        ("5 um",    0.005),
        ("10 um",   0.01),
        ("50 um",   0.05),
        ("0.1 mm",  0.1),
        ("0.5 mm",  0.5),
        ("1 mm",    1.0),
        ("5 mm",    5.0),
        ("10 mm",  10.0),
    ];

    public OverlayWindow(CameraManager cameraManager, CurrentMeasurementManager? cmeasManager = null,
        MeasurementHub? hub = null, UnifiedMotionManager? motionManager = null,
        LaserControllerManager? laserManager = null)
    {
        _cameraManager = cameraManager;
        _cmeasManager = cmeasManager;
        _laserManager = laserManager;
        _hub = hub;
        _motionManager = motionManager;

        _snapTimestamps = new double[MaxChannels][];
        _snapValues = new double[MaxChannels][];
        for (int i = 0; i < MaxChannels; i++)
        {
            _snapTimestamps[i] = new double[MaxChartSamples];
            _snapValues[i] = new double[MaxChartSamples];
        }

        // Load click-to-move config
        var configDir = ConfigLoader.GetConfigDirectory();
        _clickToMoveConfig = ConfigLoader.LoadJson<ClickToMoveConfig>(configDir, "click_to_move");

        InitializeComponent();

        // Populate jog step combo (default = 1 mm, index 6)
        JogStepCombo.ItemsSource = JogSteps.Select(s => s.Label).ToList();
        JogStepCombo.SelectedIndex = 6;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public void SetChartSource(MeasurementHub? hub, List<string>? channelKeys)
    {
        _hub = hub;
        _channelKeys = channelKeys;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Populate camera list with display names
        var cameras = _cameraManager.GetCamerasWithNames();
        _nameToId = cameras.ToDictionary(c => c.DisplayName, c => c.Id, StringComparer.OrdinalIgnoreCase);
        CameraCombo.ItemsSource = cameras.Select(c => c.DisplayName).ToList();
        if (cameras.Count > 0)
            CameraCombo.SelectedIndex = 0;

        // Populate measurement device list (cmeas + laser)
        var allMeasNames = new List<string>();
        if (_cmeasManager != null)
            allMeasNames.AddRange(_cmeasManager.GetAllDeviceNames());
        if (_laserManager != null)
            allMeasNames.AddRange(_laserManager.GetAllDeviceNames());

        if (allMeasNames.Count > 0)
        {
            MeasCombo.ItemsSource = allMeasNames;
            MeasCombo.SelectedIndex = 0;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopFeed();
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // If running, switch camera
        if (_running)
        {
            StopFeed();
            StartFeed();
        }
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
            StopFeed();
        else
            StartFeed();
    }

    private void ChartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _showChart = ChartCheckBox.IsChecked == true;
        // Guard: fires during InitializeComponent before SKElement is created
        SkiaCanvas?.InvalidateVisual();
    }

    private void MeasCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MeasCombo.SelectedItem is not string deviceName || _hub == null)
            return;

        // Try current measurement device first
        if (_cmeasManager != null)
        {
            var device = _cmeasManager.GetDevice(deviceName);
            if (device != null)
            {
                _channelKeys = new List<string>(device.ChannelCount);
                foreach (var ch in device.EnabledChannels)
                    _channelKeys.Add($"{deviceName}.CH{ch}");
                _chartFormatter = null;
                return;
            }
        }

        // Try laser controller device
        if (_laserManager != null)
        {
            var device = _laserManager.GetDevice(deviceName);
            if (device != null)
            {
                _channelKeys = new List<string> { $"{deviceName}.Temperature", $"{deviceName}.LaserCurrent" };
                _chartFormatter = FormatGenericValue;
            }
        }
    }

    private void StartFeed()
    {
        var displayName = CameraCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(displayName)) return;

        // Resolve display name to camera ID
        if (!_nameToId.TryGetValue(displayName, out var cameraId))
            cameraId = displayName; // fallback: treat as raw ID

        // Start grabbing if not already
        var status = _cameraManager.GetCameraStatus(cameraId);
        if (!status.Connected)
        {
            StatusText.Text = $"Camera '{displayName}' not connected";
            return;
        }

        if (!status.Grabbing)
        {
            if (!_cameraManager.StartGrabbingWithBroadcast(cameraId))
            {
                StatusText.Text = $"Failed to start grabbing '{displayName}'";
                return;
            }
        }

        _activeCameraId = cameraId;
        _activeCameraName = displayName;
        _subscriber = new LiveVideoSubscriber(cameraId, "overlay");
        _cameraManager.SubscribeToFrames(_subscriber);
        _subscriber.InitializeStatusFromManager(_cameraManager);

        // Allocate RGB buffer
        _rgbBuffer ??= new byte[4096 * 4096 * 3];

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += RenderTick;
        _renderTimer.Start();

        _running = true;
        StartStopButton.Content = "Stop";
        StatusText.Text = $"Live: {displayName}";
    }

    private void StopFeed()
    {
        if (_renderTimer != null)
        {
            _renderTimer.Stop();
            _renderTimer.Tick -= RenderTick;
            _renderTimer = null;
        }

        if (_subscriber != null)
        {
            _cameraManager.UnsubscribeFromFrames(_subscriber.SubscriberId);
            _subscriber = null;
        }

        _activeCameraId = null;
        _activeCameraName = null;
        _running = false;
        StartStopButton.Content = "Start";
        StatusText.Text = "Stopped";
    }

    private void RenderTick(object? sender, EventArgs e)
    {
        // Pull latest frame from subscriber
        if (_subscriber != null && _subscriber.HasNewFrame && _rgbBuffer != null)
        {
            if (_subscriber.GetLatestFrameInto(_rgbBuffer, out int w, out int h, out int ch))
            {
                EnsureBitmap(w, h);
                CopyFrameToBitmap(w, h, ch);
            }
        }

        SkiaCanvas.InvalidateVisual();
    }

    private void EnsureBitmap(int w, int h)
    {
        if (_frameBitmap != null && _lastFrameW == w && _lastFrameH == h)
            return;

        _frameBitmap?.Dispose();
        _frameBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        _lastFrameW = w;
        _lastFrameH = h;
    }

    private unsafe void CopyFrameToBitmap(int w, int h, int channels)
    {
        if (_frameBitmap == null || _rgbBuffer == null) return;

        var pixels = _frameBitmap.GetPixels();
        byte* dst = (byte*)pixels;
        int pixelCount = w * h;

        fixed (byte* src = _rgbBuffer)
        {
            int si = 0, di = 0;
            for (int i = 0; i < pixelCount; i++)
            {
                dst[di + 0] = src[si + 2]; // B
                dst[di + 1] = src[si + 1]; // G
                dst[di + 2] = src[si + 0]; // R
                dst[di + 3] = 255;          // A
                si += channels;
                di += 4;
            }
        }
    }

    private void SkiaCanvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear(SKColors.Black);

        SKRect dstRect = default;

        // Draw camera frame scaled to fill canvas
        if (_frameBitmap != null)
        {
            var srcRect = new SKRect(0, 0, _frameBitmap.Width, _frameBitmap.Height);
            dstRect = FitRect(srcRect, new SKRect(0, 0, info.Width, info.Height));
            canvas.DrawBitmap(_frameBitmap, dstRect);
        }

        // Draw center crosshair on the image
        if (_showCrosshair && _frameBitmap != null)
        {
            DrawCenterCrosshair(canvas, dstRect);
        }

        // Draw Click-to-Move overlay (mouse crosshair, distance label, moving indicator)
        if (_clickToMoveEnabled && _frameBitmap != null)
        {
            DrawClickToMoveOverlay(canvas, dstRect, _frameBitmap.Width, _frameBitmap.Height);
        }

        // Draw chart overlay
        if (_showChart && _hub != null && _channelKeys != null && _channelKeys.Count > 0)
        {
            DrawChartOverlay(canvas, info.Width, info.Height);
        }
    }

    private void DrawChartOverlay(SKCanvas canvas, int canvasW, int canvasH)
    {
        int channelCount = Math.Min(_channelKeys!.Count, MaxChannels);

        // Snapshot each channel independently
        int[] counts = new int[channelCount];
        for (int ch = 0; ch < channelCount; ch++)
        {
            var channel = _hub!.GetChannel(_channelKeys[ch]);
            counts[ch] = channel?.Snapshot(_snapTimestamps[ch], _snapValues[ch]) ?? 0;
        }

        // Find global tMax across all channels
        double tMax = double.MinValue;
        bool anyData = false;
        for (int ch = 0; ch < channelCount; ch++)
        {
            if (counts[ch] == 0) continue;
            anyData = true;
            double t = _snapTimestamps[ch][counts[ch] - 1];
            if (t > tMax) tMax = t;
        }
        if (!anyData) return;

        // Chart dimensions — top-right corner
        float chartW = Math.Min(400, canvasW * 0.4f);
        float chartH = Math.Min(200, canvasH * 0.3f);
        float margin = 12;
        float labelW = 52;
        float axisH = 16;
        float panelW = chartW + labelW + margin * 2;
        float panelH = chartH + axisH + margin * 2 + 20; // 20 for header

        float px = canvasW - panelW - 8;
        float py = 8;

        // Background panel
        using var bgPaint = new SKPaint { Color = ChartBg, Style = SKPaintStyle.Fill };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(px, py, px + panelW, py + panelH), 6), bgPaint);

        // Header
        using var headerPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 12,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Consolas")
        };
        canvas.DrawText("Current Chart (10s)", px + margin, py + 15, headerPaint);

        // Legend (top-right of panel)
        float legendX = px + panelW - margin - 100;
        float legendY = py + 15;
        for (int ch = 0; ch < channelCount; ch++)
        {
            using var dotPaint = new SKPaint { Color = ChannelColors[ch], Style = SKPaintStyle.Fill };
            float dx = legendX + ch * 50;
            canvas.DrawCircle(dx, legendY - 3, 4, dotPaint);
            using var lblPaint = new SKPaint
            {
                Color = LabelColor,
                TextSize = 10,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Consolas")
            };
            canvas.DrawText($"CH{ch + 1}", dx + 7, legendY, lblPaint);
        }

        // Chart area
        float cx = px + margin + labelW;
        float cy = py + 20 + margin;
        float cw = chartW;
        float ch2 = chartH;

        double tMin = tMax - TimeWindow;

        // Find Y range within visible window across all channels
        double yMin = double.MaxValue, yMax = double.MinValue;
        int[] visStarts = new int[channelCount];
        for (int c = 0; c < channelCount; c++)
        {
            visStarts[c] = counts[c];
            for (int i = 0; i < counts[c]; i++)
            {
                if (_snapTimestamps[c][i] < tMin) continue;
                if (visStarts[c] == counts[c]) visStarts[c] = i;
                double v = _snapValues[c][i];
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }
        }
        if (yMin > yMax) return;

        // Auto-scale with padding
        double yRange = yMax - yMin;
        if (yRange < 1e-15)
        {
            double pad = Math.Abs(yMin) * 0.1;
            if (pad < 1e-12) pad = 1e-12;
            yMin -= pad;
            yMax += pad;
            yRange = yMax - yMin;
        }
        else
        {
            double pad = yRange * 0.1;
            yMin -= pad;
            yMax += pad;
            yRange = yMax - yMin;
        }

        // Grid lines (3 horizontal)
        using var gridPaint = new SKPaint
        {
            Color = GridColor,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
        };
        for (int i = 1; i <= 3; i++)
        {
            float gy = cy + ch2 * i / 4f;
            canvas.DrawLine(cx, gy, cx + cw, gy, gridPaint);
        }

        // Y-axis labels
        using var yLblPaint = new SKPaint
        {
            Color = LabelColor,
            TextSize = 9,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Consolas"),
            TextAlign = SKTextAlign.Right
        };
        float ylx = cx - 4;
        double[] yVals = { yMax, yMin + yRange * 0.75, yMin + yRange * 0.5, yMin + yRange * 0.25 };
        float[] yPositions = { cy + 4, cy + ch2 * 0.25f + 3, cy + ch2 * 0.5f + 3, cy + ch2 * 0.75f + 3 };
        var fmt = _chartFormatter ?? FormatCurrentSI;
        for (int i = 0; i < 4; i++)
            canvas.DrawText(fmt(yVals[i]), ylx, yPositions[i], yLblPaint);

        // X-axis labels
        using var xLblPaint = new SKPaint
        {
            Color = LabelColor,
            TextSize = 9,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Consolas")
        };
        float xby = cy + ch2 + 12;
        canvas.DrawText("-10s", cx, xby, xLblPaint);
        xLblPaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText("-5s", cx + cw / 2, xby, xLblPaint);
        xLblPaint.TextAlign = SKTextAlign.Right;
        canvas.DrawText("0s", cx + cw, xby, xLblPaint);

        // Draw channel lines
        for (int c = 0; c < channelCount; c++)
        {
            using var linePaint = new SKPaint
            {
                Color = ChannelColors[c],
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round
            };

            using var path = new SKPath();
            bool started = false;

            for (int i = visStarts[c]; i < counts[c]; i++)
            {
                float x = cx + (float)((_snapTimestamps[c][i] - tMin) / TimeWindow) * cw;
                float y = cy + ch2 - (float)((_snapValues[c][i] - yMin) / yRange) * ch2;

                if (!started)
                {
                    path.MoveTo(x, y);
                    started = true;
                }
                else
                {
                    path.LineTo(x, y);
                }
            }

            canvas.DrawPath(path, linePaint);
        }
    }

    // --- Crosshair + Click-to-Move: checkbox handlers, mouse handlers, overlay ---

    private void CrosshairCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _showCrosshair = CrosshairCheckBox.IsChecked == true;
        SkiaCanvas?.InvalidateVisual();
    }

    private void ClickToMoveCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        _clickToMoveEnabled = true;
    }

    private void ClickToMoveCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _clickToMoveEnabled = false;
        _lastMouseFramePos = null;
    }

    private void SKElement_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_clickToMoveEnabled || _frameBitmap == null) return;

        var wpfPoint = e.GetPosition(SkiaCanvas);
        var srcRect = new SKRect(0, 0, _frameBitmap.Width, _frameBitmap.Height);
        var canvasSize = new SKRect(0, 0,
            (float)(SkiaCanvas.ActualWidth * GetDpiScale()),
            (float)(SkiaCanvas.ActualHeight * GetDpiScale()));
        var dstRect = FitRect(srcRect, canvasSize);

        var framePos = WpfPointToFramePixels(wpfPoint, dstRect, _frameBitmap.Width, _frameBitmap.Height);
        _lastMouseFramePos = framePos;
    }

    private void SKElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_clickToMoveEnabled || _isMoving || _motionManager == null)
            return;

        var cfg = _clickToMoveConfig ?? new ClickToMoveConfig();
        var cal = cfg.GetCalibration(_activeCameraName ?? _activeCameraId);
        if (_frameBitmap == null) return;

        var wpfPoint = e.GetPosition(SkiaCanvas);
        var srcRect = new SKRect(0, 0, _frameBitmap.Width, _frameBitmap.Height);
        var canvasSize = new SKRect(0, 0,
            (float)(SkiaCanvas.ActualWidth * GetDpiScale()),
            (float)(SkiaCanvas.ActualHeight * GetDpiScale()));
        var dstRect = FitRect(srcRect, canvasSize);

        var framePos = WpfPointToFramePixels(wpfPoint, dstRect, _frameBitmap.Width, _frameBitmap.Height);
        if (framePos == null) return;

        int frameW = _frameBitmap.Width;
        int frameH = _frameBitmap.Height;
        double frameX = framePos.Value.X;
        double frameY = framePos.Value.Y;

        // Pixel offset from frame center
        double dx = frameX - frameW / 2.0;
        double dy = frameY - frameH / 2.0;

        // Scale to calibration reference resolution
        dx *= (double)cal.CalibrationRefWidth / frameW;
        dy *= (double)cal.CalibrationRefHeight / frameH;

        // Convert to mm: pixel offset * mm_per_pixel / magnification
        double mmX = dx * cal.PixelToMmX / cal.Magnification;
        double mmY = dy * cal.PixelToMmY / cal.Magnification;

        // Apply invert flags
        if (cal.InvertX) mmX = -mmX;
        if (cal.InvertY) mmY = -mmY;

        var deviceName = cfg.MotionDevice;
        var currentPos = _motionManager.GetCurrentPosition(deviceName);
        if (currentPos == null)
        {
            StatusText.Text = $"Move2Point: device '{deviceName}' position unavailable";
            return;
        }

        double newX = currentPos.X + mmX;
        double newY = currentPos.Y + mmY;

        _isMoving = true;
        StatusText.Text = $"Moving to X={newX:F3} Y={newY:F3} mm...";

        // Fire-and-forget async move
        _ = Task.Run(() =>
        {
            try
            {
                var moveX = Task.Run(() => _motionManager.MoveToAbs(deviceName, "X", newX, blocking: true));
                var moveY = Task.Run(() => _motionManager.MoveToAbs(deviceName, "Y", newY, blocking: true));
                Task.WaitAll(moveX, moveY);
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    _isMoving = false;
                    StatusText.Text = _running ? $"Live: {_activeCameraName ?? _activeCameraId}" : "Idle";
                });
            }
        });
    }

    private void SKElement_MouseLeave(object sender, MouseEventArgs e)
    {
        _lastMouseFramePos = null;
    }

    // --- Z Jog ---

    private void ZPlus_Click(object sender, RoutedEventArgs e) => JogZ(+1);
    private void ZMinus_Click(object sender, RoutedEventArgs e) => JogZ(-1);

    private void JogZ(int direction)
    {
        if (_motionManager == null) return;

        var idx = JogStepCombo.SelectedIndex;
        if (idx < 0 || idx >= JogSteps.Length) return;

        var cfg = _clickToMoveConfig ?? new ClickToMoveConfig();
        var deviceName = cfg.MotionDevice;
        var step = JogSteps[idx].Mm;
        var distance = step * direction;

        var ok = _motionManager.MoveRelative(deviceName, "Z", distance, blocking: false);
        StatusText.Text = ok
            ? $"Jog Z {(direction > 0 ? "+" : "")}{distance} mm"
            : $"Jog Z FAILED";

        // Update Z position display after a short delay
        _ = Task.Run(() =>
        {
            Thread.Sleep(200);
            var pos = _motionManager.GetCurrentPosition(deviceName);
            if (pos != null)
                Dispatcher.Invoke(() => ZPositionText.Text = $"Z = {pos.Z:F4} mm");
        });
    }

    private void DrawCenterCrosshair(SKCanvas canvas, SKRect destRect)
    {
        if (destRect.Width <= 0 || destRect.Height <= 0) return;

        float centerX = destRect.Left + destRect.Width / 2;
        float centerY = destRect.Top + destRect.Height / 2;

        // Full image-spanning crosshair lines
        using var paint = new SKPaint
        {
            Color = new SKColor(0, 255, 0, 140),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        canvas.DrawLine(destRect.Left, centerY, destRect.Right, centerY, paint);
        canvas.DrawLine(centerX, destRect.Top, centerX, destRect.Bottom, paint);
    }

    private void DrawClickToMoveOverlay(SKCanvas canvas, SKRect destRect, int frameW, int frameH)
    {
        if (destRect.Width <= 0 || destRect.Height <= 0) return;

        float scaleX = destRect.Width / frameW;
        float scaleY = destRect.Height / frameH;
        float centerCanvasX = destRect.Left + destRect.Width / 2;
        float centerCanvasY = destRect.Top + destRect.Height / 2;
        float crossSize = 20;

        // If crosshair checkbox is off, draw the center crosshair here so click-to-move still shows reference
        if (!_showCrosshair)
        {
            using var centerPaint = new SKPaint
            {
                Color = new SKColor(0, 255, 0, 140),
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            canvas.DrawLine(destRect.Left, centerCanvasY, destRect.Right, centerCanvasY, centerPaint);
            canvas.DrawLine(centerCanvasX, destRect.Top, centerCanvasX, destRect.Bottom, centerPaint);
        }

        // Mouse crosshair + distance line
        if (_lastMouseFramePos != null)
        {
            var cfg = _clickToMoveConfig ?? new ClickToMoveConfig();
            var cal = cfg.GetCalibration(_activeCameraName ?? _activeCameraId);
            float mouseFrameX = (float)_lastMouseFramePos.Value.X;
            float mouseFrameY = (float)_lastMouseFramePos.Value.Y;

            float mouseCanvasX = destRect.Left + mouseFrameX * scaleX;
            float mouseCanvasY = destRect.Top + mouseFrameY * scaleY;

            // Cyan crosshair at mouse position
            using var mousePaint = new SKPaint
            {
                Color = new SKColor(0, 255, 255, 200),
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            canvas.DrawLine(mouseCanvasX - crossSize, mouseCanvasY, mouseCanvasX + crossSize, mouseCanvasY, mousePaint);
            canvas.DrawLine(mouseCanvasX, mouseCanvasY - crossSize, mouseCanvasX, mouseCanvasY + crossSize, mousePaint);

            // Dashed line from center to mouse
            using var dashPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 0, 120),
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 6, 4 }, 0)
            };
            canvas.DrawLine(centerCanvasX, centerCanvasY, mouseCanvasX, mouseCanvasY, dashPaint);

            // Distance label in mm
            double dx = mouseFrameX - frameW / 2.0;
            double dy = mouseFrameY - frameH / 2.0;
            dx *= (double)cal.CalibrationRefWidth / frameW;
            dy *= (double)cal.CalibrationRefHeight / frameH;
            double mmX = dx * cal.PixelToMmX / cal.Magnification;
            double mmY = dy * cal.PixelToMmY / cal.Magnification;
            if (cal.InvertX) mmX = -mmX;
            if (cal.InvertY) mmY = -mmY;
            double dist = Math.Sqrt(mmX * mmX + mmY * mmY);

            using var labelPaint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 12,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Consolas")
            };
            string label = $"dX={mmX:F3} dY={mmY:F3} mm (d={dist:F3})";
            canvas.DrawText(label, mouseCanvasX + 8, mouseCanvasY - 8, labelPaint);
        }

        // "Moving..." indicator
        if (_isMoving)
        {
            using var movingPaint = new SKPaint
            {
                Color = new SKColor(255, 100, 100, 220),
                TextSize = 16,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Consolas"),
                FakeBoldText = true
            };
            canvas.DrawText("Moving...", destRect.Left + 10, destRect.Top + 24, movingPaint);
        }
    }

    private Point? WpfPointToFramePixels(Point wpfPoint, SKRect destRect, int frameW, int frameH)
    {
        double dpiScale = GetDpiScale();
        double canvasX = wpfPoint.X * dpiScale;
        double canvasY = wpfPoint.Y * dpiScale;

        // Check if within the frame destRect
        if (canvasX < destRect.Left || canvasX > destRect.Right ||
            canvasY < destRect.Top || canvasY > destRect.Bottom)
            return null;

        // Reverse-map from destRect to frame pixel coords
        double frameX = (canvasX - destRect.Left) / destRect.Width * frameW;
        double frameY = (canvasY - destRect.Top) / destRect.Height * frameH;

        return new Point(frameX, frameY);
    }

    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private static SKRect FitRect(SKRect src, SKRect dst)
    {
        float scale = Math.Min(dst.Width / src.Width, dst.Height / src.Height);
        float w = src.Width * scale;
        float h = src.Height * scale;
        float x = dst.Left + (dst.Width - w) / 2;
        float y = dst.Top + (dst.Height - h) / 2;
        return new SKRect(x, y, x + w, y + h);
    }

    private static string FormatCurrentSI(double amps)
    {
        var absVal = Math.Abs(amps);
        if (absVal < 1e-12) return "0 A";
        if (absVal < 1e-9) return $"{amps * 1e12:F1} pA";
        if (absVal < 1e-6) return $"{amps * 1e9:F1} nA";
        if (absVal < 1e-3) return $"{amps * 1e6:F1} uA";
        if (absVal < 1.0) return $"{amps * 1e3:F1} mA";
        return $"{amps:F3} A";
    }

    private static string FormatGenericValue(double value)
    {
        var abs = Math.Abs(value);
        if (abs < 0.001) return $"{value * 1000:F2} m";
        if (abs < 1000) return $"{value:F2}";
        return $"{value:F0}";
    }
}
