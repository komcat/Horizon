using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The needle-motion drawing (FireDog's <c>DrawNeedleMotion</c>): one pass of the dispense
/// needle in eight numbered stages — 1 traverse, 2 slide down, 3 approach, 4 final, 5 shot,
/// 6 lift off, 7 slide up, 8 back to the camera — labelled with the caller's parameter values.
/// The width is cut into four regions in the ratio <see cref="RegionWeights"/>; a page lays its
/// parameter columns in a grid with the same star widths so each field sits under the stage it
/// drives. Used by the Calibration › Camera → Needle page and the Engineer › Dispensing tab.
/// </summary>
public sealed class NeedleMotionDiagram : FrameworkElement
{
    /// <summary>Design width of the four regions (sum = <see cref="DesignWidth"/>). Grid columns use these as star weights.</summary>
    public static readonly double[] RegionWeights = { 255, 325, 290, 230 };

    private const double DesignWidth = 1100;
    private const double DesignHeight = 430;
    private const double YT = 150;   // traverse level (needle tip, slide up)
    private const double YD = 215;   // slide down
    private const double YS = 290;   // safety height
    private const double YF = 360;   // surface
    private const double FontPx = 12;

    private static FrameworkPropertyMetadata TextMeta() => new("", FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty TraverseTextProperty = DependencyProperty.Register(nameof(TraverseText), typeof(string), typeof(NeedleMotionDiagram), TextMeta());
    public static readonly DependencyProperty ApproachTextProperty = DependencyProperty.Register(nameof(ApproachText), typeof(string), typeof(NeedleMotionDiagram), TextMeta());
    public static readonly DependencyProperty SafetyTextProperty = DependencyProperty.Register(nameof(SafetyText), typeof(string), typeof(NeedleMotionDiagram), TextMeta());
    public static readonly DependencyProperty FinalTextProperty = DependencyProperty.Register(nameof(FinalText), typeof(string), typeof(NeedleMotionDiagram), TextMeta());
    public static readonly DependencyProperty Final2TextProperty = DependencyProperty.Register(nameof(Final2Text), typeof(string), typeof(NeedleMotionDiagram), TextMeta());
    public static readonly DependencyProperty ShotTextProperty = DependencyProperty.Register(nameof(ShotText), typeof(string), typeof(NeedleMotionDiagram), TextMeta());
    public static readonly DependencyProperty LiftTextProperty = DependencyProperty.Register(nameof(LiftText), typeof(string), typeof(NeedleMotionDiagram), TextMeta());
    public static readonly DependencyProperty BackTextProperty = DependencyProperty.Register(nameof(BackText), typeof(string), typeof(NeedleMotionDiagram), TextMeta());

    /// <summary>Stage 8 climbs Z all the way to the camera's Z before moving sideways (the dispense
    /// order — a real part may stand between the dot and the camera). False = one diagonal move.</summary>
    public static readonly DependencyProperty ClimbFirstProperty = DependencyProperty.Register(
        nameof(ClimbFirst), typeof(bool), typeof(NeedleMotionDiagram),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Stage 8 includes the camera traverse back over the dot.</summary>
    public static readonly DependencyProperty ShowBackProperty = DependencyProperty.Register(
        nameof(ShowBack), typeof(bool), typeof(NeedleMotionDiagram),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public string TraverseText { get => (string)GetValue(TraverseTextProperty); set => SetValue(TraverseTextProperty, value); }
    public string ApproachText { get => (string)GetValue(ApproachTextProperty); set => SetValue(ApproachTextProperty, value); }
    public string SafetyText { get => (string)GetValue(SafetyTextProperty); set => SetValue(SafetyTextProperty, value); }
    public string FinalText { get => (string)GetValue(FinalTextProperty); set => SetValue(FinalTextProperty, value); }
    public string Final2Text { get => (string)GetValue(Final2TextProperty); set => SetValue(Final2TextProperty, value); }
    public string ShotText { get => (string)GetValue(ShotTextProperty); set => SetValue(ShotTextProperty, value); }
    public string LiftText { get => (string)GetValue(LiftTextProperty); set => SetValue(LiftTextProperty, value); }
    public string BackText { get => (string)GetValue(BackTextProperty); set => SetValue(BackTextProperty, value); }
    public bool ClimbFirst { get => (bool)GetValue(ClimbFirstProperty); set => SetValue(ClimbFirstProperty, value); }
    public bool ShowBack { get => (bool)GetValue(ShowBackProperty); set => SetValue(ShowBackProperty, value); }

    // The X axis stretches with the width (so the regions line up with the columns below);
    // Y follows it within a band, so the drawing stays legible in a narrow or a wide window.
    private double Sx => ActualWidth / DesignWidth;
    private double Sy => Math.Clamp(Sx, 0.7, 1.0);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? DesignWidth : availableSize.Width;
        return new Size(w, DesignHeight * Math.Clamp(w / DesignWidth, 0.7, 1.0));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth < 50) return;
        var dc = drawingContext;
        var p = new Painter(this, dc);

        // The region boundaries, faint: the caller's grid columns continue them.
        double bu = 0;
        for (int i = 0; i + 1 < RegionWeights.Length; i++)
        {
            bu += RegionWeights[i];
            p.Line(bu, 0, bu, DesignHeight, p.Edge, 1);
        }

        // ---- 1  Traverse ----
        p.Needle(40, YT);
        p.Arrow(40, YT, 222, YT);
        p.Needle(238, YT);
        p.Label(30, YT + 12, "1 traverse", TraverseText);

        // ---- 2-4  Down ----
        p.Needle(290, YD);
        p.Line(270, YT, 340, YT, p.Subtle, 1);
        p.Arrow(320, YT, 320, YD);
        p.Label(332, YT + 18, "2 slide down");

        p.Line(270, YD, 340, YD, p.Subtle, 1);
        p.Arrow(290, YD + 3, 290, YS);
        p.Label(332, YD + 18, "3 approach", ApproachText);

        p.Line(270, YS, 340, YS, p.Subtle, 1);
        if (!string.IsNullOrEmpty(SafetyText)) p.TextRightAligned(248, YS, SafetyText, p.Value);
        p.Arrow(290, YS, 290, YF);
        p.Label(332, YS + 6, "4 final", FinalText, Final2Text);

        // ---- The surface ----
        p.Line(270, YF, 850, YF, p.Surface, 3);
        p.Text(300, YF + 8, "coupon / part surface", p.Subtle);

        // ---- 5-7  Shot, lift off, slide up ----
        p.Needle(620, YF);
        p.Arrow(655, YF, 655, YF - 55);
        p.Line(640, YF - 55, 760, YF - 55, p.Subtle, 1);
        p.Label(600, YF + 8, "5 shot  6 lift off", ShotText, LiftText);

        p.Needle(700, YF - 125);
        p.Arrow(740, YF - 55, 740, YF - 125);
        p.Line(680, YF - 125, 780, YF - 125, p.Subtle, 1);
        p.Label(752, YF - 105, "7 slide up");

        // ---- 8  Back to the camera ----
        if (ClimbFirst)
        {
            p.Arrow(775, YF - 125, 775, 105);
            if (ShowBack) p.Arrow(775, 105, 900, 105);
            p.Label(880, 40, ShowBack ? "8 Z up, camera back" : "8 Z up to camera Z", BackText);
        }
        else
        {
            p.Arrow(715, 225, 890, 85);
            p.Label(880, 40, "8 camera back", BackText);
        }
    }

    /// <summary>Draws in design units: X scaled by <see cref="Sx"/>, Y by <see cref="Sy"/>; pen widths, needle width and text in pixels.</summary>
    private sealed class Painter
    {
        private readonly NeedleMotionDiagram _d;
        private readonly DrawingContext _dc;
        private readonly double _sx, _sy, _dip;
        private readonly Typeface _face = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public readonly Brush Fg, Subtle, Edge, Value, Surface, Body;

        public Painter(NeedleMotionDiagram d, DrawingContext dc)
        {
            _d = d;
            _dc = dc;
            _sx = d.Sx;
            _sy = d.Sy;
            _dip = VisualTreeHelper.GetDpi(d).PixelsPerDip;
            Fg = Res("Text", SystemColors.WindowTextBrush);
            Subtle = Res("Subtle", SystemColors.GrayTextBrush);
            Edge = Res("Edge", SystemColors.ActiveBorderBrush);
            Value = Res("Accent", SystemColors.HighlightBrush);
            Surface = Value;
            Body = Res("Panel", SystemColors.ControlBrush);
        }

        private Brush Res(string key, Brush fallback) => _d.TryFindResource(key) as Brush ?? fallback;

        private Point P(double u, double y) => new(u * _sx, y * _sy);

        public void Line(double u0, double y0, double u1, double y1, Brush b, double w)
            => _dc.DrawLine(new Pen(b, w), P(u0, y0), P(u1, y1));

        /// <summary>A needle with its tip at (cx, tipY): body, taper, the needle itself.</summary>
        public void Needle(double cx, double tipY)
        {
            double x = cx * _sx;
            var pen = new Pen(Fg, 1.5);
            var body = new Rect(new Point(x - 13, (tipY - 135) * _sy), new Point(x + 13, (tipY - 75) * _sy));
            _dc.DrawRectangle(Body, pen, body);
            var taper = new StreamGeometry();
            using (var g = taper.Open())
            {
                g.BeginFigure(new Point(x - 13, (tipY - 75) * _sy), true, true);
                g.LineTo(new Point(x + 13, (tipY - 75) * _sy), true, false);
                g.LineTo(new Point(x, (tipY - 25) * _sy), true, false);
            }
            taper.Freeze();
            _dc.DrawGeometry(Body, pen, taper);
            _dc.DrawLine(new Pen(Fg, 2), new Point(x, (tipY - 25) * _sy), new Point(x, tipY * _sy));
        }

        public void Arrow(double u0, double y0, double u1, double y1)
        {
            Point a = P(u0, y0), b = P(u1, y1);
            _dc.DrawLine(new Pen(Fg, 1.5), a, b);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            dx /= len;
            dy /= len;
            const double h = 9;
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(b, true, true);
                g.LineTo(new Point(b.X - dx * h - dy * h * 0.5, b.Y - dy * h + dx * h * 0.5), true, false);
                g.LineTo(new Point(b.X - dx * h + dy * h * 0.5, b.Y - dy * h - dx * h * 0.5), true, false);
            }
            head.Freeze();
            _dc.DrawGeometry(Fg, null, head);
        }

        private FormattedText Format(string s, Brush b)
            => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _face, FontPx, b, _dip);

        public void Text(double u, double y, string s, Brush b) => _dc.DrawText(Format(s, b), P(u, y));

        public void TextRightAligned(double u, double yMid, string s, Brush b)
        {
            var ft = Format(s, b);
            var at = P(u, yMid);
            _dc.DrawText(ft, new Point(at.X - ft.Width, at.Y - ft.Height / 2));
        }

        /// <summary>A stage name, then its value lines (empty ones skipped).</summary>
        public void Label(double u, double y, string name, params string?[] values)
        {
            var at = P(u, y);
            var ft = Format(name, Fg);
            _dc.DrawText(ft, at);
            double yy = at.Y + ft.Height;
            foreach (var v in values)
            {
                if (string.IsNullOrEmpty(v)) continue;
                var fv = Format(v, Value);
                _dc.DrawText(fv, new Point(at.X, yy));
                yy += fv.Height;
            }
        }
    }
}

