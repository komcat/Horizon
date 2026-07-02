using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FAAM.UI.ViewModels.Sections;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// Pop-out live camera view — bound to the same <c>CameraSectionViewModel</c> as
/// the Machine-page Camera section (shares the one frame pipeline). Adds the same
/// MoveClick behaviour as the Run page: a center crosshair, a short cursor
/// crosshair on hover, and click-to-jog the configured stage by the real-world
/// offset from the crosshair centre to the click point.
///
/// It also mirrors the Run page's two alignment overlays while open: the vision-detection
/// flash (yellow line, for this window's camera) and — on the main-camera window — the
/// alignment capture/confirm prompt, via the shared <c>EngineHost.CapturePrompts</c> hub.
/// </summary>
public partial class DisplayWindow : Window
{
    /// <summary>Camera id treated as the "main" feed the alignment prompts belong to.</summary>
    private const string MainCameraId = "main_camera";

    private DispatcherTimer? _detectionTimer;
    private bool _wired;

    public DisplayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private EngineHost? Host => (DataContext as CameraSectionViewModel)?.Host;

    /// <summary>The camera this pop-out currently displays (drives the vision-flash filter).</summary>
    private string? SelectedCamera => (DataContext as CameraSectionViewModel)?.SelectedCamera;

    // Wire the mirrored overlays once the DataContext (and thus the host) is in place. Both
    // pop-out entry points set DataContext before Show(), so it is available by Loaded.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_wired || Host is not { } host) return;
        _wired = true;

        host.Process.VisionProfileRan += OnVisionProfileRan;   // flash detections on this feed

        // Mirror the alignment capture/confirm prompt only on the main-camera window.
        if (string.Equals(SelectedCamera, MainCameraId, StringComparison.OrdinalIgnoreCase))
            host.CapturePrompts.Add(PopoutPrompt);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Host is { } host)
        {
            host.Process.VisionProfileRan -= OnVisionProfileRan;
            host.CapturePrompts.Remove(PopoutPrompt);   // no-op if never registered
        }
        _detectionTimer?.Stop();
    }

    private void OnFeedMouseMove(object sender, MouseEventArgs e) => MoveCursor(e);
    private void OnFeedMouseLeave(object sender, MouseEventArgs e) => MainCursor.Visibility = Visibility.Collapsed;
    private void OnMoveDisarmed(object sender, RoutedEventArgs e) => MainCursor.Visibility = Visibility.Collapsed;

    /// <summary>Centres the 20×20 cursor crosshair on the mouse (only while armed).</summary>
    private void MoveCursor(MouseEventArgs e)
    {
        if (MoveToggle.IsChecked != true) { MainCursor.Visibility = Visibility.Collapsed; return; }
        var p = e.GetPosition(FeedHost);
        MainCursor.Margin = new Thickness(p.X - MainCursor.Width / 2, p.Y - MainCursor.Height / 2, 0, 0);
        MainCursor.Visibility = Visibility.Visible;
    }

    private async void OnFeedClick(object sender, MouseButtonEventArgs e)
    {
        if (MoveToggle.IsChecked != true) return;
        if (MainImage.Source is not BitmapSource bmp) return;

        var p = e.GetPosition(MainImage);
        // Ignore clicks in the letterbox margin (outside the rendered image).
        if (p.X < 0 || p.Y < 0 || p.X > MainImage.ActualWidth || p.Y > MainImage.ActualHeight) return;

        await DoMoveClickAsync(p, MainImage.ActualWidth, MainImage.ActualHeight, bmp.PixelWidth, bmp.PixelHeight);
    }

    /// <summary>Measures the click offset and jogs the configured device — shared with the Run page via <see cref="MoveClick"/>.</summary>
    private async Task DoMoveClickAsync(Point p, double availW, double availH, int srcW, int srcH)
    {
        var host = Host;
        if (host == null) return;

        var m = MoveClick.Measure(host, p, availW, availH, srcW, srcH);
        if (m is not { } meas) return;

        MoveReadout.Text = meas.Readout;
        MoveReadoutBox.Visibility = Visibility.Visible;
        Log(host, $"MoveClick (camera popout): {meas.Readout}");

        await MoveClick.JogAsync(host, meas, msg => Log(host, msg));
    }

    private static void Log(EngineHost host, string message)
        => host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Camera", message));

    // --- vision detection flash (yellow line over this feed) — mirrors RunView ---

    /// <summary>
    /// Briefly draws the just-detected facet line over the feed. Reacts only to detections on
    /// the camera this window shows and to Line results (needs x1/y1/x2/y2); NCC / circle
    /// results are ignored. The event may arrive on a background thread, so marshal first.
    /// </summary>
    private void OnVisionProfileRan(VisionProfileDetection d)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnVisionProfileRan(d)); return; }
        var cam = SelectedCamera;
        if (cam == null || !string.Equals(d.CameraId, cam, StringComparison.OrdinalIgnoreCase)) return;
        if (!d.Result.Found) return;

        double? x1 = Value("x1"), y1 = Value("y1"), x2 = Value("x2"), y2 = Value("y2");
        if (x1 is null || y1 is null || x2 is null || y2 is null) return;   // not a line detection
        ShowDetectedLine(x1.Value, y1.Value, x2.Value, y2.Value);

        double? Value(string key)
        {
            foreach (var v in d.Result.Values)
                if (string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)) return v.Value;
            return null;
        }
    }

    /// <summary>
    /// Maps the source-pixel line endpoints onto the display and shows the yellow overlay, then
    /// arms the auto-hide timer. Uses the same uniform-scale + centre mapping as MoveClick, so
    /// the drawn line lines up with what a click at that point would report.
    /// </summary>
    private void ShowDetectedLine(double sx1, double sy1, double sx2, double sy2)
    {
        if (MainImage.Source is not BitmapSource bmp) return;
        double srcW = bmp.PixelWidth, srcH = bmp.PixelHeight;
        double availW = MainImage.ActualWidth, availH = MainImage.ActualHeight;
        if (srcW <= 0 || srcH <= 0 || availW <= 0 || availH <= 0) return;

        double scale = Math.Min(availW / srcW, availH / srcH);
        double cx = availW / 2.0, cy = availH / 2.0;

        // Size the overlay to the display box so its coordinates match the mapping below.
        DetectionOverlay.Width = availW;
        DetectionOverlay.Height = availH;

        DetectedLine.X1 = cx + (sx1 - srcW / 2.0) * scale;
        DetectedLine.Y1 = cy + (sy1 - srcH / 2.0) * scale;
        DetectedLine.X2 = cx + (sx2 - srcW / 2.0) * scale;
        DetectedLine.Y2 = cy + (sy2 - srcH / 2.0) * scale;
        DetectedLine.Visibility = Visibility.Visible;

        _detectionTimer ??= CreateDetectionTimer();
        _detectionTimer.Stop();
        _detectionTimer.Start();
    }

    private DispatcherTimer CreateDetectionTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            DetectedLine.Visibility = Visibility.Collapsed;
        };
        return t;
    }
}
