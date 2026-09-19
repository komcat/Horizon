using System.Windows.Controls;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace FAAM.UI.Views;

/// <summary>One line of the trend chart — a name, a colour and one value (or a gap) per X position.</summary>
public sealed record ChartSeries(string Name, SKColor Color, double?[] Values);

/// <summary>
/// SkiaSharp trend chart over a <b>categorical</b> X axis (one slot per DUT run, oldest
/// on the left) with several overlaid series on a shared auto-scaled Y axis. Static data:
/// it repaints only when <see cref="SetSeries"/> / <see cref="SetHighlight"/> change it, so
/// unlike the live <see cref="SiphogChart"/> there is no per-frame render loop. Gaps (a run
/// without that snapshot) break the polyline; every present value is also dotted so a lone
/// point still shows. Text is scaled by the device pixel ratio so it reads the same on a
/// scaled display.
/// </summary>
public partial class DutTrendChart : UserControl
{
    /// <summary>Default series colours — the four traces of the measurement chart.</summary>
    public static readonly SKColor[] Palette =
    {
        new(0x4F, 0xC3, 0xF7),   // cyan
        new(0xFF, 0xB7, 0x4D),   // amber
        new(0x81, 0xC7, 0x84),   // green
        new(0xBA, 0x68, 0xC8),   // purple
    };

    private string _unit = "";
    private IReadOnlyList<string> _xLabels = Array.Empty<string>();
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private int _highlight = -1;

