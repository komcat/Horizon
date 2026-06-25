using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.FauPreAlign;
using Horizon.Workflow.UCorrection;

namespace FAAM.UI.Views;

/// <summary>
/// Combined Run-page alignment panel that merges the former U-correction and FAU pre-align
/// widgets into one compact control with a single <c>Start correction</c> that runs the
/// U-correction routine and then, automatically, the FAU pre-align routine. The two Layer 2
/// routines themselves are untouched — this control simply chains
/// <see cref="IProcessService.RunUCorrectionAsync(UCorrectionOptions,IOperatorPrompt,System.Threading.CancellationToken)"/>
/// then <see cref="IProcessService.RunFauPreAlignAsync(FauPreAlignOptions,IOperatorPrompt,System.Threading.CancellationToken)"/>
/// and serves as the non-modal <see cref="IOperatorPrompt"/> for both (they never overlap, so
/// the shared Capture / dots / Apply chrome is reused; the Apply button label morphs
/// <c>Apply U</c> → <c>Move FAU</c> per phase).
///
/// <see cref="RunAsync"/> exposes single-phase entry points so the legacy <c>ucorrection</c> /
/// <c>faupre</c> <c>app.*</c> ids keep working alongside the new chained <c>align</c> id.
/// </summary>
public partial class AlignmentPanel : UserControl, IOperatorPrompt
{
    // FAAM rig device mapping (mirrors ProcessStepsPanel).
    private const string GantryDevice = "gantry-main";   // ACS gantry (X/Y/Z) — the reference points live here
    private const string HexDevice = "hex-right";        // PI hexapod — U / Y-Z corrections applied here

    private static readonly string[] UOrder = { "P1", "P2", "F1", "F2" };
    private static readonly string[] FauOrder = { "Out1", "Out2", "Fiber1", "Fiber2" };

    private enum Phase { None, U, Fau }

    private EngineHost? _host;
    private readonly Ellipse[] _dots;

    // FAU target "master" offset — persisted in AppSettings (loaded on Attach).
    private double _targetUm = 100.0;
    private double _targetAngleDeg;

    private CancellationTokenSource? _cts;
    private IDisposable? _activity;                 // process-busy token held for the run
    private TaskCompletionSource<bool>? _pending;   // the in-flight ConfirmAsync, if any
    private bool _awaitingApply;
    private int _captureIndex;
    private bool _running;
    private Phase _phase = Phase.None;

    // Optional camera-feed prompt surface that mirrors the inline Capture / Apply buttons
    // (set by RunView). Either surface completes the same pending confirm.
    private ICapturePromptSurface? _promptSurface;

    /// <summary>Raised whenever the run-state or status changes — the utility-app launcher badge listens.</summary>
    public event Action? RunStateChanged;

    /// <summary>True while a correction run is in progress (drives the launcher's Running badge).</summary>
    public bool IsRunning => _running;

    /// <summary>Current status line, surfaced to the launcher badge / app header.</summary>
    public string RunStatusDetail => StatusText.Text;

    private void RaiseRunState() => RunStateChanged?.Invoke();

    /// <summary>Begins the chained U → FAU run (the <c>align</c> app id / Start button).</summary>
    public void RequestStart() => _ = RunSequenceAsync(runU: true, runFau: true);

    /// <summary>Begins the U-correction phase only (legacy <c>ucorrection</c> app id).</summary>
    public void RequestStartU() => _ = RunSequenceAsync(runU: true, runFau: false);

    /// <summary>Begins the FAU pre-align phase only (legacy <c>faupre</c> app id).</summary>
    public void RequestStartFau() => _ = RunSequenceAsync(runU: false, runFau: true);

    /// <summary>Cancels an in-flight run (utility-app <c>app.stop</c>); no-op when idle.</summary>
    public void RequestStop()
    {
        if (!_running) return;
        _cts?.Cancel();
        _pending?.TrySetResult(false);
    }

    public AlignmentPanel()
    {
        InitializeComponent();
        _dots = new[] { DotA, DotB, DotC, DotD };
        UpdateTargetText();
    }

