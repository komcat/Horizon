using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Horizon.Camera;
using Horizon.Camera.Models;
using Horizon.Core;
using Horizon.CurrentMeasurement;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Horizon.App;

public partial class OverlayWindow : Window
{
    private readonly CameraManager _cameraManager;
    private readonly CurrentMeasurementManager? _cmeasManager;
    private MeasurementHub? _hub;
    private List<string>? _channelKeys;

    private LiveVideoSubscriber? _subscriber;
    private string? _activeCameraId;
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

    public OverlayWindow(CameraManager cameraManager, CurrentMeasurementManager? cmeasManager = null, MeasurementHub? hub = null)
    {
        _cameraManager = cameraManager;
        _cmeasManager = cmeasManager;
        _hub = hub;

        _snapTimestamps = new double[MaxChannels][];
        _snapValues = new double[MaxChannels][];
        for (int i = 0; i < MaxChannels; i++)
        {
            _snapTimestamps[i] = new double[MaxChartSamples];
            _snapValues[i] = new double[MaxChartSamples];
        }

        InitializeComponent();
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
        // Populate camera list
        var ids = _cameraManager.GetCameraIds();
        CameraCombo.ItemsSource = ids;
        if (ids.Count > 0)
            CameraCombo.SelectedIndex = 0;

        // Populate measurement device list
        if (_cmeasManager != null)
        {
            var names = _cmeasManager.GetAllDeviceNames();
            MeasCombo.ItemsSource = names;
            if (names.Count > 0)
                MeasCombo.SelectedIndex = 0; // triggers SelectionChanged → sets _channelKeys
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
        if (MeasCombo.SelectedItem is not string deviceName || _cmeasManager == null || _hub == null)
            return;

        var device = _cmeasManager.GetDevice(deviceName);
        if (device == null) return;

        _channelKeys = new List<string>(device.ChannelCount);
        foreach (var ch in device.EnabledChannels)
            _channelKeys.Add($"{deviceName}.CH{ch}");
    }

    private void StartFeed()
    {
        var cameraId = CameraCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(cameraId)) return;

        // Start grabbing if not already
        var status = _cameraManager.GetCameraStatus(cameraId);
        if (!status.Connected)
        {
            StatusText.Text = $"Camera '{cameraId}' not connected";
            return;
        }

        if (!status.Grabbing)
        {
            if (!_cameraManager.StartGrabbingWithBroadcast(cameraId))
            {
                StatusText.Text = $"Failed to start grabbing '{cameraId}'";
                return;
            }
        }

        _activeCameraId = cameraId;
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
        StatusText.Text = $"Live: {cameraId}";
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

        // Draw camera frame scaled to fill canvas
        if (_frameBitmap != null)
        {
            var srcRect = new SKRect(0, 0, _frameBitmap.Width, _frameBitmap.Height);
            var dstRect = FitRect(srcRect, new SKRect(0, 0, info.Width, info.Height));
            canvas.DrawBitmap(_frameBitmap, dstRect);
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
        for (int i = 0; i < 4; i++)
            canvas.DrawText(FormatCurrentSI(yVals[i]), ylx, yPositions[i], yLblPaint);

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
}
