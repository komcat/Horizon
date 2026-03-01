using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Horizon.Core;

namespace Horizon.App;

public partial class CurrentReadingChart : UserControl
{
    private const int MaxCapacity = 500;
    private const double TimeWindowSeconds = 10.0;
    private const int MaxChannels = 2;

    private MeasurementHub? _hub;
    private List<string>? _channelKeys;
    private DispatcherTimer? _renderTimer;

    // Pre-allocated snapshot arrays — one per channel (zero GC per frame)
    private readonly double[][] _snapTimestamps;
    private readonly double[][] _snapValues;

    // Visual elements
    private readonly Polyline[] _channelLines;
    private readonly Line[] _gridLines = new Line[3];
    private readonly TextBlock[] _yLabels = new TextBlock[4]; // top, 2 mid, bottom
    private readonly TextBlock[] _xLabels = new TextBlock[3]; // left(-10s), mid(-5s), right(0s)

    private static readonly Brush[] NormalColors = { Brushes.Cyan, Brushes.Orange };
    private static readonly Brush[] OverlayColors =
    {
        new SolidColorBrush(Color.FromRgb(0, 255, 255)),
        new SolidColorBrush(Color.FromRgb(255, 180, 0))
    };

    public Func<double, string>? YAxisFormatter { get; set; }

    private bool _isOverlayMode;
    public bool IsOverlayMode
    {
        get => _isOverlayMode;
        set
        {
            _isOverlayMode = value;
            UpdateLineAppearance();
        }
    }

    public CurrentReadingChart()
    {
        _snapTimestamps = new double[MaxChannels][];
        _snapValues = new double[MaxChannels][];
        for (int i = 0; i < MaxChannels; i++)
        {
            _snapTimestamps[i] = new double[MaxCapacity];
            _snapValues[i] = new double[MaxCapacity];
        }

        _channelLines = new Polyline[MaxChannels];

        InitializeComponent();
        BuildVisuals();
    }

    private void BuildVisuals()
    {
        // Grid lines (horizontal)
        for (int i = 0; i < 3; i++)
        {
            _gridLines[i] = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            ChartCanvas.Children.Add(_gridLines[i]);
        }

        // Channel polylines
        for (int ch = 0; ch < MaxChannels; ch++)
        {
            _channelLines[ch] = new Polyline
            {
                StrokeLineJoin = PenLineJoin.Round,
                StrokeThickness = 1
            };
            ChartCanvas.Children.Add(_channelLines[ch]);
        }

        UpdateLineAppearance();

        // Y-axis labels
        for (int i = 0; i < 4; i++)
        {
            _yLabels[i] = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                TextAlignment = TextAlignment.Right,
                Width = 50
            };
            YAxisCanvas.Children.Add(_yLabels[i]);
        }