    /// <summary>
    /// Mirrors the per-point Capture / final Apply prompts onto a camera-feed overlay (the
    /// Run-page main feed). The overlay's affirmative button completes the same confirm as
    /// the inline Capture / Apply button; its negative button cancels the run. Pass
    /// <c>null</c> to detach.
    /// </summary>
    public void SetCapturePromptSurface(ICapturePromptSurface? surface)
    {
        if (_promptSurface != null)
        {
            _promptSurface.Affirmed -= OnOverlayAffirmed;
            _promptSurface.Negated -= OnOverlayNegated;
        }
        _promptSurface = surface;
        if (_promptSurface != null)
        {
            _promptSurface.Affirmed += OnOverlayAffirmed;
            _promptSurface.Negated += OnOverlayNegated;
        }
    }

    private void OnOverlayAffirmed()
    {
        if (_awaitingApply) CompleteApply();
        else CompleteCapture();
    }

    private void OnOverlayNegated()
    {
        if (!_running) return;
        _cts?.Cancel();
        _pending?.TrySetResult(false);   // abort the in-flight confirm (same as inline Clear)
    }

    /// <summary>Supplies the engine host (Layer 2 teach / recall / correction runs + logging).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _targetUm = host.Settings.Current.FauPreAlignTargetUm;
        _targetAngleDeg = host.Settings.Current.FauPreAlignTargetAngleDeg;
        UpdateTargetText();
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

    private static string UName(string point) => $"UCorr_{point}";
    private static string FauName(string point) => $"FauPre_{point}";

    // --- Reference points: teach (Save) + recall (Move), via Layer 2 ---

    private void OnMoveU(object sender, RoutedEventArgs e) => _ = MoveToAsync(sender, UName, "U correction");
    private void OnSaveU(object sender, RoutedEventArgs e) => _ = SaveAsync(sender, UName, "U correction");
    private void OnMoveFauPoint(object sender, RoutedEventArgs e) => _ = MoveToAsync(sender, FauName, "FAU pre-align");
    private void OnSaveFauPoint(object sender, RoutedEventArgs e) => _ = SaveAsync(sender, FauName, "FAU pre-align");

