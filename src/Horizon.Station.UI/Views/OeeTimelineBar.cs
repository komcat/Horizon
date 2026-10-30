using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Horizon.Workflow.Oee;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The OEE page's day strip: the whole window (normally 00:00–24:00 local) as one bar,
/// each <see cref="OeeSegment"/> painted in its state colour, the not-yet-elapsed part
/// left empty, with hour ticks underneath. Hovering shows the segment's state and times.
/// </summary>
public sealed class OeeTimelineBar : FrameworkElement
{
    private const double BarHeight = 34;
    private const double TickArea = 18;

    private static readonly Typeface Face = new("Segoe UI");
    private OeeReport? _report;

    public OeeTimelineBar()
    {
        Height = BarHeight + TickArea;
        ToolTipService.SetInitialShowDelay(this, 0);
        MouseMove += OnMouseMove;
    }

    public OeeReport? Report
    {
        get => _report;
        set { _report = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var edge = (Brush)FindResource("Edge");
        var text = (Brush)FindResource("Subtle");
        dc.DrawRectangle((Brush)FindResource("Bg"), new Pen(edge, 1), new Rect(0, 0, w, BarHeight));
        if (_report == null || w <= 0) return;

        var total = (_report.To - _report.From).TotalSeconds;
        if (total <= 0) return;

        foreach (var s in _report.Segments)
        {
            var x0 = (s.Start - _report.From).TotalSeconds / total * w;
            var x1 = (s.End - _report.From).TotalSeconds / total * w;
            dc.DrawRectangle(OeeColors.For(s.State), null, new Rect(x0, 1, Math.Max(x1 - x0, 0.5), BarHeight - 2));
        }

        // Hour ticks (local time) — a label every 2 h when there is room, else every 4 h.
        var hours = (int)Math.Round(total / 3600);
        var labelEvery = w / Math.Max(hours, 1) >= 28 ? 2 : 4;
        var pen = new Pen(edge, 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int h = 0; h <= hours; h++)
        {
            var x = Math.Min(h * 3600 / total * w, w - 0.5);
            dc.DrawLine(pen, new Point(x, BarHeight), new Point(x, BarHeight + (h % labelEvery == 0 ? 5 : 3)));
            if (h % labelEvery != 0) continue;
            var label = _report.From.AddHours(h).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, text, dpi);
            var lx = Math.Clamp(x - ft.Width / 2, 0, Math.Max(w - ft.Width, 0));
            dc.DrawText(ft, new Point(lx, BarHeight + 5));
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_report == null || ActualWidth <= 0) { ToolTip = null; return; }
        var x = e.GetPosition(this).X;
        var t = _report.From + TimeSpan.FromSeconds(x / ActualWidth * (_report.To - _report.From).TotalSeconds);
        var seg = _report.Segments.FirstOrDefault(s => t >= s.Start && t < s.End);
        ToolTip = seg == null
            ? $"{t.ToLocalTime():HH:mm} — not elapsed yet"
            : $"{OeeColors.Label(seg.State)}  {seg.Start.ToLocalTime():HH:mm}–{seg.End.ToLocalTime():HH:mm}  ({OeeColors.Duration(seg.Duration)})";
    }
}
