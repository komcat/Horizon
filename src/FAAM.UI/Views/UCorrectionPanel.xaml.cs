using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.UCorrection;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page U-correction panel — replicates the legacy acs_solution_saa U-correction
/// UI (Move-to / Save-to P1·P2·F1·F2, Start / Capture / Clear / Apply U) but drives
/// it through the Layer 2 <see cref="IProcessService.RunUCorrectionAsync(UCorrectionOptions,IOperatorPrompt,System.Threading.CancellationToken)"/>
/// routine.
///
/// The panel <b>is</b> the routine's <see cref="IOperatorPrompt"/>: the routine moves
/// the gantry to each reference point and then blocks on <see cref="ConfirmAsync"/>;
/// the inline <c>Capture Point</c> button completes that wait (so the operator can keep
/// jogging via the live camera meanwhile — no blocking modal), and <c>Apply U</c>
/// completes the final apply confirmation. Save/Move use Layer 2 teach / recall.
/// </summary>
public partial class UCorrectionPanel : UserControl, IOperatorPrompt
{
    // FAAM rig device mapping (mirrors FaamBuiltInSteps).
    private const string GantryDevice = "gantry-main";   // ACS gantry (X/Y/Z) — the four reference points live here
    private const string HexDevice = "hex-right";        // PI hexapod — the U axis is corrected here

    // Capture order P1 → P2 → F1 → F2 (PIC line then FAU line), matching UCorrectionOptions.
    private static readonly string[] Order = { "P1", "P2", "F1", "F2" };

    private EngineHost? _host;
    private Ellipse[] _dots;

    private CancellationTokenSource? _cts;
    private IDisposable? _activity;                 // process-busy token held for the run
    private TaskCompletionSource<bool>? _pending;   // the in-flight ConfirmAsync, if any
    private bool _awaitingApply;
    private int _captureIndex;
    private bool _running;

    /// <summary>Raised whenever the run-state or status changes — the utility-app launcher badge listens.</summary>
    public event Action? RunStateChanged;

    /// <summary>True while a correction run is in progress (drives the launcher's Running badge).</summary>
    public bool IsRunning => _running;

    /// <summary>Current status line, surfaced to the launcher badge / app header.</summary>
    public string RunStatusDetail => StatusText.Text;

    private void RaiseRunState() => RunStateChanged?.Invoke();

    /// <summary>Begins the correction run (utility-app <c>app.start</c>) — same as pressing Start.</summary>
    public void RequestStart() => OnStart(this, new RoutedEventArgs());

    /// <summary>Cancels an in-flight run (utility-app <c>app.stop</c>); no-op when idle.</summary>
    public void RequestStop()
    {
        if (!_running) return;
        _cts?.Cancel();
        _pending?.TrySetResult(false);
    }

    public UCorrectionPanel()
    {
        InitializeComponent();
        _dots = new[] { DotP1, DotP2, DotF1, DotF2 };
    }

