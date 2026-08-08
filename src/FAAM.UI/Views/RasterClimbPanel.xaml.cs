using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FAAM.UI.Siphog;
using Horizon.Engine;
using Horizon.Workflow.RasterClimb;

namespace FAAM.UI.Views;

/// <summary>
/// Engineer &gt; Testing Ground panel driving the Layer 2 <see cref="RasterClimbEngine"/>
/// — step Z+, raster the whole XY plane, move to that plane's maximum, repeat until the
/// signal reaches a fraction (70% by default) of the SiPhOG <c>TARGET SAG PWR</c>.
///
/// Two things live here rather than in Layer 2, because both are SiPhOG-specific and the
/// engine has no SiPhOG facet:
/// <list type="bullet">
/// <item>the <b>target</b> — read from the live frame's <c>TargetSagPwrV</c> unless the
/// operator types one in;</item>
/// <item>the <b>SLED guard</b> — before any motion, while the target SAG reads above the
/// limit (2.1 V), drop the server's SLED current by a step (5 mA), wait (5 s) and
/// re-check, so the climb starts from a sane operating point.</item>
/// </list>
/// Feedback is the same live SAG PWR the hill climber uses, taken from the Layer 2
/// provider the Run page registers, so no second SiPhOG connection is needed.
/// </summary>
public partial class RasterClimbPanel : UserControl
{
    // Bounded retries for the SLED guard (Power of 10 Rule 2 — a fixed cap, so a target
    // that never comes down can't spin here forever).
    private const int MaxSledGuardAttempts = 10;
    // Refuse to drive the SLED below this: the guard lowers current, it must not walk the
    // source down to nothing chasing a target that isn't responding.
    private const double MinSledCurrentMa = 40.0;

    private EngineHost? _host;
    private SiphogClient? _siphog;
    private RasterClimbEngine? _engine;
    private CancellationTokenSource? _cts;

    private double _targetV;   // resolved target for the run (live frame or typed)
    private bool _vStartShown; // the first progress sample of a run seeds the "V start" readout

    // One-shot Auto W maximizer, mirroring the hill climber it replaced on the Run page:
    // after a climb reaches the target the Auto W panel runs once, re-armed on each Start.
    private AutoWPanel? _autoW;
    private bool _autoWDoneThisRun;

    private RasterClimbState _lastState = RasterClimbState.Idle;

    /// <summary>Raised whenever the run state changes — the utility-app badge listens.</summary>
    public event Action? RunStateChanged;

    /// <summary>True while a climb is in progress (drives the launcher's Running badge).</summary>
    public bool IsRunning => _engine?.IsRunning ?? false;

    /// <summary>True when the last climb ended in an error (drives the launcher's Error badge).</summary>
    public bool IsError => _lastState == RasterClimbState.Error;

    /// <summary>Current state text, surfaced to the utility-app badge / app header.</summary>
    public string RunStatusDetail => StateText.Text;

    /// <summary>Begins a climb (utility-app <c>app.start</c> / the alignment chain's handoff).</summary>
    public void RequestStart() => OnStart(this, new RoutedEventArgs());

    /// <summary>Cancels an in-flight climb (utility-app <c>app.stop</c>); no-op when idle.</summary>
    public void RequestStop() => _cts?.Cancel();

