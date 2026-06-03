using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.FauPreAlign;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page FAU pre-align panel — replicates the legacy acs_solution_saa FAU pre-align
/// UI (Move-to / Save-to Out1·Out2·Fiber1·Fiber2, target-offset editor, Start / Capture
/// / Clear / Move FAU) and drives it through the Layer 2
/// <see cref="IProcessService.RunFauPreAlignAsync(FauPreAlignOptions,IOperatorPrompt,System.Threading.CancellationToken)"/>
/// routine.
///
/// Like the U-correction panel, this control <b>is</b> the routine's non-modal
/// <see cref="IOperatorPrompt"/>: the inline <c>Capture Point</c> completes each
/// per-point refine confirm (so the operator keeps jogging via the live camera), and
/// <c>Move FAU</c> completes the final apply confirm.
/// </summary>
public partial class FauPreAlignPanel : UserControl, IOperatorPrompt
{
    private const string GantryDevice = "gantry-main";   // ACS gantry — the four reference points live here
    private const string HexDevice = "hex-right";        // PI hexapod — the Y/Z correction is applied here

    // Capture order Out1 → Out2 → Fiber1 → Fiber2, matching FauPreAlignOptions.
    private static readonly string[] Order = { "Out1", "Out2", "Fiber1", "Fiber2" };

    private EngineHost? _host;
    private Ellipse[] _dots;

    // Target "master" offset — persisted in AppSettings (loaded on Attach).
    private double _targetUm = 100.0;
    private double _targetAngleDeg;

    private CancellationTokenSource? _cts;
    private IDisposable? _activity;                 // process-busy token held for the run
    private TaskCompletionSource<bool>? _pending;
    private bool _awaitingApply;
    private int _captureIndex;
    private bool _running;

    public FauPreAlignPanel()
    {
        InitializeComponent();
        _dots = new[] { DotOut1, DotOut2, DotFiber1, DotFiber2 };
        UpdateTargetText();
    }

    /// <summary>Supplies the engine host (Layer 2 teach / recall / pre-align run + logging).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _targetUm = host.Settings.Current.FauPreAlignTargetUm;
        _targetAngleDeg = host.Settings.Current.FauPreAlignTargetAngleDeg;
        UpdateTargetText();
        host.Activity.Changed += OnActivityChanged;   // block Start/Move while a process step or other run holds the hexapod
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

    private static string PositionName(string point) => $"FauPre_{point}";

    // --- Target offset editor ---

    private void OnEditDistance(object sender, RoutedEventArgs e)
    {
        if (TryPromptDouble("Target distance (µm)", _targetUm, out var v))
        {
            _targetUm = v;
            PersistTarget();
            UpdateTargetText();
        }
    }