    private readonly SKPaint _gridPaint = new() { Color = new SKColor(0x2A, 0x2A, 0x30), StrokeWidth = 1 };
    private readonly SKPaint _highlightPaint = new() { Color = new SKColor(0xE0, 0xE0, 0xE0, 0x90), StrokeWidth = 1.5f, IsAntialias = true };
    private readonly SKPaint _labelPaint = new() { Color = new SKColor(0x8A, 0x8A, 0x92), IsAntialias = true };
    private readonly SKPaint _legendPaint = new() { IsAntialias = true };
    private readonly SKPaint _emptyPaint = new() { Color = new SKColor(0x6A, 0x6A, 0x72), IsAntialias = true };
    private readonly SKPaint _linePaint = new() { StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _dotPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKFont _labelFont = new();
    private readonly SKFont _legendFont = new();
    private readonly SKPath _path = new();

    public DutTrendChart() => InitializeComponent();

    /// <summary>Replaces the plotted data. Every series must have one value slot per X label.</summary>
    public void SetSeries(string unit, IReadOnlyList<string> xLabels, IReadOnlyList<ChartSeries> series)
    {
        _unit = unit ?? "";
        _xLabels = xLabels ?? Array.Empty<string>();
        _series = series ?? Array.Empty<ChartSeries>();
        if (_highlight >= _xLabels.Count) _highlight = -1;
        Chart.InvalidateVisual();
    }

    /// <summary>Marks one X position (the selected run) with a vertical line; -1 clears it.</summary>
    public void SetHighlight(int index)
    {
        _highlight = index >= 0 && index < _xLabels.Count ? index : -1;
        Chart.InvalidateVisual();
    }

    private void OnChartPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x16, 0x16, 0x18));

        int w = e.Info.Width, h = e.Info.Height;
        if (w < 4 || h < 4) return;

        // Device pixel ratio — the surface is in device pixels, WPF sizes in DIPs.
        float s = Chart.ActualWidth > 1 ? (float)(w / Chart.ActualWidth) : 1f;
        _labelFont.Size = 11f * s;
        _legendFont.Size = 12f * s;

        int n = _xLabels.Count;
        if (n == 0 || !FindRange(n, out double yMin, out double yMax))
        {
            canvas.DrawText("No UV data for this selection", w / 2f, h / 2f, SKTextAlign.Center, _legendFont, _emptyPaint);
            return;
        }

        if (yMax - yMin < 1e-9) { yMin -= 0.5; yMax += 0.5; }
        double pad = (yMax - yMin) * 0.1;
        yMin -= pad; yMax += pad;
        double range = yMax - yMin;

        float margin = 8f * s, bottom = 18f * s;
        float cx = margin, cy = margin, cw = w - 2 * margin, chh = h - margin - bottom;

        for (int i = 0; i <= 4; i++)
        {
            float gy = cy + chh * i / 4f;
            canvas.DrawLine(cx, gy, cx + cw, gy, _gridPaint);
        }

        // The selected run — behind the traces so the dots stay on top.
        if (_highlight >= 0)
        {
            float hx = XAt(_highlight, n, cx, cw);
            canvas.DrawLine(hx, cy, hx, cy + chh, _highlightPaint);
            canvas.DrawText(_xLabels[_highlight], hx, h - 4f * s, SKTextAlign.Center, _labelFont, _labelPaint);
        }

        for (int k = 0; k < _series.Count; k++)
            DrawSeries(canvas, _series[k], n, yMin, range, cx, cy, cw, chh, s);

        // Y-axis extremes and the X extremes (oldest / newest serial).
        canvas.DrawText($"{yMax:0.0000} {_unit}", cx + 4f * s, cy + 12f * s, _labelFont, _labelPaint);
        canvas.DrawText($"{yMin:0.0000} {_unit}", cx + 4f * s, cy + chh - 4f * s, _labelFont, _labelPaint);
        if (_highlight != 0)
            canvas.DrawText(_xLabels[0], cx, h - 4f * s, SKTextAlign.Left, _labelFont, _labelPaint);
        if (n > 1 && _highlight != n - 1)
            canvas.DrawText(_xLabels[n - 1], cx + cw, h - 4f * s, SKTextAlign.Right, _labelFont, _labelPaint);

        // Legend — right-aligned, one line per series, with the highlighted run's value if any.
        float ly = cy + 14f * s;
        for (int k = 0; k < _series.Count; k++)
        {
            var series = _series[k];
            _legendPaint.Color = series.Color;
            var text = series.Name;
            if (_highlight >= 0 && _highlight < series.Values.Length && series.Values[_highlight] is double hv)
                text = $"{series.Name}  {hv:0.0000} {_unit}";
            canvas.DrawText(text, cx + cw - 6f * s, ly, SKTextAlign.Right, _legendFont, _legendPaint);
            ly += 15f * s;
        }
    }

    /// <summary>Min/max over every present value; false when there is nothing to plot.</summary>
    private bool FindRange(int n, out double yMin, out double yMax)
    {
        yMin = double.MaxValue; yMax = double.MinValue;
        foreach (var series in _series)
        {
            int count = Math.Min(n, series.Values.Length);
            for (int i = 0; i < count; i++)
            {
                if (series.Values[i] is not double v || double.IsNaN(v) || double.IsInfinity(v)) continue;
                yMin = Math.Min(yMin, v);
                yMax = Math.Max(yMax, v);
            }
        }
        return yMin <= yMax;
    }

    private static float XAt(int i, int n, float cx, float cw)
        => n == 1 ? cx + cw / 2f : cx + cw * i / (n - 1);

    private void DrawSeries(SKCanvas canvas, ChartSeries series, int n,
        double yMin, double range, float cx, float cy, float cw, float chh, float s)
    {
        _linePaint.Color = series.Color;
        _dotPaint.Color = series.Color;
        _path.Reset();
        bool started = false;
        int count = Math.Min(n, series.Values.Length);
        for (int i = 0; i < count; i++)
        {
            if (series.Values[i] is not double v || double.IsNaN(v) || double.IsInfinity(v))
            {
                started = false;   // a gap breaks the line
                continue;
            }
            float x = XAt(i, n, cx, cw);
            float y = cy + chh - (float)((v - yMin) / range) * chh;
            if (!started) { _path.MoveTo(x, y); started = true; }
            else _path.LineTo(x, y);
            canvas.DrawCircle(x, y, 2.2f * s, _dotPaint);
        }
        canvas.DrawPath(_path, _linePaint);
    }
}
