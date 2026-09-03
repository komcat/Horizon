using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FAAM.UI.ViewModels;
using FAAM.UI.ViewModels.Sections;
using Horizon.Engine;
using Horizon.Workflow.Registration;

namespace FAAM.UI.Views;

/// <summary>
/// The Part Alignment tab's view — feed geometry (MoveClick jog, one Mark click routed to the
/// panel's chosen target) plus the unsaved edit markers: E1 / E2 as taught here and the selected
/// object's points, drawn cyan and glued to their gantry-frame spot. Saved parts are drawn by the
/// shared <see cref="ToolOverlayView"/>. A ~5 Hz timer refreshes the model while shown.
/// </summary>
public partial class PartAlignmentView : UserControl
{
    private static readonly Brush PartBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));

    private readonly PartAlignmentViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<UIElement> _marks = new();
    private bool _active;

    public PartAlignmentView(EngineHost host)
    {
        _vm = new PartAlignmentViewModel(host);
        DataContext = _vm;
        InitializeComponent();

        _timer.Tick += (_, _) => _vm.Refresh();
        _vm.Ticked += Redraw;
        MainImage.SizeChanged += (_, _) => Redraw();
        _vm.Camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CameraSectionViewModel.SelectedCamera))
                ToolOverlay.Camera = _vm.Camera.SelectedCamera;
        };
        ToolOverlay.Attach(host, MainImage, cover: false);
        ToolOverlay.Camera = _vm.Camera.SelectedCamera;

        // Marker placement: the panel's Mark buttons (and the run's manual points) arm the
        // placer; a feed click drops the crosshair; Confirm commits the point.
        Placer.Attach(MainImage);
        Placer.Confirmed += OnPlacementConfirmed;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PartAlignmentViewModel.MarkTarget)) return;
            if (_vm.MarkArmed) Placer.Arm(_vm.MarkTarget); else Placer.Disarm();
        };
    }

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

    private void OnMoveArmed(object sender, RoutedEventArgs e) => MarkToggle.IsChecked = false;
    private void OnMarkArmed(object sender, RoutedEventArgs e) => MoveToggle.IsChecked = false;

    private async void OnFeedClick(object sender, MouseButtonEventArgs e)
    {
        if (MainImage.Source is not BitmapSource bmp) return;
        var p = e.GetPosition(MainImage);
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        if (p.X < 0 || p.Y < 0 || p.X > availW || p.Y > availH) return;

        if (_vm.MarkArmed)
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
                host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Part", msg)));
        }
    }

    private async void OnPlacementConfirmed(Point p)
    {
        if (MainImage.Source is not BitmapSource bmp) return;
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        double scale = MoveClick.DisplayScale(availW, availH, bmp.PixelWidth, bmp.PixelHeight, cover: false);
        if (scale <= 0) return;
        var (dx, dy) = ToolOverlayMath.FromDisplay(p, availW, availH, scale);
        await _vm.MarkAsync(bmp.PixelWidth / 2.0 + dx, bmp.PixelHeight / 2.0 + dy, bmp.PixelWidth, bmp.PixelHeight);
        Redraw();
    }

    /// <summary>Draws the taught (unsaved) E1 / E2 and the selected object's points at their current on-screen place.</summary>
    private void Redraw()
    {
        foreach (var m in _marks) EditOverlay.Children.Remove(m);
        _marks.Clear();

        if (MainImage.Source is not BitmapSource bmp) return;
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        if (availW <= 0 || availH <= 0) return;
        double scale = MoveClick.DisplayScale(availW, availH, bmp.PixelWidth, bmp.PixelHeight, cover: false);
        if (scale <= 0) return;
        var cam = _vm.Host.Engine.Motion.GetState(_vm.CameraDevice);
        if (!cam.Valid) return;
        var settings = _vm.Host.Settings.Current;
        EditOverlay.Width = availW;
        EditOverlay.Height = availH;

        Point? Place(double gx, double gy)
        {
            var px = ToolOverlayMath.MmToSourcePx(gx - cam.Position.X, gy - cam.Position.Y, settings);
            return px is { } o ? ToolOverlayMath.ToDisplay(o.DxPx, o.DyPx, availW, availH, scale) : null;
        }

        foreach (var (label, gx, gy, fiducial) in _vm.EditMarkers())
        {
            if (Place(gx, gy) is not { } d) continue;
            Shape mark = fiducial
                ? new Polygon
                {
                    Stroke = PartBrush, StrokeThickness = 2, Fill = Brushes.Transparent,
                    Points = new PointCollection { new(d.X, d.Y - 7), new(d.X + 7, d.Y), new(d.X, d.Y + 7), new(d.X - 7, d.Y) },
                }
                : new Ellipse { Width = 9, Height = 9, Stroke = PartBrush, StrokeThickness = 2, Fill = Brushes.Transparent };
            if (!fiducial) { Canvas.SetLeft(mark, d.X - 4.5); Canvas.SetTop(mark, d.Y - 4.5); }
            var tag = new TextBlock { Text = label, Foreground = PartBrush, FontSize = 11, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(tag, d.X + 8); Canvas.SetTop(tag, d.Y - 16);
            EditOverlay.Children.Add(mark); EditOverlay.Children.Add(tag);
            _marks.Add(mark); _marks.Add(tag);
        }
    }
}
