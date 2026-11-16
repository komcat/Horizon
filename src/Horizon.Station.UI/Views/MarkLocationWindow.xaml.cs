using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.Workflow;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The manual fallback of the Detect Object block (<see cref="IOperatorPrompt.MarkLocationAsync"/>):
/// a live feed of the block's camera with the centre crosshair, on which the operator clicks the
/// required location with the standard <see cref="MarkerPlacer"/> gesture (click, arrows nudge
/// one pixel, Confirm). <see cref="Finished"/> fires once — with the marked point in source
/// pixels, or null when the operator cancels or closes the window. The window holds its own
/// camera grab lease for as long as it is open.
/// </summary>
public partial class MarkLocationWindow : Window
{
    private readonly EngineHost _host;
    private readonly string _camera;
    private readonly Dispatcher _dispatcher;
    private readonly object _frameLock = new();
    private CameraFrame? _pendingFrame;
    private bool _renderScheduled;
    private WriteableBitmap? _bmp;
    private bool _leased;
    private bool _finished;

    /// <summary>The operator's answer — raised exactly once.</summary>
    public event Action<MarkedLocation?>? Finished;

    public MarkLocationWindow(MarkLocationRequest request, EngineHost host)
    {
        _host = host;
        _camera = request.CameraId;
        _dispatcher = Dispatcher.CurrentDispatcher;
        InitializeComponent();

        PromptTitle.Text = string.IsNullOrWhiteSpace(request.Title) ? "Mark location" : request.Title;
        PromptMessage.Text = request.Message;
        CameraText.Text = $"Camera {_camera} — click the image to place the marker, arrow keys nudge it one pixel, Enter / Confirm accepts.";

        Placer.Attach(FeedImage);
        Placer.Confirmed += OnConfirmed;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Placer.Arm("the location");
        _host.Engine.Camera.FrameReady += OnFrameReady;
        try
        {
            var r = await _host.Engine.Camera.StartGrabbingAsync(_camera);
            _leased = r.IsSuccess;
            if (!r.IsSuccess) NoVideo.Text = $"Camera {_camera}: {r.Message}";
        }
        catch (Exception ex) { NoVideo.Text = $"Camera {_camera}: {ex.Message}"; }

        // Closed while the grab was starting — give the lease straight back.
        if (_finished && _leased)
        {
            _leased = false;
            _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _host.Engine.Camera.FrameReady -= OnFrameReady;
        Placer.Confirmed -= OnConfirmed;
        if (_leased)
        {
            _leased = false;
            _ = _host.Engine.Camera.StopGrabbingAsync(_camera);
        }
        Finish(null);   // closed by the title bar / a run Stop = no location
    }

    private void Finish(MarkedLocation? mark)
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(mark);
    }

    // --- feed ---

    private void OnFrameReady(CameraFrame frame)
    {
        if (!string.Equals(frame.Camera, _camera, StringComparison.OrdinalIgnoreCase)) return;
        lock (_frameLock)
        {
            _pendingFrame = frame;
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        _dispatcher.BeginInvoke(RenderPending);
    }

    private void RenderPending()
    {
        CameraFrame? frame;
        lock (_frameLock)
        {
            _renderScheduled = false;
            frame = _pendingFrame;
            _pendingFrame = null;
        }
        if (frame == null || _finished) return;

        PixelFormat? format = frame.Channels switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Bgr24,
            4 => PixelFormats.Bgra32,
            _ => null,
        };
        if (format == null || frame.Width <= 0 || frame.Height <= 0) return;

        if (_bmp == null || _bmp.PixelWidth != frame.Width || _bmp.PixelHeight != frame.Height || _bmp.Format != format.Value)
        {
            _bmp = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format.Value, null);
            FeedImage.Source = _bmp;
        }
        try { _bmp.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, frame.Width * frame.Channels, 0); }
        catch (ArgumentException) { return; }   // malformed buffer — skip the frame
        NoVideo.Visibility = Visibility.Collapsed;
    }

    // --- marking ---

    private void OnFeedClick(object sender, MouseButtonEventArgs e)
    {
        if (FeedImage.Source is not BitmapSource) return;
        var p = e.GetPosition(FeedImage);
        if (p.X < 0 || p.Y < 0 || p.X > FeedImage.ActualWidth || p.Y > FeedImage.ActualHeight) return;
        Placer.Place(p);   // committed on Confirm
    }

    private void OnConfirmed(Point p)
    {
        if (FeedImage.Source is not BitmapSource bmp) return;
        double availW = FeedImage.ActualWidth, availH = FeedImage.ActualHeight;
        double scale = MoveClick.DisplayScale(availW, availH, bmp.PixelWidth, bmp.PixelHeight, cover: false);
        if (scale <= 0) return;
        var (dx, dy) = ToolOverlayMath.FromDisplay(p, availW, availH, scale);
        Finish(new MarkedLocation(bmp.PixelWidth / 2.0 + dx, bmp.PixelHeight / 2.0 + dy, bmp.PixelWidth, bmp.PixelHeight));
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Finish(null);
        Close();
    }
}
