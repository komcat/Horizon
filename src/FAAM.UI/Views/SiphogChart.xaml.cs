using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FAAM.UI.Siphog;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace FAAM.UI.Views;

/// <summary>
/// SkiaSharp live trend chart for the SiPhOG stream — overlays SAG PWR (V) and
/// Target SAG PWR (V) on one shared, auto-scaled Y axis over a sliding window.
/// Fed by <see cref="SiphogClient.FrameDecoded"/> on the receive thread into a ring
/// buffer; repainted on a ~30 fps throttle. Snapshot arrays + SkiaSharp objects are
/// reused, so a frame allocates nothing.
/// </summary>
public partial class SiphogChart : UserControl
{
    private const int Capacity = 2000;            // ~10 s at 200 Hz
    private const double TimeWindowSeconds = 10.0;

    private static readonly SKColor SagColor = new(0x4F, 0xC3, 0xF7);     // cyan
    private static readonly SKColor TargetColor = new(0xFF, 0xB7, 0x4D);  // amber

    private readonly object _lock = new();
    private readonly double[] _t = new double[Capacity];
    private readonly double[] _sag = new double[Capacity];
    private readonly double[] _target = new double[Capacity];
    private int _write;
    private int _count;

    // Reused snapshot buffers (filled under lock, drawn outside it).
    private readonly double[] _snT = new double[Capacity];
    private readonly double[] _snSag = new double[Capacity];
    private readonly double[] _snTarget = new double[Capacity];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private SiphogClient? _client;
    private DateTime _lastInvalidate;

    private readonly SKPaint _gridPaint = new() { Color = new SKColor(0x2A, 0x2A, 0x30), StrokeWidth = 1 };
    private readonly SKPaint _labelPaint = new() { Color = new SKColor(0x8A, 0x8A, 0x92), IsAntialias = true };
    private readonly SKPaint _legendPaint = new() { IsAntialias = true };
    private readonly SKPaint _emptyPaint = new() { Color = new SKColor(0x6A, 0x6A, 0x72), IsAntialias = true };
    private readonly SKFont _labelFont = new() { Size = 33f };
    private readonly SKFont _legendFont = new() { Size = 36f };
    private readonly SKFont _emptyFont = new() { Size = 36f };
    private readonly SKPaint _sagPaint = new() { Color = SagColor, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _targetPaint = new() { Color = TargetColor, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPath _path = new();

    public SiphogChart()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Binds the chart to a client's frame stream (shared with the SiPhOG panel).</summary>
    public void Attach(SiphogClient client)
    {
        if (_client != null)
        {
            _client.FrameDecoded -= OnFrame;
            _client.ConnectionChanged -= OnConnectionChanged;
        }
        _client = client;
        _client.FrameDecoded += OnFrame;
        _client.ConnectionChanged += OnConnectionChanged;
    }

    // Receive thread — append to the ring (cheap; no UI work).
    private void OnFrame(SiphogFrame f)
    {
        lock (_lock)
        {
            _t[_write] = _clock.Elapsed.TotalSeconds;
            _sag[_write] = f.SagPwrV;
            _target[_write] = f.TargetSagPwrV;
            _write = (_write + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected) return;          // freeze the last trace on disconnect
        lock (_lock) { _write = 0; _count = 0; }   // fresh trace on (re)connect
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => CompositionTarget.Rendering -= OnRendering;

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastInvalidate).TotalMilliseconds < 33) return;   // ~30 fps
        _lastInvalidate = now;
        Chart.InvalidateVisual();
    }

    private void OnChartPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x16, 0x16, 0x18));

        int w = e.Info.Width, h = e.Info.Height;
        if (w < 4 || h < 4) return;

        // Snapshot the ring in chronological order.
        int n;
        lock (_lock)
        {
            n = _count;
            int start = (_write - _count + Capacity) % Capacity;
            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % Capacity;
                _snT[i] = _t[idx];
                _snSag[i] = _sag[idx];
                _snTarget[i] = _target[idx];
            }
        }

        if (n == 0)
        {
            canvas.DrawText("Connect SiPhOG to plot SAG / Target",
                w / 2f, h / 2f, SKTextAlign.Center, _emptyFont, _emptyPaint);
            return;
        }

        double tMax = _snT[n - 1];
        double tMin = tMax - TimeWindowSeconds;

        // Common Y scale over the visible samples of both series.
        double yMin = double.MaxValue, yMax = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (_snT[i] < tMin) continue;
            yMin = Math.Min(yMin, Math.Min(_snSag[i], _snTarget[i]));
            yMax = Math.Max(yMax, Math.Max(_snSag[i], _snTarget[i]));
        }
        if (yMin > yMax) { yMin = -1; yMax = 1; }
        if (yMax - yMin < 1e-9) { yMin -= 0.5; yMax += 0.5; }
        double pad = (yMax - yMin) * 0.1;
        yMin -= pad; yMax += pad;
        double range = yMax - yMin;

        const float margin = 8f;
        float cx = margin, cy = margin, cw = w - 2 * margin, chh = h - 2 * margin;

        for (int i = 0; i <= 4; i++)
        {
            float gy = cy + chh * i / 4f;
            canvas.DrawLine(cx, gy, cx + cw, gy, _gridPaint);
        }

        DrawSeries(canvas, _snT, _snSag, n, tMin, yMin, range, cx, cy, cw, chh, _sagPaint);
        DrawSeries(canvas, _snT, _snTarget, n, tMin, yMin, range, cx, cy, cw, chh, _targetPaint);

        // Y-axis labels.
        canvas.DrawText($"{yMax:0.0000} V", cx + 4, cy + 30, _labelFont, _labelPaint);
        canvas.DrawText($"{yMin:0.0000} V", cx + 4, cy + chh - 8, _labelFont, _labelPaint);

        // Legend with latest values — right-aligned so the larger text never clips.
        _legendPaint.Color = SagColor;
        canvas.DrawText($"SAG  {_snSag[n - 1]:0.0000} V", cx + cw - 6, cy + 34, SKTextAlign.Right, _legendFont, _legendPaint);
        _legendPaint.Color = TargetColor;
        canvas.DrawText($"Target  {_snTarget[n - 1]:0.0000} V", cx + cw - 6, cy + 78, SKTextAlign.Right, _legendFont, _legendPaint);
    }

    private void DrawSeries(SKCanvas canvas, double[] t, double[] v, int n,
        double tMin, double yMin, double range, float cx, float cy, float cw, float chh, SKPaint paint)
    {
        _path.Reset();
        bool started = false;
        for (int i = 0; i < n; i++)
        {
            if (t[i] < tMin) continue;
            float x = cx + (float)((t[i] - tMin) / TimeWindowSeconds) * cw;
            float y = cy + chh - (float)((v[i] - yMin) / range) * chh;
            if (!started) { _path.MoveTo(x, y); started = true; }
            else _path.LineTo(x, y);
        }
        canvas.DrawPath(_path, paint);
    }
}