/// <summary>
/// The last fraction of a millimetre, magnified (FireDog's dispense-helper close-up): where the
/// needle STOPS relative to where it would TOUCH, and where that touch comes from.
/// <code>
///   calibrated touch = focused camera Z + dZ     (the machine's answer)
///   touch            = calibrated touch + trim   (+ = closer to the part)
///   stop             = touch − gap               (the standoff)
/// </code>
/// Not to scale — both arrows are always drawn so both corrections are visible.
/// </summary>
public sealed class GapTrimDiagram : FrameworkElement
{
    public static readonly DependencyProperty GapMmProperty = DependencyProperty.Register(
        nameof(GapMm), typeof(double), typeof(GapTrimDiagram),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrimMmProperty = DependencyProperty.Register(
        nameof(TrimMm), typeof(double), typeof(GapTrimDiagram),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double GapMm { get => (double)GetValue(GapMmProperty); set => SetValue(GapMmProperty, value); }
    public double TrimMm { get => (double)GetValue(TrimMmProperty); set => SetValue(TrimMmProperty, value); }

    private const double LineH = 16;

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 260 : availableSize.Width, 3 * LineH + 54);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        double w = ActualWidth;
        if (w < 60) return;
        var fg = TryFindResource("Text") as Brush ?? SystemColors.WindowTextBrush;
        var dim = TryFindResource("Subtle") as Brush ?? SystemColors.GrayTextBrush;
        var val = TryFindResource("Accent") as Brush ?? SystemColors.HighlightBrush;
        var body = TryFindResource("Panel") as Brush ?? SystemColors.ControlBrush;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText F(string s, Brush b) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 11.5, b, dip);

