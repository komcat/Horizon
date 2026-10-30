using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Horizon.Station.UI.ViewModels.Sections;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Horizon.Station.UI.Views.Sections;

/// <summary>
/// SkiaSharp live trend chart for the Measurement section — overlays one
/// colour-coded current trace per Keithley 6482 channel, sharing a sliding ~10 s
/// window and a common auto-scaled Y axis. Bound to a
/// <see cref="MeasurementSectionViewModel"/>. Snapshot arrays and SkiaSharp
/// objects are created once and reused — a frame allocates nothing.
/// </summary>
public partial class MeasurementChartView : UserControl
{
    private const int MaxChannels = 4;
    private const int Capacity = 500;
    private const double TimeWindowSeconds = 10.0;

    private static readonly SKColor[] TraceColors =
    {
        new(0x4F, 0xC3, 0xF7),   // cyan   — CH1
        new(0xFF, 0xB7, 0x4D),   // amber  — CH2
        new(0x81, 0xC7, 0x84),   // green  — CH3
        new(0xBA, 0x68, 0xC8),   // purple — CH4
    };

    private readonly double[][] _ts;
    private readonly double[][] _values;
    private readonly int[] _counts = new int[MaxChannels];
    private readonly SKPaint[] _tracePaints;
    private DateTime _lastInvalidate;

    private readonly SKPaint _gridPaint = new() { Color = new SKColor(0x2A, 0x2A, 0x30), StrokeWidth = 1 };
    private readonly SKPaint _labelPaint = new() { Color = new SKColor(0x8A, 0x8A, 0x92), IsAntialias = true };
    private readonly SKPaint _legendPaint = new() { IsAntialias = true };
    private readonly SKPaint _emptyPaint = new() { Color = new SKColor(0x6A, 0x6A, 0x72), IsAntialias = true };
    private readonly SKFont _labelFont = new() { Size = 11f };
    private readonly SKFont _legendFont = new() { Size = 12f };
    private readonly SKFont _emptyFont = new() { Size = 12f };
    private readonly SKPath _path = new();

    public MeasurementChartView()
    {
        InitializeComponent();
        _ts = new double[MaxChannels][];
        _values = new double[MaxChannels][];
        _tracePaints = new SKPaint[MaxChannels];
        for (int i = 0; i < MaxChannels; i++)
        {
            _ts[i] = new double[Capacity];
            _values[i] = new double[Capacity];
            _tracePaints[i] = new SKPaint
            {
                Color = TraceColors[i],
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round,
            };
        }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;   // guard against a doubled subscription
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => CompositionTarget.Rendering -= OnRendering;

    private void OnRendering(object? sender, EventArgs e)
    {
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

        var vm = DataContext as MeasurementSectionViewModel;
        var channels = vm?.Channels;
        int n = channels == null ? 0 : Math.Min(channels.Count, MaxChannels);

        // Snapshot every channel; track the global time extent.
        double tMax = double.MinValue;
        for (int c = 0; c < n; c++)
        {
            var tele = channels![c].Store.GetChannel(channels[c].CurrentKey);
            _counts[c] = tele?.Snapshot(_ts[c], _values[c]) ?? 0;
            if (_counts[c] > 0 && _ts[c][_counts[c] - 1] > tMax)
                tMax = _ts[c][_counts[c] - 1];
        }

        if (tMax == double.MinValue)
        {
            canvas.DrawText("Connect the device to plot current",
                w / 2f, h / 2f, SKTextAlign.Center, _emptyFont, _emptyPaint);
            return;
        }
        double tMin = tMax - TimeWindowSeconds;

        // Common Y scale over the visible, non-NaN samples of every channel.
        double yMin = double.MaxValue, yMax = double.MinValue;
        for (int c = 0; c < n; c++)
        {
            for (int i = 0; i < _counts[c]; i++)
            {
                if (_ts[c][i] < tMin) continue;
                double v = _values[c][i];
                if (double.IsNaN(v)) continue;
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }
        }
        if (yMin > yMax)
        {
            canvas.DrawText("no reading", w / 2f, h / 2f, SKTextAlign.Center, _emptyFont, _emptyPaint);
            return;
        }

        if (yMax - yMin < 1e-30) { yMin -= 1; yMax += 1; }
        double pad = (yMax - yMin) * 0.1;
        yMin -= pad;
        yMax += pad;
        double range = yMax - yMin;

        const float margin = 8f;
        float cx = margin, cy = margin;
        float cw = w - 2 * margin;
        float chh = h - 2 * margin;

        for (int i = 0; i <= 4; i++)
        {
            float gy = cy + chh * i / 4f;
            canvas.DrawLine(cx, gy, cx + cw, gy, _gridPaint);
        }

        // One colour-coded trace per channel — break the line over NaN gaps.
        for (int c = 0; c < n; c++)
        {
            _path.Reset();
            bool started = false;
            for (int i = 0; i < _counts[c]; i++)
            {
                if (_ts[c][i] < tMin) continue;
                double v = _values[c][i];
                if (double.IsNaN(v)) { started = false; continue; }
                float x = cx + (float)((_ts[c][i] - tMin) / TimeWindowSeconds) * cw;
                float y = cy + chh - (float)((v - yMin) / range) * chh;
                if (!started) { _path.MoveTo(x, y); started = true; }
                else _path.LineTo(x, y);
            }
            canvas.DrawPath(_path, _tracePaints[c]);
        }

        // Y-axis labels.
        canvas.DrawText(FormatAmps(yMax), cx + 3, cy + 12, _labelFont, _labelPaint);
        canvas.DrawText(FormatAmps(yMin), cx + 3, cy + chh - 4, _labelFont, _labelPaint);

        // Legend — channel + latest value, in the trace colour.
        float ly = cy + 14;
        for (int c = 0; c < n; c++)
        {
            double latest = _counts[c] > 0 ? _values[c][_counts[c] - 1] : double.NaN;
            _legendPaint.Color = TraceColors[c];
            canvas.DrawText(
                $"CH{channels![c].Channel}  {(double.IsNaN(latest) ? "—" : FormatAmps(latest))}",
                cx + cw - 140, ly, _legendFont, _legendPaint);
            ly += 16;
        }
    }

    private static string FormatAmps(double amps) => $"{amps:E3} A";
}
