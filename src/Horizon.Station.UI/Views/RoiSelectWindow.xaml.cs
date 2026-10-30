using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Horizon.Engine;

namespace Horizon.Station.UI.Views;

/// <summary>
/// Modal ROI picker: shows a captured frame and lets the operator drag a free
/// rectangle. The selection is mapped from display px back to source px (the image
/// is uniform-stretched, so it is centred with letterbox margins). The chosen
/// <see cref="Roi"/> is in source-frame pixels.
/// </summary>
public partial class RoiSelectWindow : Window
{
    private readonly int _srcW;
    private readonly int _srcH;

    private bool _dragging;
    private Point _start;

    /// <summary>The selected ROI in source pixels, or null if cancelled.</summary>
    public RoiSpec? Roi { get; private set; }

    public RoiSelectWindow(BitmapSource frame, int sourceWidth, int sourceHeight)
    {
        _srcW = sourceWidth;
        _srcH = sourceHeight;
        InitializeComponent();
        FrameImage.Source = frame;
    }

    // Displayed image rectangle within the Surface grid (uniform-stretch + letterbox).
    private (double scale, double offX, double offY) DisplayMetrics()
    {
        double availW = Surface.ActualWidth, availH = Surface.ActualHeight;
        if (availW <= 0 || availH <= 0 || _srcW <= 0 || _srcH <= 0) return (0, 0, 0);
        double scale = Math.Min(availW / _srcW, availH / _srcH);
        double dispW = _srcW * scale, dispH = _srcH * scale;
        return (scale, (availW - dispW) / 2.0, (availH - dispH) / 2.0);
    }

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        var (scale, offX, offY) = DisplayMetrics();
        if (scale <= 0) return;

        _start = Clamp(e.GetPosition(Surface), offX, offY, scale);
        _dragging = true;
        Surface.CaptureMouse();
        DrawRect(_start, _start);
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var (scale, offX, offY) = DisplayMetrics();
        if (scale <= 0) return;

        DrawRect(_start, Clamp(e.GetPosition(Surface), offX, offY, scale));
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Surface.ReleaseMouseCapture();

        var (scale, offX, offY) = DisplayMetrics();
        if (scale <= 0) return;

        var end = Clamp(e.GetPosition(Surface), offX, offY, scale);
        double left = Math.Min(_start.X, end.X), top = Math.Min(_start.Y, end.Y);
        double w = Math.Abs(end.X - _start.X), h = Math.Abs(end.Y - _start.Y);

        // Map display px → source px.
        int sx = (int)Math.Round((left - offX) / scale);
        int sy = (int)Math.Round((top - offY) / scale);
        int sw = (int)Math.Round(w / scale);
        int sh = (int)Math.Round(h / scale);

        if (sw < 4 || sh < 4)
        {
            Roi = null;
            OkButton.IsEnabled = false;
            RoiReadout.Text = "Selection too small";
            RoiRect.Visibility = Visibility.Collapsed;
            return;
        }

        Roi = new RoiSpec(sx, sy, sw, sh);
        OkButton.IsEnabled = true;
        RoiReadout.Text = $"ROI  x {sx}  y {sy}  w {sw}  h {sh} px";
    }

    /// <summary>Clamps a point to the displayed-image rectangle.</summary>
    private Point Clamp(Point p, double offX, double offY, double scale)
    {
        double maxX = offX + _srcW * scale, maxY = offY + _srcH * scale;
        return new Point(Math.Clamp(p.X, offX, maxX), Math.Clamp(p.Y, offY, maxY));
    }

    private void DrawRect(Point a, Point b)
    {
        double left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        RoiRect.Margin = new Thickness(left, top, 0, 0);
        RoiRect.Width = Math.Abs(b.X - a.X);
        RoiRect.Height = Math.Abs(b.Y - a.Y);
        RoiRect.Visibility = Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Roi == null) return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Roi = null;
        DialogResult = false;
        Close();
    }
}