        double yStop = LineH + 10, yCal = yStop + LineH + 14, yTouch = yCal + LineH + 14;
        double x0 = 4, x1 = w - 4, xNeedle = 16, xGap = 36, xTrim = 56, xText = 70;

        void Dashed(double y, Brush b)
        {
            var pen = new Pen(b, 1) { DashStyle = new DashStyle(new[] { 5.0, 5.0 }, 0) };
            dc.DrawLine(pen, new Point(x0, y), new Point(x1, y));
        }
        void Span(double x, double ya, double yb, Brush b)
        {
            dc.DrawLine(new Pen(b, 1.5), new Point(x, ya), new Point(x, yb));
            Tri(x, ya, +1, b);
            Tri(x, yb, -1, b);
        }
        void Tri(double x, double y, int dir, Brush b)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, y), true, true);
                c.LineTo(new Point(x - 4, y + dir * 7), true, false);
                c.LineTo(new Point(x + 4, y + dir * 7), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(b, null, g);
        }

        // The needle tip, stopped.
        var tip = new StreamGeometry();
        using (var c = tip.Open())
        {
            c.BeginFigure(new Point(xNeedle - 8, yStop - 26), true, true);
            c.LineTo(new Point(xNeedle + 8, yStop - 26), true, false);
            c.LineTo(new Point(xNeedle, yStop - 10), true, false);
        }
        tip.Freeze();
        dc.DrawGeometry(body, new Pen(fg, 1), tip);
        dc.DrawLine(new Pen(fg, 2), new Point(xNeedle, yStop - 10), new Point(xNeedle, yStop));

        // The three heights, top to bottom, each named just above its line.
        Dashed(yStop, val);
        var t1 = F(string.Format(CultureInfo.CurrentCulture, "stop: gap {0:F3} mm above the touch", GapMm), val);
        dc.DrawText(t1, new Point(xText, yStop - t1.Height - 1));
        Dashed(yCal, dim);
        var t2 = F("calibrated touch: focus Z + dZ", dim);
        dc.DrawText(t2, new Point(xText, yCal - t2.Height - 1));
        dc.DrawLine(new Pen(val, 3), new Point(x0, yTouch), new Point(x1, yTouch));
        var t3 = F(string.Format(CultureInfo.CurrentCulture, "touch: calibrated {0:+0.000;-0.000;0.000} mm trim", TrimMm), fg);
        dc.DrawText(t3, new Point(xText, yTouch - t3.Height - 1));
        var surface = val.Clone();
        surface.Opacity = 0.25;
        dc.DrawRectangle(surface, null, new Rect(new Point(x0, yTouch + 2), new Point(x1, yTouch + 10)));

        Span(xGap, yStop, yTouch, val);
        Span(xTrim, yCal, yTouch, fg);
    }
}