    /// <summary>Supplies the engine host (Layer 2 teach / recall / correction run + logging).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        host.Activity.Changed += OnActivityChanged;   // block Start/Move while a process step or other run holds the hexapod
        SetRefPointsCollapsed(host.Settings.Current.UCorrectionRefPointsCollapsed);   // restore last collapse state
        RefreshButtons();
    }

    private void OnActivityChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshButtons();
        else Dispatcher.BeginInvoke(RefreshButtons);
    }

    private void RefreshButtons()
    {
        RefreshStartEnabled();
        RefreshReferenceButtons();
    }

    /// <summary>Start is available only when nothing else (a process step or another alignment) holds the hexapod.</summary>
    private void RefreshStartEnabled()
    {
        if (_host == null) return;
        bool othersBusy = _host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !_running);
        StartButton.IsEnabled = !_running && !othersBusy;
    }

    private static string PositionName(string point) => $"UCorr_{point}";

    // --- Reference points: teach (Save) + recall (Move), via Layer 2 ---

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = PositionName(point);
        var velocity = _host.Process.GetPosition(GantryDevice, name)?.Velocity ?? 0;
        var r = await _host.Process.TeachPositionAsync(GantryDevice, name, velocity);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"U correction: taught {point} ({name})."
                        : $"U correction: teach {point} failed — {r.Message}");
    }

    private async void OnMove(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = PositionName(point);
        await ApplyPointExposureAsync(name);   // set the point's exposure (if any) before moving
        var r = await _host.Process.MoveToNamedPositionAsync(GantryDevice, name);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"U correction: moved to {point}."
                        : $"U correction: move to {point} failed — {r.Message} (Save it first).");
    }

    // --- Per-point camera exposure ---

    /// <summary>Opens the per-point exposure editor (P1/P2/F1/F2).</summary>
    private void OnEditExposures(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var points = Order.Select(p => (Label: p, Key: PositionName(p))).ToList();
        new ExposureEditorWindow("U correction — exposure per point", points, _host.Settings)
        {
            Owner = Window.GetWindow(this),
        }.ShowDialog();
    }

    /// <summary>The camera the exposure is applied to — <c>main_camera</c> if present, else the first.</summary>
    private string? MainCamera()
    {
        var ids = _host?.Engine.Camera.CameraIds;
        if (ids == null || ids.Count == 0) return null;
        return ids.Contains("main_camera") ? "main_camera" : ids[0];
    }

    /// <summary>Configured exposure (µs) for a position, or 0 when none is set.</summary>
    private double PointExposureUs(string positionName)
        => _host != null && _host.Settings.Current.PointExposureUs.TryGetValue(positionName, out var us) ? us : 0;

    /// <summary>Applies the point's saved exposure (auto-exposure off) before a move; no-op when unset.</summary>
    private async Task ApplyPointExposureAsync(string positionName)
    {
        double us = PointExposureUs(positionName);
        if (_host == null || us <= 0) return;
        var cam = MainCamera();
        if (cam == null) return;

        var current = _host.Engine.Camera.GetState(cam).Exposure;
        var r = await _host.Engine.Camera.ApplyExposureAsync(cam, current with { ExposureUs = us, AutoExposure = false });
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"U correction: exposure {us:0} µs on {cam}."
                        : $"U correction: exposure {us:0} µs failed — {r.Message}");
    }

    private void OnLockChanged(object sender, RoutedEventArgs e) => RefreshReferenceButtons();

    /// <summary>Collapses / expands the reference-point group and persists the new state.</summary>
    private void OnToggleRefPoints(object sender, RoutedEventArgs e)
    {
        bool collapsed = RefPointsBody.Visibility == Visibility.Visible;
        SetRefPointsCollapsed(collapsed);
        if (_host != null)
        {
            _host.Settings.Update(_host.Settings.Current with { UCorrectionRefPointsCollapsed = collapsed });
            _host.Settings.Save();
        }
    }

    /// <summary>Applies the collapsed/expanded state to the reference-point group (no persist).</summary>
    private void SetRefPointsCollapsed(bool collapsed)
    {
        RefPointsBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RefPointsChevron.Text = collapsed ? "▸" : "▾";
    }

    // --- Correction run ---

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || _running) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            StatusText.Text = "Busy — wait for the current run";
            Log(LogLevel.Warning, "U correction blocked: a process step or alignment is running.");
            return;
        }

        // All four reference points must be taught before the run can navigate them.
        var missing = Order.Where(p => _host.Process.GetPosition(GantryDevice, PositionName(p)) == null).ToList();
        if (missing.Count > 0)
        {
            StatusText.Text = $"Save these first: {string.Join(", ", missing)}";
            Log(LogLevel.Warning, $"U correction: cannot start — untaught points: {string.Join(", ", missing)}.");
            return;
        }

        _running = true;
        RaiseRunState();
        _captureIndex = 0;
        ResetDots();
        RefreshReferenceButtons();
        StartButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        _activity = _host.Activity.Begin(ActivityKind.Alignment, "U correction");   // block process buttons during the run

        // Per-point exposures (0 = leave unchanged). Only pin the camera when at least
        // one point has an exposure set, so the default run is unchanged.
        double e1 = PointExposureUs(PositionName("P1")), e2 = PointExposureUs(PositionName("P2"));
        double e3 = PointExposureUs(PositionName("F1")), e4 = PointExposureUs(PositionName("F2"));
        string? camera = (e1 > 0 || e2 > 0 || e3 > 0 || e4 > 0) ? MainCamera() : null;

        var options = new UCorrectionOptions(
            GantryDevice, HexDevice,
            PositionName("P1"), PositionName("P2"), PositionName("F1"), PositionName("F2"),
            Camera: camera,
            P1ExposureUs: e1 > 0 ? e1 : null, P2ExposureUs: e2 > 0 ? e2 : null,
            F1ExposureUs: e3 > 0 ? e3 : null, F2ExposureUs: e4 > 0 ? e4 : null);

        try
        {
            var r = await _host.Process.RunUCorrectionAsync(options, this, _cts.Token);
            StatusText.Text = r.IsSuccess
                ? (r.Value.Applied
                    ? $"Applied U = {r.Value.CorrectionDegrees:F4}° (angle {r.Value.AngleDegrees:F4}°)"
                    : $"Declined — angle {r.Value.AngleDegrees:F4}°")
                : r.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            Log(LogLevel.Error, $"U correction run error: {ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Capture the current point — completes the routine's per-point <see cref="ConfirmAsync"/>.</summary>
    private void OnCapture(object sender, RoutedEventArgs e)
    {
        var pending = _pending;
        if (pending == null || _awaitingApply) return;

        _pending = null;
        CaptureButton.IsEnabled = false;
        SetDot(_captureIndex, captured: true);
        _captureIndex++;
        StatusText.Text = "Capturing…";
        pending.TrySetResult(true);   // routine reads the live pose, then advances
    }

    /// <summary>Apply the correction — completes the routine's final apply <see cref="ConfirmAsync"/>.</summary>
    private void OnApplyU(object sender, RoutedEventArgs e)
    {
        var pending = _pending;
        if (pending == null || !_awaitingApply) return;

        _pending = null;
        _awaitingApply = false;
        ApplyButton.IsEnabled = false;
        StatusText.Text = "Applying U…";
        pending.TrySetResult(true);
    }

    /// <summary>Clear/abort — cancels an in-flight run, or just resets the indicators when idle.</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            _pending?.TrySetResult(false);   // abort a pending confirm immediately
        }
        else
        {
            ResetDots();
            StatusText.Text = "Idle";
        }
    }

    // --- IOperatorPrompt (inline, non-modal) — the routine blocks here per point + on apply ---

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetResult(false));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        // The routine awaits us from a background thread — set up the UI on the dispatcher.
        Dispatcher.Invoke(() =>
        {
            _pending = tcs;
            _awaitingApply = title.Contains("Apply", System.StringComparison.OrdinalIgnoreCase);

            if (_awaitingApply)
            {
                StatusText.Text = message.Replace("\n", " ");
                ApplyButton.IsEnabled = true;
                CaptureButton.IsEnabled = false;
            }
            else
            {
                StatusText.Text = message;
                SetDot(_captureIndex, captured: false);   // highlight the point now being refined
                CaptureButton.IsEnabled = true;
            }
            RaiseRunState();   // refresh the launcher badge / app-header status line
        });

        return tcs.Task;
    }

    // --- indicators + button state ---

    private void ResetDots()
    {
        foreach (var dot in _dots)
            dot.Fill = (Brush)FindResource("Edge");
    }

    /// <summary>Marks dot <paramref name="index"/> captured (green) or awaiting (accent).</summary>
    private void SetDot(int index, bool captured)
    {
        if (index < 0 || index >= _dots.Length) return;
        _dots[index].Fill = (Brush)FindResource(captured ? "Ok" : "Accent");
    }

    /// <summary>Save/Move buttons are disabled while a run is in progress or the Lock box is checked.</summary>
    private void RefreshReferenceButtons()
    {
        bool othersBusy = _host != null && (_host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !_running));
        bool enabled = !_running && !othersBusy && LockBox.IsChecked != true;
        foreach (var b in new[] { MoveP1, MoveP2, MoveF1, MoveF2, SaveP1, SaveP2, SaveF1, SaveF2 })
            b.IsEnabled = enabled;
    }

    private void Finish()
    {
        _running = false;
        _awaitingApply = false;
        _pending = null;
        _activity?.Dispose();
        _activity = null;
        _cts?.Dispose();
        _cts = null;
        CaptureButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        RefreshButtons();
        RaiseRunState();
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, System.DateTimeOffset.Now, "UCorr", message));
}
