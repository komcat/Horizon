using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Horizon.Station.UI.ViewModels;
using Horizon.Station.UI.ViewModels.Sections;
using Horizon.Engine;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The Gripped FAU page's view — owns the feed geometry (MoveClick jog, Mark 1 / Mark 2 point
/// clicks, outline corner clicks) and draws the taught points, centre and outline preview in
/// purple over the live image, glued to the physical spot as the camera moves. The live glyphs
/// (tool + saved objects) come from the shared <see cref="ToolOverlayView"/>. A ~5 Hz timer
/// drives the model's refresh while the page is shown.
/// </summary>
public partial class GrippedObjectView : UserControl
{
    private static readonly Brush ObjBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x70, 0xFF));
    private static readonly Brush OutlineBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));

    private readonly GrippedObjectViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<UIElement> _marks = new();
    private readonly Rectangle _preview = new()
    {
        Stroke = OutlineBrush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 },
        Fill = Brushes.Transparent, Visibility = Visibility.Collapsed,
    };
    private readonly Line _cH = new() { Stroke = ObjBrush, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Line _cV = new() { Stroke = ObjBrush, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private bool _active;

    public GrippedObjectView(EngineHost host)
    {
        _vm = new GrippedObjectViewModel(host);
        DataContext = _vm;
        InitializeComponent();
        PointOverlay.Children.Add(_preview);
        PointOverlay.Children.Add(_cH);
        PointOverlay.Children.Add(_cV);

        _timer.Tick += (_, _) => _vm.Refresh();
        _vm.Ticked += RedrawPoints;
        MainImage.SizeChanged += (_, _) => RedrawPoints();
        _vm.Camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CameraSectionViewModel.SelectedCamera))
                ToolOverlay.Camera = _vm.Camera.SelectedCamera;
        };
        ToolOverlay.Attach(host, MainImage, cover: false);
        ToolOverlay.Camera = _vm.Camera.SelectedCamera;

        // Marker placement (points + outline corners): armed by the toggles / the procedure's
        // manual step; a feed click drops the crosshair; Confirm commits.
        Placer.Attach(MainImage);
        Placer.Confirmed += OnPlacementConfirmed;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GrippedObjectViewModel.Mark1Armed) or nameof(GrippedObjectViewModel.Mark2Armed)
                or nameof(GrippedObjectViewModel.PickOutlineArmed))
                SyncPlacer();
        };
    }

    private void SyncPlacer()
    {
        if (_vm.MarkArmed != 0) Placer.Arm($"point {_vm.MarkArmed}");
        else if (_vm.PickOutlineArmed) Placer.Arm("outline corner");
        else Placer.Disarm();
    }

    // --- page lifecycle (shell) ---

    public void Activate()
    {
        if (_active) return;
        _active = true;
        _vm.SetActive(true);
        ToolOverlay.Camera = _vm.Camera.SelectedCamera;
        ToolOverlay.SetActive(true);
        _timer.Start();
        _vm.Refresh();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _timer.Stop();
        ToolOverlay.SetActive(false);
        _vm.CancelProcedure();
        _vm.SetActive(false);
    }

    // Exclusive feed modes.
    private void OnMoveArmed(object sender, RoutedEventArgs e) { Mark1Toggle.IsChecked = false; Mark2Toggle.IsChecked = false; OutlineToggle.IsChecked = false; }
    private void OnMarkArmed(object sender, RoutedEventArgs e) { MoveToggle.IsChecked = false; OutlineToggle.IsChecked = false; }
    private void OnOutlineArmed(object sender, RoutedEventArgs e) { MoveToggle.IsChecked = false; Mark1Toggle.IsChecked = false; Mark2Toggle.IsChecked = false; }

    private async void OnFeedClick(object sender, MouseButtonEventArgs e)
    {
        if (MainImage.Source is not BitmapSource bmp) return;
        var p = e.GetPosition(MainImage);
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        if (p.X < 0 || p.Y < 0 || p.X > availW || p.Y > availH) return;

        if (_vm.MarkArmed != 0 || OutlineToggle.IsChecked == true)
        {
            Placer.Place(p);   // committed on Confirm (OnPlacementConfirmed)
            return;
        }

        if (MoveToggle.IsChecked == true)
        {
            var host = _vm.Host;
            if (!host.Session.Has(AccessPolicy.Jog)) return;
            var m = MoveClick.Measure(host, p, availW, availH, bmp.PixelWidth, bmp.PixelHeight);
            if (m is not { } meas) return;
            await MoveClick.JogAsync(host, meas, msg =>
                host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Gripped", msg)));
        }
    }

    private async void OnPlacementConfirmed(Point p)
    {
        if (MainImage.Source is not BitmapSource bmp) return;
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        double scale = MoveClick.DisplayScale(availW, availH, bmp.PixelWidth, bmp.PixelHeight, cover: false);
        if (scale <= 0) return;
        var (dx, dy) = ToolOverlayMath.FromDisplay(p, availW, availH, scale);
        double sx = bmp.PixelWidth / 2.0 + dx, sy = bmp.PixelHeight / 2.0 + dy;
        if (_vm.MarkArmed != 0) await _vm.MarkPointAsync(sx, sy, bmp.PixelWidth, bmp.PixelHeight);
        else if (OutlineToggle.IsChecked == true) await _vm.AddOutlineCornerAsync(sx, sy, bmp.PixelWidth, bmp.PixelHeight);
        RedrawPoints();
        SyncPlacer();   // re-arm for the next corner / point, or hide when done
    }

    /// <summary>Draws the taught points, the centre and the outline preview at their current on-screen place.</summary>
    private void RedrawPoints()
    {
        foreach (var m in _marks) PointOverlay.Children.Remove(m);
        _marks.Clear();
        _preview.Visibility = _cH.Visibility = _cV.Visibility = Visibility.Collapsed;

        if (MainImage.Source is not BitmapSource bmp) return;
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        if (availW <= 0 || availH <= 0) return;
        double scale = MoveClick.DisplayScale(availW, availH, bmp.PixelWidth, bmp.PixelHeight, cover: false);
        if (scale <= 0) return;

        var cam = _vm.Host.Engine.Motion.GetState(_vm.CameraDevice);
        if (!cam.Valid) return;
        var settings = _vm.Host.Settings.Current;
        PointOverlay.Width = availW;
        PointOverlay.Height = availH;

        Point? Place(double frameX, double frameY)
        {
            var px = ToolOverlayMath.MmToSourcePx(frameX - cam.Position.X, frameY - cam.Position.Y, settings);
            return px is { } o ? ToolOverlayMath.ToDisplay(o.DxPx, o.DyPx, availW, availH, scale) : null;
        }

        foreach (var pt in _vm.Points)
        {
            if (Place(pt.CameraFrameX, pt.CameraFrameY) is not { } d) continue;
            var dot = new Ellipse { Width = 9, Height = 9, Stroke = ObjBrush, StrokeThickness = 2, Fill = Brushes.Transparent };
            Canvas.SetLeft(dot, d.X - 4.5); Canvas.SetTop(dot, d.Y - 4.5);
            var tag = new TextBlock { Text = pt.Index.ToString(), Foreground = ObjBrush, FontSize = 11, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(tag, d.X + 6); Canvas.SetTop(tag, d.Y - 14);
            PointOverlay.Children.Add(dot); PointOverlay.Children.Add(tag);
            _marks.Add(dot); _marks.Add(tag);
        }

        foreach (var corner in _vm.OutlineCorners)
        {
            if (Place(corner.X, corner.Y) is not { } d) continue;
            var sq = new Rectangle { Width = 8, Height = 8, Stroke = OutlineBrush, StrokeThickness = 2, Fill = Brushes.Transparent };
            Canvas.SetLeft(sq, d.X - 4); Canvas.SetTop(sq, d.Y - 4);
            PointOverlay.Children.Add(sq);
            _marks.Add(sq);
        }

        if (_vm.Centre is { } c && Place(c.X, c.Y) is { } cd)
        {
            _cH.X1 = cd.X - 12; _cH.X2 = cd.X + 12; _cH.Y1 = _cH.Y2 = cd.Y;
            _cV.Y1 = cd.Y - 12; _cV.Y2 = cd.Y + 12; _cV.X1 = _cV.X2 = cd.X;
            _cH.Visibility = _cV.Visibility = Visibility.Visible;

            if (settings.MmPerPixel > 0 && _vm.OutlineBox is { } box && Place(c.X + box.OffX, c.Y + box.OffY) is { } bc)
            {
                double wPx = box.W / settings.MmPerPixel * scale, hPx = box.L / settings.MmPerPixel * scale;
                _preview.Width = wPx; _preview.Height = hPx;
                Canvas.SetLeft(_preview, bc.X - wPx / 2); Canvas.SetTop(_preview, bc.Y - hPx / 2);
                _preview.Visibility = Visibility.Visible;
            }
        }
    }
}