    public RasterClimbPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Strips the panel down to the readout plus Start / Stop for the Run page, where the
    /// operator runs it on the defaults and has no business retuning step sizes mid-build.
    /// The hidden boxes keep their default text, so the run still reads its parameters from
    /// them; the Engineer Testing Ground copy leaves them visible for tuning.
    /// </summary>
    public void SetCompact()
    {
        RootBorder.Width = 280;                          // matches the Run page's utility slot
        SubtitleText.Visibility = Visibility.Collapsed;
        DetailRows.Visibility = Visibility.Collapsed;
        ParamsPanel.Visibility = Visibility.Collapsed;
        SledHelpText.Visibility = Visibility.Collapsed;
        SledParams.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Wires the Auto W maximizer so a climb that reaches its target auto-runs it once
    /// (gated by the Find First Light <c>AutoWEnabled</c> setting), as the hill climber did.
    /// </summary>
    public void SetAutoWPanel(AutoWPanel autoW) => _autoW = autoW;

    /// <summary>Supplies the engine host (Layer 1 motion + logging, Layer 2 feedback provider).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _engine = new RasterClimbEngine(host.Engine, Log);

        _engine.StateChanged += OnStateChanged;
        _engine.ProgressUpdated += OnProgressUpdated;
        _engine.PlaneCompleted += OnPlaneCompleted;
        _engine.RunCompleted += OnRunCompleted;

        // Block Start while a process step (or another alignment) holds the stage.
        host.Activity.Changed += OnActivityChanged;
        RefreshStartEnabled();
    }

    /// <summary>
    /// Supplies the shared SiPhOG client — the command channel the SLED guard needs and the
    /// source of the live target SAG power. Wired by the shell from the Run page's utility
    /// host, so the Engineer page reuses the one connection.
    /// </summary>
    public void AttachSiphog(SiphogClient siphog)
    {
        _siphog = siphog;
        RefreshSledReadout();
    }

    /// <summary>Cancels any in-flight climb when the Testing Ground tab / Engineer page is left.</summary>
    public void Deactivate() => _cts?.Cancel();

    /// <summary>Refreshes the SLED / target readout when the tab is shown.</summary>
    public void Activate() => RefreshSledReadout();

