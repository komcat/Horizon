using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.Calibration;

namespace Horizon.Station.UI.Views;

/// <summary>
/// Camera→needle calibration panel. Runs the Layer 2 routine
/// (<see cref="IProcessService.RunCameraNeedleCalibrationAsync(IOperatorPrompt, CancellationToken)"/>)
/// and serves as its non-modal <see cref="IOperatorPrompt"/> — each routine confirm
/// gate shows a green/red banner on the live feed while the operator jogs (left rail)
/// or MoveClicks to satisfy it. Mirrors the Run page's camera + crosshair + MoveClick
/// and the AlignmentPanel's TaskCompletionSource prompt pattern.
/// </summary>
public partial class CameraNeedleCalibrationView : UserControl, IOperatorPrompt
{
    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyList<string> _cameras;
    private readonly string? _mainCamera;

    private readonly object _frameLock = new();
    private readonly Dictionary<string, CameraFrame> _pendingFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WriteableBitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private bool _renderScheduled;
    private bool _active;
    private bool _subscribed;

    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _pending;

    /// <summary>Raised when the operator presses Back (only while idle).</summary>
    public event Action? BackRequested;

    public CameraNeedleCalibrationView(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        InitializeComponent();

        _cameras = host.Engine.Camera.CameraIds;
        _mainCamera = _cameras.Count > 0 ? _cameras[0] : null;
        MainLabel.Text = _mainCamera ?? "no cameras";

        MainPrompt.Affirmed += () => CompletePrompt(true);
        MainPrompt.Negated += () => CompletePrompt(false);
    }

    // --- page lifecycle ---

    public void Activate()
    {
        if (_active || _mainCamera == null) return;
        _active = true;
        _ = _host.Engine.Camera.StartGrabbingAsync(_mainCamera);
        if (!_subscribed)
        {
            _host.Engine.Camera.FrameReady += OnFrameReady;
            _subscribed = true;
        }
    }

    public void Deactivate()
    {
        if (!_active) return;
        _cts?.Cancel();
        if (_subscribed)
        {
            _host.Engine.Camera.FrameReady -= OnFrameReady;
            _subscribed = false;
        }
        if (_mainCamera != null) _ = _host.Engine.Camera.StopGrabbingAsync(_mainCamera);
        _active = false;
    }

    // --- run / cancel / back ---

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        if (_mainCamera == null) { SetStatus("No camera available.", true); return; }

        ResultText.Text = "—";
        SetStatus("Running…");
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BackButton.IsEnabled = false;

        _cts = new CancellationTokenSource();
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Camera-needle calibration");
        try
        {
            var r = await _host.Process.RunCameraNeedleCalibrationAsync(this, _cts.Token);
            if (r.IsSuccess)
            {
                var v = r.Value;
                SetStatus("Saved.");
                StepText.Text = "Calibration complete and saved to camera_needle_calibration.json.";
                ResultText.Text =
                    $"vcamneedle = ({v.VcamneedleX:F4}, {v.VcamneedleY:F4}) mm\n" +
                    $"focus → needle Z = {v.FocusToNeedleZmm:F4} mm";
            }
            else
            {
                SetStatus(r.Message, true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            activity.Dispose();
            MainPrompt.Hide();
            _pending = null;
            _cts.Dispose();
            _cts = null;
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            BackButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_cts == null) BackRequested?.Invoke();
    }

    // --- IOperatorPrompt: non-modal inline confirm via the camera overlay ---

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetResult(false));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        // The routine awaits from a background thread — set up the overlay on the UI thread.
        Dispatcher.Invoke(() =>
        {
            _pending = tcs;
            StepText.Text = message;
            SetStatus(title + "…");
            MainPrompt.Show(message, "Confirm", "Cancel");
        });

        return tcs.Task;
    }

    private void CompletePrompt(bool ok)
    {
        var pending = _pending;
        if (pending == null) return;
        _pending = null;
        MainPrompt.Hide();
        pending.TrySetResult(ok);
    }

    // --- MoveClick: click the feed to jog from the crosshair centre ---

    private async void OnFeedClick(object sender, MouseButtonEventArgs e)
    {
        if (MoveToggle.IsChecked != true || _mainCamera == null) return;
        if (!_bitmaps.TryGetValue(_mainCamera, out var bmp)) return;

        var m = MoveClick.Measure(_host, e.GetPosition(MainImage),
            MainImage.ActualWidth, MainImage.ActualHeight, bmp.PixelWidth, bmp.PixelHeight);
        if (m is not { } meas) return;

        MoveReadout.Text = meas.Readout;
        MoveReadoutBox.Visibility = Visibility.Visible;
        await MoveClick.JogAsync(_host, meas, Log);
    }

    // --- frame routing (single main camera) ---

    private void OnFrameReady(CameraFrame frame)
    {
        lock (_frameLock)
        {
            _pendingFrames[frame.Camera] = frame;
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        _dispatcher.BeginInvoke(RenderPending);
    }

    private void RenderPending()
    {
        List<CameraFrame> frames;
        lock (_frameLock)
        {
            _renderScheduled = false;
            frames = _pendingFrames.Values.ToList();
            _pendingFrames.Clear();
        }
        foreach (var frame in frames) RenderFrame(frame);
    }

    private void RenderFrame(CameraFrame frame)
    {
        if (!string.Equals(frame.Camera, _mainCamera, StringComparison.OrdinalIgnoreCase)) return;

        PixelFormat? format = frame.Channels switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Bgr24,
            4 => PixelFormats.Bgra32,
            _ => null,
        };
        if (format == null) return;

        bool created = false;
        if (!_bitmaps.TryGetValue(frame.Camera, out var bmp) ||
            bmp.PixelWidth != frame.Width || bmp.PixelHeight != frame.Height ||
            bmp.Format != format.Value)
        {
            bmp = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format.Value, null);
            _bitmaps[frame.Camera] = bmp;
            created = true;
        }

        try
        {
            bmp.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Data, frame.Width * frame.Channels, 0);
        }
        catch
        {
            return;
        }

        if (created)
        {
            MainImage.Source = bmp;
            NoVideo.Visibility = Visibility.Collapsed;
        }
    }

    private void Log(string message)
        => _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Cal", message));

    private void SetStatus(string text, bool warn = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(warn ? "Danger" : "Subtle");
    }
}