        // X-axis labels
        string[] xTexts = { "-10s", "-5s", "0s" };
        for (int i = 0; i < 3; i++)
        {
            _xLabels[i] = new TextBlock
            {
                Text = xTexts[i],
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
            };
            XAxisCanvas.Children.Add(_xLabels[i]);
        }
    }

    private void UpdateLineAppearance()
    {
        var colors = _isOverlayMode ? OverlayColors : NormalColors;
        double thickness = _isOverlayMode ? 2.0 : 1.0;

        for (int ch = 0; ch < MaxChannels; ch++)
        {
            if (_channelLines[ch] == null) continue;
            _channelLines[ch].Stroke = colors[ch];
            _channelLines[ch].StrokeThickness = thickness;
        }
    }

    public void Attach(MeasurementHub hub, List<string> channelKeys)
    {
        _hub = hub;
        _channelKeys = channelKeys;
    }

    public void Detach()
    {
        StopRendering();
        _hub = null;
        _channelKeys = null;
        for (int ch = 0; ch < MaxChannels; ch++)
            _channelLines[ch].Points = new PointCollection();
    }

    public void StartRendering()
    {
        if (_renderTimer != null) return;
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += RenderFrame;
        _renderTimer.Start();
    }

    public void StopRendering()
    {
        if (_renderTimer == null) return;
        _renderTimer.Stop();
        _renderTimer.Tick -= RenderFrame;
        _renderTimer = null;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (_hub == null || _channelKeys == null || _channelKeys.Count == 0) return;

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w < 1 || h < 1) return;

        int channelCount = Math.Min(_channelKeys.Count, MaxChannels);

        // Snapshot each channel independently
        int[] counts = new int[channelCount];
        for (int ch = 0; ch < channelCount; ch++)
        {
            var channel = _hub.GetChannel(_channelKeys[ch]);
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

        double tMin = tMax - TimeWindowSeconds;

        // Find Y min/max across all channels within the visible time window
        double yMin = double.MaxValue;
        double yMax = double.MinValue;
        int[] visibleStarts = new int[channelCount];

        for (int ch = 0; ch < channelCount; ch++)
        {
            visibleStarts[ch] = counts[ch];
            for (int i = 0; i < counts[ch]; i++)
            {
                if (_snapTimestamps[ch][i] < tMin) continue;
                if (visibleStarts[ch] == counts[ch]) visibleStarts[ch] = i;

                double v = _snapValues[ch][i];
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }
        }

        if (yMin > yMax) return; // no visible data

        // Auto-scale with 10% padding
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

        // Map points for each channel
        for (int ch = 0; ch < channelCount; ch++)
        {
            int vs = visibleStarts[ch];
            int cnt = counts[ch];
            var pts = new PointCollection(cnt - vs);
            for (int i = vs; i < cnt; i++)
            {
                double x = ((_snapTimestamps[ch][i] - tMin) / TimeWindowSeconds) * w;
                double y = h - ((_snapValues[ch][i] - yMin) / yRange) * h;
                pts.Add(new Point(x, y));
            }
            _channelLines[ch].Points = pts;
        }

        // Hide unused channels
        for (int ch = channelCount; ch < MaxChannels; ch++)
            _channelLines[ch].Points = new PointCollection();

        // Update grid lines (25%, 50%, 75% of height)
        for (int i = 0; i < 3; i++)
        {
            double y = h * (i + 1) / 4.0;
            _gridLines[i].X1 = 0;
            _gridLines[i].X2 = w;
            _gridLines[i].Y1 = y;
            _gridLines[i].Y2 = y;
        }

        // Update Y-axis labels (top=yMax, 25%, 50%, 75%, bottom=yMin → we show 4 labels)
        double[] yPositions = { 2, h * 0.25, h * 0.5, h * 0.75 };
        double[] yValues = { yMax, yMin + yRange * 0.75, yMin + yRange * 0.5, yMin + yRange * 0.25 };
        for (int i = 0; i < 4; i++)
        {
            _yLabels[i].Text = (YAxisFormatter ?? FormatCurrentSI)(yValues[i]);
            Canvas.SetLeft(_yLabels[i], 0);
            Canvas.SetTop(_yLabels[i], yPositions[i] - 6);
        }

        // Update X-axis labels
        Canvas.SetLeft(_xLabels[0], 0);
        Canvas.SetTop(_xLabels[0], 1);
        Canvas.SetLeft(_xLabels[1], w / 2 - 12);
        Canvas.SetTop(_xLabels[1], 1);
        Canvas.SetLeft(_xLabels[2], w - 16);
        Canvas.SetTop(_xLabels[2], 1);
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Force immediate re-render on resize
        RenderFrame(null, EventArgs.Empty);
    }

    private static string FormatCurrentSI(double amps)
    {
        var absVal = Math.Abs(amps);

        if (absVal < 1e-12)
            return "0 A";
        if (absVal < 1e-9)
            return $"{amps * 1e12:F1} pA";
        if (absVal < 1e-6)
            return $"{amps * 1e9:F1} nA";
        if (absVal < 1e-3)
            return $"{amps * 1e6:F1} uA";
        if (absVal < 1.0)
            return $"{amps * 1e3:F1} mA";

        return $"{amps:F3} A";
    }
}