    private void OnActivityChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshStartEnabled();
        else Dispatcher.BeginInvoke(RefreshStartEnabled);
    }

    private void RefreshStartEnabled()
    {
        if (_host == null) return;
        bool running = _engine?.IsRunning ?? false;
        bool othersBusy = _host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !running);
        StartButton.IsEnabled = !running && !othersBusy;
    }

    private void RefreshSledReadout()
    {
        var f = _siphog?.Latest;
        SledText.Text = f.HasValue
            ? $"{f.Value.SledCurrentMa:0.0} mA / {f.Value.TargetSagPwrV:0.000} V"
            : "—";
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || _engine == null || _engine.IsRunning) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            StateText.Text = "Busy — wait for the current run";
            Log(LogLevel.Warning, "Raster climb blocked: a process step or alignment is running.");
            return;
        }

        var device = DeviceBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(device))
        {
            StateText.Text = "No device";
            return;
        }
        if (!_host.Engine.Motion.IsConnected(device))
        {
            StateText.Text = $"'{device}' not connected";
            Log(LogLevel.Warning, $"Raster climb blocked: '{device}' not connected.");
            return;
        }

        var feedback = _host.Process.GetHillClimbFeedback();
        if (feedback == null || !feedback.HasSample)
        {
            StateText.Text = "SiPhOG not streaming";
            Log(LogLevel.Warning, "Raster climb blocked: no SiPhOG feedback (connect the SiPhOG widget on the Run page).");
            return;
        }

        if (!TryReadDouble(ZStepBox, 0.05, 100.0, "0.0##", out double zStep)
            || !TryReadDouble(ZProbeBox, 0, 99, "0.#", out double zProbePct)
            || !TryReadDouble(XyStepBox, 0.05, 100.0, "0.0##", out double xyStep)
            || !TryReadDouble(StepsBox, 1, 50, "0", out double steps)
            || !TryReadDouble(MaxZBox, 0.5, 500.0, "0.#", out double maxZTravel)
            || !TryReadDouble(ExitPctBox, 1, 100, "0.#", out double exitPct)
            || !TryReadDouble(DropsBox, 1, 20, "0", out double drops)
            || !TryReadDouble(ContactDropBox, 0, 99, "0.#", out double contactPct))
        {
            StateText.Text = "Bad parameter";
            return;
        }

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _vStartShown = false;
        _autoWDoneThisRun = false;
        _cts = new CancellationTokenSource();
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Raster climb");
        RunStateChanged?.Invoke();

        RasterClimbResult? result = null;
        try
        {
            // 1) SLED guard — bring the target SAG power under the limit before any motion.
            if (SledGuardCheck.IsChecked == true && !await RunSledGuardAsync(_cts.Token))
                return;

            // 2) Resolve the target: typed value wins, else the live frame's TARGET SAG PWR.
            if (!TryResolveTarget(out _targetV))
                return;

            var config = RasterClimbConfig.Default;
            config.PlaneScan = ScanModeBox.SelectedIndex == 1 ? RasterPlaneScan.Spiral : RasterPlaneScan.Cross;
            config.PeakDropCount = (int)drops;
            config.ZStep_um = zStep;
            config.ZProbeSkipRasterFraction = zProbePct / 100.0;
            config.XyStep_um = xyStep;
            config.XyStepsPerSide = (int)steps;
            config.MaxZTravel_um = maxZTravel;
            config.ContactDropFraction = contactPct / 100.0;
            config.TargetSagnacV = _targetV;
            config.TargetReachedFraction = exitPct / 100.0;

            TargetText.Text = $"{_targetV:0.000} / {_targetV * config.TargetReachedFraction:0.000}";
            Log(LogLevel.Info,
                $"Raster climb starting on '{device}': Z+{zStep:0.###} µm up to {maxZTravel:0.#} µm " +
                $"({config.EffectiveZSteps} steps), " +
                $"Z probe {(zProbePct > 0 ? $"skips the XY search while a step gains >{zProbePct:0.#}%" : "off")}, " +
                $"{config.PlaneScan} plane scan {xyStep:0.###} µm ±{(int)steps} " +
                $"(≤{config.PointsPerRaster} pts/plane, past-peak after {(int)drops} drop(s)), " +
                $"target {_targetV:0.###} V → exit at {exitPct:0.#}% ({_targetV * config.TargetReachedFraction:0.###} V), " +
                $"contact guard {(contactPct > 0 ? $"-{contactPct:0.#}% of peak" : "off")}.");

            result = await _engine.RunAsync(device, feedback, config, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Info, "Raster climb cancelled.");
        }
        catch (Exception ex)
        {
            StateText.Text = $"Error: {ex.Message}";
            Log(LogLevel.Error, $"Raster climb failed: {ex.Message}");
        }
        finally
        {
            activity.Dispose();
            StopButton.IsEnabled = false;
            RefreshStartEnabled();
            RefreshSledReadout();
            _cts?.Dispose();
            _cts = null;
            RunStateChanged?.Invoke();
        }

        // After the finally: the hexapod is free, so the chained Auto W can take it.
        MaybeRunAutoW(result);
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// Pre-alignment SLED guard: while the SiPhOG reports a target SAG power above the
    /// limit, lower the server's SLED current by one step, wait for it to settle, and
    /// re-read. Returns true when the target is under the limit (or the guard has nothing
    /// to do); false when the climb must not start.
    /// </summary>
    private async Task<bool> RunSledGuardAsync(CancellationToken ct)
    {
        if (_siphog == null || !_siphog.IsConnected)
        {
            StateText.Text = "SiPhOG command channel unavailable";
            Log(LogLevel.Warning,
                "Raster climb blocked: the SLED guard needs the SiPhOG connection (connect it on the Run page, or clear the guard checkbox).");
            return false;
        }

        if (!TryReadDouble(SledLimitBox, 0.1, 10.0, "0.0##", out double limitV)
            || !TryReadDouble(SledStepBox, 0.1, 50.0, "0.#", out double stepMa)
            || !TryReadDouble(SledWaitBox, 0.5, 120.0, "0.#", out double waitS))
        {
            StateText.Text = "Bad SLED guard parameter";
            return false;
        }

        for (int attempt = 0; attempt < MaxSledGuardAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var frame = _siphog.Latest;
            if (frame == null)
            {
                StateText.Text = "No SiPhOG frame";
                Log(LogLevel.Warning, "Raster climb blocked: the SiPhOG is connected but not streaming frames.");
                return false;
            }

            RefreshSledReadout();
            if (frame.Value.TargetSagPwrV <= limitV)
            {
                if (attempt > 0)
                    Log(LogLevel.Info,
                        $"SLED guard: target SAG {frame.Value.TargetSagPwrV:0.000} V ≤ {limitV:0.###} V after {attempt} reduction(s) " +
                        $"(SLED now {frame.Value.SledCurrentMa:0.0} mA).");
                return true;
            }

            double next = frame.Value.SledCurrentMa - stepMa;
            if (next < MinSledCurrentMa)
            {
                StateText.Text = "SLED floor reached";
                Log(LogLevel.Warning,
                    $"Raster climb blocked: target SAG {frame.Value.TargetSagPwrV:0.000} V still above {limitV:0.###} V, " +
                    $"but the next reduction ({next:0.0} mA) is below the {MinSledCurrentMa:0.0} mA floor.");
                return false;
            }

            StateText.Text = $"SLED guard: {next:0.0} mA";
            var reply = await _siphog.SendCommandAsync(
                "SET_CURRENT:" + next.ToString("0.###", CultureInfo.InvariantCulture), ct);
            Log(LogLevel.Info,
                $"SLED guard: target SAG {frame.Value.TargetSagPwrV:0.000} V > {limitV:0.###} V — " +
                $"SLED {frame.Value.SledCurrentMa:0.0} → {next:0.0} mA ({reply}); waiting {waitS:0.#} s.");
            if (reply.StartsWith("ERR:", StringComparison.Ordinal))
            {
                StateText.Text = "SLED command rejected";
                Log(LogLevel.Warning, $"Raster climb blocked: the SiPhOG server rejected the current change ({reply}).");
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(waitS), ct);
        }

        StateText.Text = "SLED guard gave up";
        Log(LogLevel.Warning,
            $"Raster climb blocked: target SAG still above the limit after {MaxSledGuardAttempts} SLED reductions.");
        return false;
    }

    /// <summary>
    /// Resolves the run's target: a non-zero typed value wins (and is kept), otherwise the
    /// live frame's TARGET SAG PWR is used. Fails when neither is available — the target is
    /// this search's only exit criterion, so it must not be guessed.
    /// </summary>
    private bool TryResolveTarget(out double target)
    {
        target = 0;
        if (!double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed))
        {
            StateText.Text = "Bad target V";
            return false;
        }

        if (typed > 0)
        {
            target = typed;
            return true;
        }

        var frame = _siphog?.Latest;
        if (frame is not { TargetSagPwrV: > 0 })
        {
            StateText.Text = "No target SAG power";
            Log(LogLevel.Warning,
                "Raster climb blocked: no live TARGET SAG PWR from the SiPhOG — type a target V instead.");
            return false;
        }

        target = frame.Value.TargetSagPwrV;
        return true;
    }

    private static bool TryReadDouble(TextBox box, double min, double max, string fmt, out double value)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        value = Math.Clamp(value, min, max);
        box.Text = value.ToString(fmt, CultureInfo.InvariantCulture);
        return true;
    }

    // --- Engine events (fire on a background thread → marshal to the UI) ---

    private void OnStateChanged(object? sender, RasterClimbStateChangedEventArgs e) => Ui(() =>
    {
        _lastState = e.NewState;
        StateText.Text = e.NewState.ToString();

        bool running = e.NewState is RasterClimbState.SteppingZ or RasterClimbState.Rastering
            or RasterClimbState.MovingToPlaneBest or RasterClimbState.MovingToBest;
        RootBorder.Background = (Brush)FindResource(running ? "Running" : "Panel");

        StateText.Foreground = e.NewState switch
        {
            RasterClimbState.Idle => (Brush)FindResource("Subtle"),
            RasterClimbState.Completed => (Brush)FindResource("Ok"),
            RasterClimbState.Error => Brushes.IndianRed,
            RasterClimbState.Cancelled => (Brush)FindResource("Subtle"),
            _ => (Brush)FindResource("Accent"),
        };
        RunStateChanged?.Invoke();
    });

    private void OnProgressUpdated(object? sender, RasterClimbProgressEventArgs e) => Ui(() =>
    {
        if (!_vStartShown)
        {
            _vStartShown = true;
            VStartText.Text = e.VCurrent.ToString("F6");
        }
        VCurText.Text = e.VCurrent.ToString("F6");
        VBestText.Text = e.VRunBest.ToString("F6");
        PlaneText.Text = $"{e.PlaneIndex}  /  {e.ZOffset_um:+0.0;-0.0}";
        RasterText.Text = $"{e.PointsInPlane} / {e.PointsPerPlane}";
        if (_targetV > 0)
            FractionText.Text = $"{e.VPlaneBest / _targetV:P1}";
    });

    private void OnPlaneCompleted(object? sender, RasterClimbPlaneEventArgs e) => Ui(() =>
    {
        VBestText.Text = e.RunBestV.ToString("F6");
        FractionText.Text = $"{e.TargetFraction:P1}";
        BestOffsetText.Text = $"{e.BestOffsetA_um:+0.0;-0.0} / {e.BestOffsetB_um:+0.0;-0.0}";
    });

    private void OnRunCompleted(object? sender, RasterClimbResult r) => Ui(() =>
    {
        VBestText.Text = r.VBest.ToString("F6");
        FractionText.Text = $"{r.TargetFraction:P1}";
        BestOffsetText.Text = $"{r.BestOffsetA_um:+0.0;-0.0} / {r.BestOffsetB_um:+0.0;-0.0}";
        PlaneText.Text = $"{r.PlanesRastered}  /  {r.BestZOffset_um:+0.0;-0.0}";

        // A suspected contact ends the run "successfully" as far as the engine is concerned
        // (it retreated to the peak), but the operator must see it as an abnormal stop.
        bool contact = r.StopReason == RasterClimbStopReason.SignalDropped;
        if (contact)
        {
            StateText.Text = "Stopped — suspected contact";
            StateText.Foreground = Brushes.IndianRed;
        }

        Log(contact ? LogLevel.Warning : LogLevel.Info,
            $"Raster climb {r.StopReason}: V {r.VStart:F6} → {r.VBest:F6} ({r.TargetFraction:P1} of {r.TargetV:0.###} V), " +
            $"{r.PlanesRastered} plane(s) searched + {r.PlanesSkipped} skipped on the Z probe, " +
            $"{r.PointsSampled} points, Z travel {r.ZTravelled_um:+0.0;-0.0} µm" +
            (contact ? $"; retreated to the peak at Z{r.BestZOffset_um:+0.0;-0.0} µm." : "."));

    });

    /// <summary>
    /// One-shot Auto W maximizer, only on a clean target-reached finish: a budget-spent or
    /// suspected-contact stop is not a peak worth polishing. Called after the Start handler's
    /// finally has released the Alignment activity token — Auto W's own Start is gated on it,
    /// so firing this from the completion event instead would find the hexapod still busy.
    /// </summary>
    private void MaybeRunAutoW(RasterClimbResult? r)
    {
        if (r == null || r.StopReason != RasterClimbStopReason.TargetReached) return;
        if (_autoWDoneThisRun || _autoW == null) return;
        if (!(_host?.Settings.Current.FindFirstLight.AutoWEnabled ?? false)) return;

        _autoWDoneThisRun = true;
        Log(LogLevel.Info, "Raster climb reached target — auto-running the Auto W maximizer once.");
        _autoW.RequestStart();
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "RasterClimb", message));
}