    private async Task SaveAsync(object sender, Func<string, string> nameOf, string tag)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = nameOf(point);
        var velocity = _host.Process.GetPosition(GantryDevice, name)?.Velocity ?? 0;
        var r = await _host.Process.TeachPositionAsync(GantryDevice, name, velocity);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"{tag}: taught {point} ({name})."
                        : $"{tag}: teach {point} failed — {r.Message}");
    }

    private async Task MoveToAsync(object sender, Func<string, string> nameOf, string tag)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = nameOf(point);
        await ApplyPointExposureAsync(name, tag);   // set the point's exposure (if any) before moving
        var r = await _host.Process.MoveToNamedPositionAsync(GantryDevice, name);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"{tag}: moved to {point}."
                        : $"{tag}: move to {point} failed — {r.Message} (Save it first).");
    }

    // --- FAU target offset editor ---

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

    /// <summary>Writes the current FAU target offset back to AppSettings (app_settings.json).</summary>
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

    // --- Per-point camera exposure ---

    private void OnEditExposuresU(object sender, RoutedEventArgs e)
        => OpenExposureEditor("U correction — exposure per point", UOrder, UName);

    private void OnEditExposuresFau(object sender, RoutedEventArgs e)
        => OpenExposureEditor("FAU pre-align — exposure per point", FauOrder, FauName);

    private void OpenExposureEditor(string title, string[] order, Func<string, string> nameOf)
    {
        if (_host == null) return;
        var points = order.Select(p => (Label: p, Key: nameOf(p))).ToList();
        new ExposureEditorWindow(title, points, _host.Settings)
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
    private async Task ApplyPointExposureAsync(string positionName, string tag)
    {
        double us = PointExposureUs(positionName);
        if (_host == null || us <= 0) return;
        var cam = MainCamera();
        if (cam == null) return;

        var current = _host.Engine.Camera.GetState(cam).Exposure;
        var r = await _host.Engine.Camera.ApplyExposureAsync(cam, current with { ExposureUs = us, AutoExposure = false });
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"{tag}: exposure {us:0} µs on {cam}."
                        : $"{tag}: exposure {us:0} µs failed — {r.Message}");
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

    private void SetRefPointsCollapsed(bool collapsed)
    {
        RefPointsBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RefPointsChevron.Text = collapsed ? "▸" : "▾";
    }

    // --- Chained correction run: U correction → FAU pre-align ---

    private async void OnStart(object sender, RoutedEventArgs e) => await RunSequenceAsync(runU: true, runFau: true);

    /// <summary>
    /// Runs the requested phases in order (U then FAU). The chain stops if a phase is
    /// cancelled or errors; a "declined" U (angle below threshold) still proceeds to FAU.
    /// </summary>
    private async Task RunSequenceAsync(bool runU, bool runFau)
    {
        if (_host == null || _running) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            SetStatus(Phase.None, "Busy — wait for the current run");
            Log(LogLevel.Warning, "Alignment blocked: a process step or alignment is running.");
            return;
        }

        // Every reference point a phase will navigate must be taught first.
        var missing = new List<string>();
        if (runU) missing.AddRange(UOrder.Where(p => _host.Process.GetPosition(GantryDevice, UName(p)) == null).Select(p => $"U:{p}"));
        if (runFau) missing.AddRange(FauOrder.Where(p => _host.Process.GetPosition(GantryDevice, FauName(p)) == null).Select(p => $"FAU:{p}"));
        if (missing.Count > 0)
        {
            SetStatus(Phase.None, $"Save these first: {string.Join(", ", missing)}");
            Log(LogLevel.Warning, $"Alignment: cannot start — untaught points: {string.Join(", ", missing)}.");
            return;
        }

        _running = true;
        RaiseRunState();
        StartButton.IsEnabled = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _activity = _host.Activity.Begin(ActivityKind.Alignment, "Alignment (U → FAU)");

        try
        {
            if (runU)
            {
                BeginPhase(Phase.U, "Apply U");
                var rU = await _host.Process.RunUCorrectionAsync(BuildUOptions(), this, cts.Token);
                SetStatus(Phase.U, rU.IsSuccess
                    ? (rU.Value.Applied
                        ? $"Applied U = {rU.Value.CorrectionDegrees:F4}° (angle {rU.Value.AngleDegrees:F4}°)"
                        : $"Declined — angle {rU.Value.AngleDegrees:F4}°")
                    : rU.Message);

                if (cts.IsCancellationRequested) return;
                if (!rU.IsSuccess) { runFau = false; Log(LogLevel.Warning, "Alignment: U correction failed — skipping FAU pre-align."); }
            }

            if (runFau && !cts.IsCancellationRequested)
            {
                BeginPhase(Phase.Fau, "Move FAU");
                var rF = await _host.Process.RunFauPreAlignAsync(BuildFauOptions(), this, cts.Token);
                SetStatus(Phase.Fau, rF.IsSuccess
                    ? (rF.Value.Applied
                        ? $"Applied hex Y={rF.Value.HexYmm:F4}, Z={rF.Value.HexZmm:F4} mm"
                        : $"Declined — corr ({rF.Value.CorrectionXmm:F4}, {rF.Value.CorrectionYmm:F4}) mm")
                    : rF.Message);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(_phase, "Cancelled.");
        }
        catch (System.Exception ex)
        {
            SetStatus(_phase, $"Error: {ex.Message}");
            Log(LogLevel.Error, $"Alignment run error: {ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    private UCorrectionOptions BuildUOptions()
    {
        double e1 = PointExposureUs(UName("P1")), e2 = PointExposureUs(UName("P2"));
        double e3 = PointExposureUs(UName("F1")), e4 = PointExposureUs(UName("F2"));
        string? camera = (e1 > 0 || e2 > 0 || e3 > 0 || e4 > 0) ? MainCamera() : null;

        return new UCorrectionOptions(
            GantryDevice, HexDevice,
            UName("P1"), UName("P2"), UName("F1"), UName("F2"),
            Camera: camera,
            P1ExposureUs: e1 > 0 ? e1 : null, P2ExposureUs: e2 > 0 ? e2 : null,
            F1ExposureUs: e3 > 0 ? e3 : null, F2ExposureUs: e4 > 0 ? e4 : null);
    }

    private FauPreAlignOptions BuildFauOptions()
    {
        double e1 = PointExposureUs(FauName("Out1")), e2 = PointExposureUs(FauName("Out2"));
        double e3 = PointExposureUs(FauName("Fiber1")), e4 = PointExposureUs(FauName("Fiber2"));
        string? camera = (e1 > 0 || e2 > 0 || e3 > 0 || e4 > 0) ? MainCamera() : null;

        return new FauPreAlignOptions(
            GantryDevice, HexDevice,
            FauName("Out1"), FauName("Out2"), FauName("Fiber1"), FauName("Fiber2"),
            TargetDistanceUm: _targetUm, TargetAngleDeg: _targetAngleDeg,
            Camera: camera,
            Out1ExposureUs: e1 > 0 ? e1 : null, Out2ExposureUs: e2 > 0 ? e2 : null,
            Fiber1ExposureUs: e3 > 0 ? e3 : null, Fiber2ExposureUs: e4 > 0 ? e4 : null);
    }

    /// <summary>Resets per-phase indicators and the morphing Apply-button label at the start of a phase.</summary>
    private void BeginPhase(Phase phase, string applyLabel)
    {
        _phase = phase;
        _captureIndex = 0;
        ResetDots();
        ApplyButton.Content = applyLabel;
        SetStatus(phase, "Starting…");
        RefreshReferenceButtons();
    }

    /// <summary>Capture the current point — completes the routine's per-point <see cref="ConfirmAsync"/>.</summary>
    private void OnCapture(object sender, RoutedEventArgs e) => CompleteCapture();

    /// <summary>Driven by either the inline Capture button or the camera-overlay affirmative.</summary>
    private void CompleteCapture()
    {
        var pending = _pending;
        if (pending == null || _awaitingApply) return;

        _pending = null;
        CaptureButton.IsEnabled = false;
        _promptSurface?.Hide();
        SetDot(_captureIndex, captured: true);
        _captureIndex++;
        SetStatus(_phase, "Capturing…");
        pending.TrySetResult(true);   // routine reads the live pose, then advances
    }

    /// <summary>Apply — completes the routine's final apply <see cref="ConfirmAsync"/> (Apply U / Move FAU).</summary>
    private void OnApply(object sender, RoutedEventArgs e) => CompleteApply();

    /// <summary>Driven by either the inline Apply button or the camera-overlay affirmative.</summary>
    private void CompleteApply()
    {
        var pending = _pending;
        if (pending == null || !_awaitingApply) return;

        _pending = null;
        _awaitingApply = false;
        ApplyButton.IsEnabled = false;
        _promptSurface?.Hide();
        SetStatus(_phase, _phase == Phase.U ? "Applying U…" : "Moving FAU…");
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
            SetStatus(Phase.None, "Idle");
        }
    }

    // --- IOperatorPrompt (inline, non-modal) — the active routine blocks here per point + on apply ---

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
                var msg = message.Replace("\n", " ");
                SetStatus(_phase, msg);
                ApplyButton.IsEnabled = true;
                CaptureButton.IsEnabled = false;
                _promptSurface?.Show(msg, _phase == Phase.U ? "Apply U" : "Move FAU", "Cancel");
            }
            else
            {
                SetStatus(_phase, message);
                SetDot(_captureIndex, captured: false);   // highlight the point now being refined
                CaptureButton.IsEnabled = true;
                _promptSurface?.Show(message, "Capture", "Cancel");
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
        foreach (var b in new[]
        {
            MoveP1, MoveP2, MoveF1, MoveF2, SaveP1, SaveP2, SaveF1, SaveF2,
            MoveOut1, MoveOut2, MoveFiber1, MoveFiber2, SaveOut1, SaveOut2, SaveFiber1, SaveFiber2,
        })
            b.IsEnabled = enabled;
    }

    private void SetStatus(Phase phase, string message)
    {
        PhaseText.Text = phase switch
        {
            Phase.U => "U correction",
            Phase.Fau => "FAU pre-align",
            _ => "Idle",
        };
        StatusText.Text = message;
    }

    private void Finish()
    {
        _running = false;
        _awaitingApply = false;
        _pending = null;
        _phase = Phase.None;
        _activity?.Dispose();
        _activity = null;
        _cts?.Dispose();
        _cts = null;
        CaptureButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        ApplyButton.Content = "Apply U";
        _promptSurface?.Hide();
        RefreshButtons();
        RaiseRunState();
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, System.DateTimeOffset.Now, "Align", message));
}
