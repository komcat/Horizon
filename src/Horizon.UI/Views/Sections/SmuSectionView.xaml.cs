using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Horizon.UI.ViewModels.Sections;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Horizon.UI.Views.Sections;

/// <summary>
/// The Machine page's SMU section — streaming control, numeric readout, and a
/// live SkiaSharp trend chart of the streamed current. Each frame snapshots the
/// telemetry ring buffer and draws a sliding ~10 s window. The snapshot arrays
/// and every SkiaSharp paint/font/path are created once and reused, so a frame
/// allocates nothing — no per-frame native-object churn, no GC stutter.
/// </summary>
public partial class SmuSectionView : UserControl
{
    private const int Capacity = 500;
    private const double TimeWindowSeconds = 10.0;

    private readonly double[] _ts = new double[Capacity];
    private readonly double[] _values = new double[Capacity];
    private DateTime _lastInvalidate;

    // Reused across frames — SkiaSharp objects wrap native handles; creating
    // them per paint is what caused the periodic GC freeze.
    private readonly SKPaint _gridPaint = new() { Color = new SKColor(0x2A, 0x2A, 0x30), StrokeWidth = 1 };
    private readonly SKPaint _linePaint = new()
    {
        Color = new SKColor(0x4F, 0xC3, 0xF7),
        StrokeWidth = 1.5f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeJoin = SKStrokeJoin.Round,
    };
    private readonly SKPaint _labelPaint = new() { Color = new SKColor(0x8A, 0x8A, 0x92), IsAntialias = true };
    private readonly SKPaint _latestPaint = new() { Color = new SKColor(0x4F, 0xC3, 0xF7), IsAntialias = true };
    private readonly SKPaint _emptyPaint = new() { Color = new SKColor(0x6A, 0x6A, 0x72), IsAntialias = true };
    private readonly SKFont _labelFont = new() { Size = 11f };
    private readonly SKFont _latestFont = new() { Size = 14f };
    private readonly SKFont _emptyFont = new() { Size = 12f };
    private readonly SKPath _path = new();

    public SmuSectionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Render only while the section is on screen — no leaked global handler.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;   // guard against a doubled subscription
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => CompositionTarget.Rendering -= OnRendering;

    private void OnRendering(object? sender, EventArgs e)
    {
        // Throttle to ~30 fps — the data stream is far slower than the frame rate.
        var now = DateTime.UtcNow;
        if ((now - _lastInvalidate).TotalMilliseconds < 33) return;
        _lastInvalidate = now;
        Chart.InvalidateVisual();
    }

    private void OnChartPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x16, 0x16, 0x18));

        int w = e.Info.Width;
        int h = e.Info.Height;
        if (w < 4 || h < 4) return;

        var vm = DataContext as SmuSectionViewModel;
        var channel = vm?.Store.GetChannel($"{vm.SelectedDevice}.CH{vm.Channel}.current");
        int count = channel?.Snapshot(_ts, _values) ?? 0;

        if (count == 0)
        {
            canvas.DrawText("Start streaming to plot current",
                w / 2f, h / 2f, SKTextAlign.Center, _emptyFont, _emptyPaint);
            return;
        }

        // Sliding window — the most recent TimeWindowSeconds of samples.
        double tMax = _ts[count - 1];
        double tMin = tMax - TimeWindowSeconds;

        // Y auto-scale over the visible, non-NaN samples.
        double yMin = double.MaxValue, yMax = double.MinValue;
        int firstVisible = count;
        for (int i = 0; i < count; i++)
        {
            if (_ts[i] < tMin) continue;
            if (firstVisible == count) firstVisible = i;
            double v = _values[i];
            if (double.IsNaN(v)) continue;
            if (v < yMin) yMin = v;
            if (v > yMax) yMax = v;
        }
        if (yMin > yMax)
        {
            canvas.DrawText("no reading", w / 2f, h / 2f, SKTextAlign.Center, _emptyFont, _emptyPaint);
            return;
        }

        // 10 % padding; guard a flat trace so the range is never zero.
        if (yMax - yMin < 1e-30) { yMin -= 1; yMax += 1; }
        double pad = (yMax - yMin) * 0.1;
        yMin -= pad;
        yMax += pad;
        double range = yMax - yMin;

        const float margin = 8f;
        float cx = margin, cy = margin;
        float cw = w - 2 * margin;
        float chh = h - 2 * margin;

        // Horizontal grid.
        for (int i = 0; i <= 4; i++)
        {
            float gy = cy + chh * i / 4f;
            canvas.DrawLine(cx, gy, cx + cw, gy, _gridPaint);
        }

        // Current trace — break the line over NaN gaps.
        _path.Reset();
        bool started = false;
        for (int i = firstVisible; i < count; i++)
        {
            double v = _values[i];
            if (double.IsNaN(v)) { started = false; continue; }
            float x = cx + (float)((_ts[i] - tMin) / TimeWindowSeconds) * cw;
            float y = cy + chh - (float)((v - yMin) / range) * chh;
            if (!started) { _path.MoveTo(x, y); started = true; }
            else _path.LineTo(x, y);
        }
        canvas.DrawPath(_path, _linePaint);

        // Axis labels — max, min, and the latest value.
        canvas.DrawText(FormatAmps(yMax), cx + 3, cy + 12, _labelFont, _labelPaint);
        canvas.DrawText(FormatAmps(yMin), cx + 3, cy + chh - 4, _labelFont, _labelPaint);

        double latest = _values[count - 1];
        if (!double.IsNaN(latest))
            canvas.DrawText(FormatAmps(latest), cx + 3, cy + 32, _latestFont, _latestPaint);
    }

    private static string FormatAmps(double amps) => $"{amps:E3} A";
}