    private void OnEditAngle(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TryPromptDouble("Target angle (deg)", _targetAngleDeg, out var v))
        {
            _targetAngleDeg = v;
            PersistTarget();
            UpdateTargetText();
        }
    }

    /// <summary>Writes the current target offset back to AppSettings (app_settings.json).</summary>
    private void PersistTarget()
    {
        if (_host == null) return;
        _host.Settings.Update(_host.Settings.Current with
        {
            FauPreAlignTargetUm = _targetUm,
            FauPreAlignTargetAngleDeg = _targetAngleDeg,
        });
        _host.Settings.Save();
    }

    private bool TryPromptDouble(string label, double current, out double value)
    {
        value = current;
        var win = new SerialEntryWindow("FAU pre-align", label, current.ToString(CultureInfo.InvariantCulture))
        {
            Owner = Window.GetWindow(this),
        };
        if (win.ShowDialog() != true) return false;
        return double.TryParse(win.Serial, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void UpdateTargetText()
        => TargetText.Text = $"{_targetUm:0.#} µm @ {_targetAngleDeg:0.##}°";

    // --- Reference points: teach (Save) + recall (Move), via Layer 2 ---

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = PositionName(point);
        var velocity = _host.Process.GetPosition(GantryDevice, name)?.Velocity ?? 0;
        var r = await _host.Process.TeachPositionAsync(GantryDevice, name, velocity);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"FAU pre-align: taught {point} ({name})."
                        : $"FAU pre-align: teach {point} failed — {r.Message}");
    }

    private async void OnMove(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = PositionName(point);
        await ApplyPointExposureAsync(name);   // set the point's exposure (if any) before moving
        var r = await _host.Process.MoveToNamedPositionAsync(GantryDevice, name);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"FAU pre-align: moved to {point}."
                        : $"FAU pre-align: move to {point} failed — {r.Message} (Save it first).");
    }

    // --- Per-point camera exposure ---

    /// <summary>Opens the per-point exposure editor (Out1/Out2/Fiber1/Fiber2).</summary>
    private void OnEditExposures(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var points = Order.Select(p => (Label: p, Key: PositionName(p))).ToList();
        new ExposureEditorWindow("FAU pre-align — exposure per point", points, _host.Settings)
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
            r.IsSuccess ? $"FAU pre-align: exposure {us:0} µs on {cam}."
                        : $"FAU pre-align: exposure {us:0} µs failed — {r.Message}");
    }

    private void OnLockChanged(object sender, RoutedEventArgs e) => RefreshReferenceButtons();

    // --- Correction run ---

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || _running) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            StatusText.Text = "Busy — wait for the current run";
            Log(LogLevel.Warning, "FAU pre-align blocked: a process step or alignment is running.");
            return;
        }

        var missing = Order.Where(p => _host.Process.GetPosition(GantryDevice, PositionName(p)) == null).ToList();
        if (missing.Count > 0)
        {
            StatusText.Text = $"Save these first: {string.Join(", ", missing)}";
            Log(LogLevel.Warning, $"FAU pre-align: cannot start — untaught points: {string.Join(", ", missing)}.");
            return;
        }

        _running = true;
        _captureIndex = 0;
        ResetDots();
        RefreshReferenceButtons();
        StartButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        _activity = _host.Activity.Begin(ActivityKind.Alignment, "FAU pre-align");   // block process buttons during the run

        // Per-point exposures (0 = leave unchanged). Only pin the camera when at least
        // one point has an exposure set, so the default run is unchanged.
        double e1 = PointExposureUs(PositionName("Out1")), e2 = PointExposureUs(PositionName("Out2"));
        double e3 = PointExposureUs(PositionName("Fiber1")), e4 = PointExposureUs(PositionName("Fiber2"));
        string? camera = (e1 > 0 || e2 > 0 || e3 > 0 || e4 > 0) ? MainCamera() : null;

        var options = new FauPreAlignOptions(
            GantryDevice, HexDevice,
            PositionName("Out1"), PositionName("Out2"), PositionName("Fiber1"), PositionName("Fiber2"),
            TargetDistanceUm: _targetUm, TargetAngleDeg: _targetAngleDeg,
            Camera: camera,
            Out1ExposureUs: e1 > 0 ? e1 : null, Out2ExposureUs: e2 > 0 ? e2 : null,
            Fiber1ExposureUs: e3 > 0 ? e3 : null, Fiber2ExposureUs: e4 > 0 ? e4 : null);

        try
        {
            var r = await _host.Process.RunFauPreAlignAsync(options, this, _cts.Token);
            StatusText.Text = r.IsSuccess
                ? (r.Value.Applied
                    ? $"Applied hex Y={r.Value.HexYmm:F4}, Z={r.Value.HexZmm:F4} mm"
                    : $"Declined — corr ({r.Value.CorrectionXmm:F4}, {r.Value.CorrectionYmm:F4}) mm")
                : r.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            Log(LogLevel.Error, $"FAU pre-align run error: {ex.Message}");
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
        pending.TrySetResult(true);
    }

    /// <summary>Apply the correction — completes the routine's final apply <see cref="ConfirmAsync"/>.</summary>
    private void OnMoveFau(object sender, RoutedEventArgs e)
    {
        var pending = _pending;
        if (pending == null || !_awaitingApply) return;

        _pending = null;
        _awaitingApply = false;
        MoveFauButton.IsEnabled = false;
        StatusText.Text = "Moving FAU…";
        pending.TrySetResult(true);
    }

    /// <summary>Clear/abort — cancels an in-flight run, or just resets the indicators when idle.</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            _pending?.TrySetResult(false);
        }
        else
        {
            ResetDots();
            StatusText.Text = "Idle";
        }
    }

    // --- IOperatorPrompt (inline, non-modal) ---

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetResult(false));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        Dispatcher.Invoke(() =>
        {
            _pending = tcs;
            _awaitingApply = title.Contains("Apply", System.StringComparison.OrdinalIgnoreCase);

            if (_awaitingApply)
            {
                StatusText.Text = message.Replace("\n", " ");
                MoveFauButton.IsEnabled = true;
                CaptureButton.IsEnabled = false;
            }
            else
            {
                StatusText.Text = message;
                SetDot(_captureIndex, captured: false);
                CaptureButton.IsEnabled = true;
            }
        });

        return tcs.Task;
    }

    // --- indicators + button state ---

    private void ResetDots()
    {
        foreach (var dot in _dots)
            dot.Fill = (Brush)FindResource("Edge");
    }

    private void SetDot(int index, bool captured)
    {
        if (index < 0 || index >= _dots.Length) return;
        _dots[index].Fill = (Brush)FindResource(captured ? "Ok" : "Accent");
    }

    private void RefreshReferenceButtons()
    {
        bool othersBusy = _host != null && (_host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !_running));
        bool enabled = !_running && !othersBusy && LockBox.IsChecked != true;
        foreach (var b in new[] { MoveOut1, MoveOut2, MoveFiber1, MoveFiber2, SaveOut1, SaveOut2, SaveFiber1, SaveFiber2 })
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
        MoveFauButton.IsEnabled = false;
        RefreshButtons();
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, System.DateTimeOffset.Now, "FauPre", message));
}
