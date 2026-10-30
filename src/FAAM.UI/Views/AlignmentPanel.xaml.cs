using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FAAM.UI.Siphog;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.FauPreAlign;
using Horizon.Workflow.GridSearch;
using Horizon.Workflow.HillClimb;
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

    // Automatic vision U-correction: one look at the PIC facet, one at the FAU facet. Each is a
    // taught gantry position paired with the saved vision profile (edge detection → "angle") that
    // reads that facet's angle. Names must match the taught positions / saved .hvp profiles.
    private const string PicFacetPosition = "visionFacet";
    private const string PicFacetProfile = "edgePIC";
    private const string FauFacetPosition = "visionFAUFacet";
    private const string FauFacetProfile = "edgeFAU";

    private enum Phase { None, U, Fau, Grid }

    /// <summary>Outcome of the automatic vision U-correction attempt.</summary>
    private enum VisionUOutcome { Applied, Fallback, Cancelled }

    private EngineHost? _host;
    private readonly Ellipse[] _dots;

    // Post-FAU XY signal finder (Stage 1 X line → Stage 2 grid). Wired by UtilityAppHost via
    // EnableGridSearch once the SiPhOG client exists; left null (skipped) otherwise. The old
    // hand-off to the raster climb (_hill) is only taken when a climb panel is supplied — the
    // Run page no longer supplies one (Raster Climb is DEPRECATED — do not use).
    private FauGridSearchEngine? _gridEngine;
    private IHillClimbFeedback? _gridFeedback;
    private RasterClimbPanel? _hill;
    private bool _gridEnabled;

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

    private void RaiseRunState()
    {
        // Yellow panel while a correction run is in progress; default panel colour otherwise.
        RootBorder.Background = (Brush)FindResource(_running ? "Running" : "Panel");
        RunStateChanged?.Invoke();
    }

    /// <summary>Begins the chained U → FAU run (the <c>align</c> app id / Start button).</summary>
    public void RequestStart() => _ = RunSequenceAsync(runU: true, runFau: true);

    /// <summary>Begins the U-correction phase only (legacy <c>ucorrection</c> app id).</summary>
    public void RequestStartU() => _ = RunSequenceAsync(runU: true, runFau: false);

    /// <summary>Begins the FAU pre-align phase only (legacy <c>faupre</c> app id).</summary>
    public void RequestStartFau() => _ = RunSequenceAsync(runU: false, runFau: true);

    /// <summary>
    /// Runs the requested phases from a program / deck (<c>align.correction</c> block via
    /// <see cref="IUtilityAppHost.RunAsync"/>) and completes only when the chain ends. The
    /// calling program already holds the Process gate, so that is not treated as "busy"; a
    /// program Stop (<paramref name="ct"/>) aborts the run, and a panel Stop fails the step.
    /// Must be called on the UI thread. The operator still answers each point with Capture
    /// (inline or on the camera feed) and may MoveClick the live view in between.
    /// <paramref name="overrides"/> = the block's points / gap / angle / guards (null members
    /// fall back to the panel's fixed point names and its target setting).
    /// </summary>
    public Task<Result> RunFromProgramAsync(bool runU, bool runFau, AlignmentRunOverrides? overrides, CancellationToken ct)
        => RunSequenceCoreAsync(runU, runFau, fromProgram: true, overrides, ct);

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

    /// <summary>
    /// Enables the post-FAU grid search: after the FAU pre-align phase succeeds, the panel runs the
    /// Layer 2 <see cref="FauGridSearchEngine"/> over hex-right XY (live SiPhOG SAG-V feedback) and,
    /// if a stage finds signal, hands off to <paramref name="hill"/> when one is given (the
    /// DEPRECATED raster climb — the Run page passes none, so the chain stops at the found signal).
    /// Call after <see cref="Attach"/>.
    /// </summary>
    public void EnableGridSearch(SiphogClient siphog, RasterClimbPanel? hill = null)
    {
        if (_host == null) return;
        _hill = hill;
        _gridFeedback = new SiphogHillClimbFeedback(siphog);
        _gridEngine = new FauGridSearchEngine(_host.Engine, Log);
        _gridEngine.Measured += OnGridMeasured;
        _gridEnabled = true;
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

    /// <summary>
    /// Drives the prominent button. While running it morphs into a red Stop (always enabled so the
    /// operator can break the whole chain mid-step); when idle it is the accent Start, enabled only
    /// when nothing else (a process step or another alignment) holds the hexapod.
    /// </summary>
    private void RefreshStartEnabled()
    {
        if (_host == null) return;
        if (_running)
        {
            StartButton.Content = "■ Stop";
            StartButton.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            StartButton.IsEnabled = true;
            return;
        }

        StartButton.Content = "Start correction";
        StartButton.Background = (Brush)FindResource("Accent");
        bool othersBusy = _host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !_running);
        StartButton.IsEnabled = !othersBusy;
    }

    private static string UName(string point) => $"UCorr_{point}";
    private static string FauName(string point) => $"FauPre_{point}";

    /// <summary>The four U points in capture order (P1, P2, F1, F2) — a block's names, else the fixed UCorr_* ones.</summary>
    private static string[] UPoints(AlignmentRunOverrides? o) => new[]
    {
        o?.UPicPoint1 ?? UName("P1"), o?.UPicPoint2 ?? UName("P2"),
        o?.UFauPoint1 ?? UName("F1"), o?.UFauPoint2 ?? UName("F2"),
    };

    /// <summary>The four FAU points in capture order (Out1, Out2, Fiber1, Fiber2) — a block's names, else the fixed FauPre_* ones.</summary>
    private static string[] FauPoints(AlignmentRunOverrides? o) => new[]
    {
        o?.PlaceOut1 ?? FauName("Out1"), o?.PlaceOut2 ?? FauName("Out2"),
        o?.PlaceFiber1 ?? FauName("Fiber1"), o?.PlaceFiber2 ?? FauName("Fiber2"),
    };

    // --- Reference points: teach (Save) + recall (Move), via Layer 2 ---

    private void OnMoveU(object sender, RoutedEventArgs e) => _ = MoveToAsync(sender, UName, "U correction");
    private void OnSaveU(object sender, RoutedEventArgs e) => _ = SaveAsync(sender, UName, "U correction");
    private void OnMoveFauPoint(object sender, RoutedEventArgs e) => _ = MoveToAsync(sender, FauName, "Accuracy place FAU");
    private void OnSaveFauPoint(object sender, RoutedEventArgs e) => _ = SaveAsync(sender, FauName, "Accuracy place FAU");

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
        var win = new SerialEntryWindow("Accuracy place FAU", label, current.ToString(CultureInfo.InvariantCulture))
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
        => OpenExposureEditor("Accuracy place FAU — exposure per point", FauOrder, FauName);

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

    /// <summary>
    /// The prominent button does double duty: it starts the chained U → FAU run when idle,
    /// and becomes a Stop that cancels the whole chain (U / FAU / grid search) while running.
    /// </summary>
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            RequestStop();
            return;
        }
        await RunSequenceAsync(runU: true, runFau: true);
    }

    /// <summary>Panel / app-id entry point — the outcome is shown on the status line, not returned.</summary>
    private Task RunSequenceAsync(bool runU, bool runFau)
        => RunSequenceCoreAsync(runU, runFau, fromProgram: false, overrides: null, CancellationToken.None);

    private static Result Cancelled() => Result.Fail(ErrorCode.ProcessCancelled, "Alignment cancelled.");

    /// <summary>
    /// Runs the requested phases in order (U then FAU). The chain stops if a phase is
    /// cancelled or errors; a "declined" U (angle below threshold) still proceeds to FAU.
    /// Returns the chain's outcome (used by the program block; the panel only shows it).
    /// </summary>
    private async Task<Result> RunSequenceCoreAsync(
        bool runU, bool runFau, bool fromProgram, AlignmentRunOverrides? overrides, CancellationToken external)
    {
        if (_host == null) return Result.Fail(ErrorCode.OperationFailed, "Alignment panel not attached.");
        if (_running) return Result.Fail(ErrorCode.OperationFailed, "An alignment run is already in progress.");

        // A program calling us holds the Process gate itself — only another alignment blocks it.
        bool busy = fromProgram ? _host.Activity.AlignmentBusy : _host.Activity.ProcessBusy || _host.Activity.AlignmentBusy;
        if (busy)
        {
            SetStatus(Phase.None, "Busy — wait for the current run");
            Log(LogLevel.Warning, "Alignment blocked: a process step or alignment is running.");
            return Result.Fail(ErrorCode.OperationFailed, "Alignment blocked: another alignment / scan is running.");
        }

        // Each phase needs four distinct points — a repeated name collapses a line to zero length.
        var uPoints = UPoints(overrides);
        var fauPoints = FauPoints(overrides);
        string? dup = runU && uPoints.Distinct(StringComparer.OrdinalIgnoreCase).Count() < uPoints.Length ? "U correction"
                    : runFau && fauPoints.Distinct(StringComparer.OrdinalIgnoreCase).Count() < fauPoints.Length ? "Accuracy place FAU"
                    : null;
        if (dup != null)
        {
            SetStatus(Phase.None, $"{dup}: the four points must be different positions");
            Log(LogLevel.Warning, $"Alignment: cannot start — {dup} uses the same position twice.");
            return Result.Fail(ErrorCode.InvalidParameter, $"{dup}: the four reference points must be different positions.");
        }

        // Every reference point a phase will navigate must be taught first.
        var missing = new List<string>();
        if (runU) missing.AddRange(uPoints.Where(n => _host.Process.GetPosition(GantryDevice, n) == null).Select(n => $"U:{n}"));
        if (runFau) missing.AddRange(fauPoints.Where(n => _host.Process.GetPosition(GantryDevice, n) == null).Select(n => $"FAU:{n}"));
        if (missing.Count > 0)
        {
            SetStatus(Phase.None, $"Save these first: {string.Join(", ", missing)}");
            Log(LogLevel.Warning, $"Alignment: cannot start — untaught points: {string.Join(", ", missing)}.");
            return Result.Fail(ErrorCode.InvalidParameter, $"Untaught reference points: {string.Join(", ", missing)}.");
        }

        _running = true;
        RaiseRunState();
        RefreshStartEnabled();   // morph Start → Stop immediately
        _host.Activities.Publish(OperatorActivity.Section(ActivityCategory.Sequence, "started",
            $"Start correction (U={runU}, FAU={runFau}{(fromProgram ? ", program" : "")})"));
        // A program Stop cancels the linked token; the panel's own Stop cancels it directly.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _cts = cts;
        _activity = _host.Activity.Begin(ActivityKind.Alignment, "Alignment (U → FAU)");

        bool handoffClimb = false;
        var result = Result.Ok();
        try
        {
            // Optional automatic vision U-correction: measure the PIC/FAU facet angles and rotate hex U
            // by the delta. On success skip the manual capture; on any failure fall back to it.
            if (runU && _host.Settings.Current.FindFirstLight.AutoVisionUCorrection)
            {
                var outcome = await TryVisionUCorrectionAsync(overrides?.MaxUDeg, cts.Token);
                if (cts.IsCancellationRequested) return Cancelled();
                if (outcome == VisionUOutcome.Applied) runU = false;   // vision handled U — skip manual capture
                // Fallback → leave runU true so the manual capture flow below runs.
            }

            if (runU)
            {
                BeginPhase(Phase.U, "Apply U");
                _host.Activities.Publish(OperatorActivity.Section(FaamActivity.UCorrection, "started"));
                var rU = await _host.Process.RunUCorrectionAsync(BuildUOptions(overrides), this, cts.Token);
                SetStatus(Phase.U, rU.IsSuccess
                    ? (rU.Value.Applied
                        ? $"Applied U = {rU.Value.CorrectionDegrees:F4}° (angle {rU.Value.AngleDegrees:F4}°)"
                        : $"Declined — angle {rU.Value.AngleDegrees:F4}°")
                    : rU.Message);
                _host.Activities.Publish(OperatorActivity.Section(FaamActivity.UCorrection,
                    rU.IsSuccess ? (rU.Value.Applied ? "completed" : "declined") : "failed",
                    rU.IsSuccess
                        ? (rU.Value.Applied
                            ? $"U = {rU.Value.CorrectionDegrees:F4}° (angle {rU.Value.AngleDegrees:F4}°)"
                            : $"angle {rU.Value.AngleDegrees:F4}°")
                        : rU.Message,
                    rU.IsSuccess
                        ? new Dictionary<string, object?>
                        {
                            ["angleDeg"] = rU.Value.AngleDegrees,
                            ["correctionDeg"] = rU.Value.CorrectionDegrees,
                            ["applied"] = rU.Value.Applied,
                        }
                        : null));

                if (cts.IsCancellationRequested) return Cancelled();
                if (!rU.IsSuccess)
                {
                    runFau = false;
                    result = Result.Fail(rU.Error, $"U correction failed: {rU.Message}");
                    Log(LogLevel.Warning, "Alignment: U correction failed — skipping accuracy place FAU.");
                }
            }

            if (runFau && !cts.IsCancellationRequested)
            {
                BeginPhase(Phase.Fau, "Move FAU");
                _host.Activities.Publish(OperatorActivity.Section(FaamActivity.FauPreAlign, "started"));
                var rF = await _host.Process.RunFauPreAlignAsync(BuildFauOptions(overrides), this, cts.Token);
                SetStatus(Phase.Fau, rF.IsSuccess
                    ? (rF.Value.Applied
                        ? $"Applied hex Y={rF.Value.HexYmm:F4}, Z={rF.Value.HexZmm:F4} mm"
                        : $"Declined — corr ({rF.Value.CorrectionXmm:F4}, {rF.Value.CorrectionYmm:F4}) mm")
                    : rF.Message);
                _host.Activities.Publish(OperatorActivity.Section(FaamActivity.FauPreAlign,
                    rF.IsSuccess ? (rF.Value.Applied ? "completed" : "declined") : "failed",
                    rF.IsSuccess
                        ? (rF.Value.Applied
                            ? $"hex Y={rF.Value.HexYmm:F4}, Z={rF.Value.HexZmm:F4} mm"
                            : $"corr ({rF.Value.CorrectionXmm:F4}, {rF.Value.CorrectionYmm:F4}) mm")
                        : rF.Message,
                    rF.IsSuccess
                        ? new Dictionary<string, object?>
                        {
                            ["hexYmm"] = rF.Value.HexYmm,
                            ["hexZmm"] = rF.Value.HexZmm,
                            ["applied"] = rF.Value.Applied,
                        }
                        : null));

                if (cts.IsCancellationRequested) return Cancelled();
                if (!rF.IsSuccess) result = Result.Fail(rF.Error, $"Accuracy place FAU failed: {rF.Message}");

                // End of corrections: find the Sagnac signal in XY (Stage 1 X line → Stage 2 grid).
                if (rF.IsSuccess && !cts.IsCancellationRequested && _gridEnabled)
                {
                    _host.Activities.Publish(OperatorActivity.Section(FaamActivity.GridSearch, "started"));
                    handoffClimb = await RunGridSearchAsync(cts.Token);
                    _host.Activities.Publish(OperatorActivity.Section(FaamActivity.GridSearch,
                        handoffClimb ? "completed" : "no-signal",
                        handoffClimb ? "signal found" : "no signal / skipped",
                        new Dictionary<string, object?> { ["signalFound"] = handoffClimb }));
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(_phase, "Cancelled.");
            return Cancelled();
        }
        catch (System.Exception ex)
        {
            SetStatus(_phase, $"Error: {ex.Message}");
            Log(LogLevel.Error, $"Alignment run error: {ex.Message}");
            return Result.Fail(ErrorCode.OperationFailed, $"Alignment run error: {ex.Message}");
        }
        finally
        {
            Finish();
            _host.Activities.Publish(OperatorActivity.Section(ActivityCategory.Sequence, "ended"));
        }

        // Hand off to the climb once this run released the hexapod (alignment activity disposed
        // in Finish), mirroring the climb → Auto-W one-shot. Only fires when a grid stage
        // actually found signal. Still gated by the Find First Light HillClimbEnabled setting —
        // the same operator switch, now governing the raster climb that replaced it.
        if (handoffClimb && _hill == null)
            Log(LogLevel.Info, "Grid search found signal — stopping here (run a Scanning script preset to climb).");
        if (handoffClimb && _hill != null)
        {
            if (_host.Settings.Current.FindFirstLight.HillClimbEnabled)
            {
                Log(LogLevel.Info, "Grid search found signal — handing off to the raster climb.");
                _hill.RequestStart();
            }
            else
            {
                Log(LogLevel.Info, "Grid search found signal — climb disabled in settings, stopping here.");
            }
        }
        return result;
    }

    /// <summary>
    /// Runs the post-FAU grid search on hex-right. Returns true when a stage found signal (the
    /// hexapod is parked at the hand-off pose). Skips quietly (returns false) when the hexapod is
    /// disconnected or the SiPhOG isn't streaming.
    /// </summary>
    private async Task<bool> RunGridSearchAsync(CancellationToken ct)
    {
        if (_host == null || _gridEngine == null || _gridFeedback == null) return false;

        var ffl = _host.Settings.Current.FindFirstLight;
        if (!ffl.Enabled)
        {
            SetStatus(Phase.Grid, "Grid search disabled (Find First Light off)");
            Log(LogLevel.Info, "Grid search skipped: Find First Light disabled on the Engineer page.");
            return false;
        }

        if (!_host.Engine.Motion.IsConnected(HexDevice))
        {
            SetStatus(Phase.Grid, "Grid search skipped — hexapod not connected");
            Log(LogLevel.Warning, "Grid search skipped: hexapod not connected.");
            return false;
        }
        if (!_gridFeedback.HasSample)
        {
            SetStatus(Phase.Grid, "Grid search skipped — SiPhOG not streaming");
            Log(LogLevel.Warning, "Grid search skipped: no SiPhOG feedback (connect the SiPhOG widget).");
            return false;
        }

        // Build the search config from the operator-edited Find First Light settings.
        GridSearchConfig config;
        try
        {
            config = ffl.ToGridSearchConfig();
            config.Validate();
        }
        catch (ArgumentException ex)
        {
            SetStatus(Phase.Grid, $"Grid search skipped — invalid settings: {ex.Message}");
            Log(LogLevel.Warning, $"Grid search skipped: invalid Find First Light settings — {ex.Message}");
            return false;
        }

        _phase = Phase.Grid;
        SetStatus(Phase.Grid, "Grid search: baseline…");
        var r = await _gridEngine.RunAsync(HexDevice, _gridFeedback, config, ct);
        SetStatus(Phase.Grid, r.Message);
        return r.SignalFound;
    }

    /// <summary>Surfaces each grid node/step value to the status line (engine fires on a bg thread).</summary>
    private void OnGridMeasured(object? sender, GridSearchMeasuredEventArgs e) => Ui(() =>
        SetStatus(Phase.Grid, $"{e.Phase} {e.Label}: {e.V:F4} V (peak {e.VPeak:F4}){(e.SignalSeen ? " ✓" : "")}"));

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Runs the automatic vision U-correction: moves to <c>visionFacet</c> and runs the
    /// <c>edgePIC</c> profile for the PIC facet angle, then to <c>visionFAUFacet</c> and runs
    /// <c>edgeFAU</c> for the FAU facet angle, and applies the difference on hex U. Policy (from the Engineer
    /// "Find First Light" tab): ≤ ConfirmDeg applies silently; between ConfirmDeg and MaxDeg asks
    /// the operator to confirm; above MaxDeg — or if any facet isn't detected — falls back to the
    /// manual capture correction. A block's <paramref name="maxUDeg"/> lowers that MaxDeg limit too.
    /// Returns whether U was applied, we should fall back, or the run was cancelled.
    /// </summary>
    private async Task<VisionUOutcome> TryVisionUCorrectionAsync(double? maxUDeg, CancellationToken ct)
    {
        if (_host == null) return VisionUOutcome.Fallback;

        _phase = Phase.U;
        ResetDots();
        ApplyButton.Content = "Apply U";
        SetStatus(Phase.U, "Vision U: measuring facet angles…");

        var r = await _host.Process.MeasureVisionUCorrectionAsync(BuildVisionUOptions(), ct);
        if (ct.IsCancellationRequested) return VisionUOutcome.Cancelled;

        if (!r.IsSuccess)
        {
            SetStatus(Phase.U, $"Vision U failed: {r.Message} — manual capture");
            Log(LogLevel.Warning, $"Vision U-correction failed ({r.Message}); falling back to manual capture.");
            return VisionUOutcome.Fallback;
        }

        var v = r.Value;
        if (!v.AllFound)
        {
            SetStatus(Phase.U, $"Vision U: {v.FailedPoint} not detected — manual capture");
            Log(LogLevel.Warning, $"Vision U-correction: facet not detected at {v.FailedPoint}; falling back to manual capture.");
            return VisionUOutcome.Fallback;
        }

        var ffl = _host.Settings.Current.FindFirstLight;
        double mag = Math.Abs(v.DeltaDeg);
        Log(LogLevel.Info,
            $"Vision U: PIC {v.PicAngleDeg:F3}° [{PicFacetProfile} @ {PicFacetPosition}], " +
            $"FAU {v.FauAngleDeg:F3}° [{FauFacetProfile} @ {FauFacetPosition}] → Δ {v.DeltaDeg:F3}°.");

        double limit = maxUDeg is { } m ? Math.Min(ffl.AutoVisionUMaxDeg, m) : ffl.AutoVisionUMaxDeg;
        if (!double.IsFinite(mag) || mag > limit)
        {
            SetStatus(Phase.U, $"Vision U Δ {v.DeltaDeg:F3}° > {limit:0.###}° — manual capture");
            Log(LogLevel.Warning, $"Vision U-correction Δ {v.DeltaDeg:F3}° exceeds fallback limit {limit:0.###}°; using manual capture.");
            return VisionUOutcome.Fallback;
        }

        if (mag > ffl.AutoVisionUConfirmDeg)
        {
            var answer = MessageBox.Show(
                Window.GetWindow(this),
                $"Vision U correction = {v.DeltaDeg:F3}°\n" +
                $"PIC facet {v.PicAngleDeg:F3}°, FAU facet {v.FauAngleDeg:F3}°\n\n" +
                "Apply this hex U rotation?",
                "Confirm U correction", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                SetStatus(Phase.U, "Vision U declined — manual capture");
                Log(LogLevel.Info, "Vision U-correction declined by operator; falling back to manual capture.");
                return VisionUOutcome.Fallback;
            }
        }

        SetStatus(Phase.U, $"Vision U: applying {v.DeltaDeg:F3}°…");
        var mv = await _host.Process.ApplyHexUCorrectionAsync(HexDevice, v.DeltaDeg, ct);
        if (ct.IsCancellationRequested) return VisionUOutcome.Cancelled;
        if (!mv.IsSuccess)
        {
            SetStatus(Phase.U, $"Vision U apply failed: {mv.Message} — manual capture");
            Log(LogLevel.Warning, $"Vision U-correction apply failed ({mv.Message}); falling back to manual capture.");
            return VisionUOutcome.Fallback;
        }

        foreach (var dot in _dots) dot.Fill = (Brush)FindResource("Ok");
        SetStatus(Phase.U, $"Vision U applied {v.DeltaDeg:F3}° (PIC {v.PicAngleDeg:F3}°, FAU {v.FauAngleDeg:F3}°)");
        Log(LogLevel.Info, $"Vision U-correction applied {v.DeltaDeg:F3}° on {HexDevice}.");
        return VisionUOutcome.Applied;
    }

    private VisionUCorrectionOptions BuildVisionUOptions() => new(
        GantryDevice, HexDevice,
        PicFacetPosition, PicFacetProfile,
        FauFacetPosition, FauFacetProfile);

    /// <summary>U options from the panel defaults, with a block's points / Max U layered on top.</summary>
    private UCorrectionOptions BuildUOptions(AlignmentRunOverrides? o)
    {
        var n = UPoints(o);
        double e1 = PointExposureUs(n[0]), e2 = PointExposureUs(n[1]);
        double e3 = PointExposureUs(n[2]), e4 = PointExposureUs(n[3]);
        string? camera = (e1 > 0 || e2 > 0 || e3 > 0 || e4 > 0) ? MainCamera() : null;

        var opts = new UCorrectionOptions(
            GantryDevice, HexDevice,
            n[0], n[1], n[2], n[3],
            Camera: camera,
            P1ExposureUs: e1 > 0 ? e1 : null, P2ExposureUs: e2 > 0 ? e2 : null,
            F1ExposureUs: e3 > 0 ? e3 : null, F2ExposureUs: e4 > 0 ? e4 : null);
        return o?.MaxUDeg is { } maxU ? opts with { MaxUvwDeg = maxU } : opts;
    }

    /// <summary>FAU options from the panel defaults, with a block's points / gap / angle / waveguide / guard layered on top.</summary>
    private FauPreAlignOptions BuildFauOptions(AlignmentRunOverrides? o)
    {
        var n = FauPoints(o);
        double e1 = PointExposureUs(n[0]), e2 = PointExposureUs(n[1]);
        double e3 = PointExposureUs(n[2]), e4 = PointExposureUs(n[3]);
        string? camera = (e1 > 0 || e2 > 0 || e3 > 0 || e4 > 0) ? MainCamera() : null;

        var opts = new FauPreAlignOptions(
            GantryDevice, HexDevice,
            n[0], n[1], n[2], n[3],
            TargetDistanceUm: o?.TargetDistanceUm ?? _targetUm,
            TargetAngleDeg: o?.TargetAngleDeg ?? _targetAngleDeg,
            Camera: camera,
            Out1ExposureUs: e1 > 0 ? e1 : null, Out2ExposureUs: e2 > 0 ? e2 : null,
            Fiber1ExposureUs: e3 > 0 ? e3 : null, Fiber2ExposureUs: e4 > 0 ? e4 : null);
        if (o?.WaveguideAngleDeg is { } wg) opts = opts with { WaveguideAngleDeg = wg };
        if (o?.MaxPlaceCorrectionMm is { } maxMm) opts = opts with { MaxCorrectionMm = maxMm };
        return opts;
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
            // The final apply step (Apply U / Move FAU) is auto-confirmed: once every reference
            // point is captured, the correction is applied / the FAU is moved without waiting for
            // an operator click. Only the per-point capture confirms still block on the operator.
            if (title.Contains("Apply", System.StringComparison.OrdinalIgnoreCase))
            {
                _pending = null;
                _awaitingApply = false;
                var msg = message.Replace("\n", " ");
                SetStatus(_phase, _phase == Phase.U ? "Auto-applying U…" : "Auto-moving FAU…");
                Log(LogLevel.Info, $"Auto-{(_phase == Phase.U ? "applying U" : "moving FAU")}: {msg}");
                ApplyButton.IsEnabled = false;
                CaptureButton.IsEnabled = false;
                _promptSurface?.Hide();
                RaiseRunState();
                tcs.TrySetResult(true);   // proceed without an operator click
                return;
            }

            _pending = tcs;
            _awaitingApply = false;
            SetStatus(_phase, message);
            SetDot(_captureIndex, captured: false);   // highlight the point now being refined
            CaptureButton.IsEnabled = true;
            _promptSurface?.Show(message, "Capture", "Cancel");
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
            Phase.Fau => "Accuracy place FAU",
            Phase.Grid => "Grid search",
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
