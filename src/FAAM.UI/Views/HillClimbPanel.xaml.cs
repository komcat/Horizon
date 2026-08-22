using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FAAM.UI.Siphog;
using Horizon.Engine;
using Horizon.Workflow.HillClimb;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page Hill Climber panel — replicates the legacy acs_solution_saa Hill Climber
/// UI (State / V start·cur·peak / Z cumulative / Step Z·XY / Eff threshold / Distance /
/// Start / Stop) but drives the exact algorithm through the Layer 2
/// <see cref="HillClimbEngine"/>. Feedback is the live SiPhOG <c>SAG PWR (V)</c> via
/// <see cref="SiphogHillClimbFeedback"/>; the engine's events update the readouts.
/// </summary>
public partial class HillClimbPanel : UserControl
{
    // FAAM rig: the PI hexapod carries the FAU through the climb (mirrors UCorrectionPanel).
    private const string HexDevice = "hex-right";

    private EngineHost? _host;
    private HillClimbEngine? _engine;
    private SiphogHillClimbFeedback? _feedback;
    private SiphogClient? _siphog;          // shared SiPhOG client — used to set the SLED current at the handoff
    private CancellationTokenSource? _cts;
    private double _budgetUm = 99.0;

    // One-shot Auto W maximizer: after the climb parks at peak, auto-run the Auto W panel once
    // per "lifetime" (the same lifetime as the distance budget — re-armed on Set Start / Clear
    // Distance). _autoWDoneThisLifetime gates it to a single run until the next re-arm.
    private AutoWPanel? _autoW;
    private bool _autoWDoneThisLifetime;

    // --- Auto-SLED handoff (one-shot per Start press) ---
    // When the climb's SAG peak first reaches AutoSledTriggerV, switch the SiPhOG SLED to its
    // operating current, let it settle, trim it back down if that saturates the target SAG,
    // then re-baseline and continue the climb once from the current position. The continuation
    // does NOT switch the current again.
    private const double AutoSledTriggerV = 1.8;    // V — SAG PWR milestone that triggers the handoff
    private const int AutoSledCurrentMa = 110;      // mA — operating SLED current set at the milestone
    private const int AutoSledWaitMsFallback = 4000; // ms — settle time used when no host/settings are attached
    // Settle time after the current change is operator-tunable in the Find First Light settings
    // (FindFirstLightSettings.AutoSledWaitMs); read at handoff time so an edit takes effect next run.
    private int AutoSledWaitMs => Math.Max(0, _host?.Settings.Current.FindFirstLight.AutoSledWaitMs ?? AutoSledWaitMsFallback);

    // --- SLED trim after the handoff ---
    // The operating current can push the SiPhOG's TARGET SAG PWR into saturation, which flattens
    // the feedback the continuation climbs on. Above the enter threshold, step the current down
    // and re-measure until the target sits under the (lower) exit threshold — the gap between the
    // two is deliberate hysteresis, so a target hovering at the trigger doesn't get trimmed for
    // one noisy sample.
    private const double AutoSledTrimEnterV = 2.7;   // V — target SAG above this starts the trim
    private const double AutoSledTrimExitV = 2.1;    // V — trim until the target is below this
    private const double AutoSledTrimStepMa = 10.0;  // mA — reduction per iteration (110 → 100 → …)
    private const int AutoSledTrimSettleMs = 5000;   // ms — stabilise after each reduction, before measuring
    // Refuse to drive the SLED below this: the trim lowers current, it must not walk the source
    // down to nothing chasing a target that isn't responding (mirrors RasterClimbPanel's guard).
    private const double AutoSledTrimMinMa = 40.0;
    // Bounded retries (Power of 10 Rule 2 — a target that never comes down can't spin here forever).
    private const int MaxAutoSledTrimAttempts = 10;

    // Lifetime Z anchor. When set, Z+ travel is metered against a single ceiling across
    // repeated runs, so a second climb only gets the budget the first one didn't consume.
    private double? _lifetimeStartZmm;   // hex-right Z absolute (mm) captured at Set Start
    private double _lifetimeCeilingUm = 98.0;        // lifetime Z+ budget — editable via the ✎ button
    private bool _editingCeiling;                    // true while the ✎ button has the box in ceiling-edit mode
    private const double LifetimeFloorUm = 1.0;      // below this the anchor is spent

    private HillClimbState _lastState = HillClimbState.Idle;

    /// <summary>Raised whenever the climb state changes — the utility-app launcher badge listens.</summary>
    public event Action? RunStateChanged;

    /// <summary>True while a climb is in progress (drives the launcher's Running badge).</summary>
    public bool IsRunning => _engine?.IsRunning ?? false;

    /// <summary>True when the last climb ended in an error (drives the launcher's Error badge).</summary>
    public bool IsError => _lastState == HillClimbState.Error;

    /// <summary>Current state text, surfaced to the launcher badge / app header.</summary>
    public string RunStatusDetail => StateText.Text;

    /// <summary>Begins a climb (utility-app <c>app.start</c>) — same as pressing Start (uses the panel's current Distance / Target).</summary>
    public void RequestStart() => OnStart(this, new RoutedEventArgs());

    /// <summary>Cancels an in-flight climb (utility-app <c>app.stop</c>); no-op when idle.</summary>
    public void RequestStop() => _cts?.Cancel();

    public HillClimbPanel()
    {
        InitializeComponent();
    }

    /// <summary>Supplies the engine host (Layer 1 motion + logging) and the SiPhOG feedback client.</summary>
    public void Attach(EngineHost host, SiphogClient siphog)
    {
        _host = host;
        _siphog = siphog;
        _feedback = new SiphogHillClimbFeedback(siphog);
        _engine = new HillClimbEngine(host.Engine, Log);

        _engine.StateChanged += OnStateChanged;
        _engine.BaselineRecorded += OnBaselineRecorded;
        _engine.ProgressUpdated += OnProgressUpdated;
        _engine.StepCompleted += OnStepCompleted;
        _engine.ClimbCompleted += OnClimbCompleted;

        // Block Start while a process step (or another alignment) holds the hexapod.
        host.Activity.Changed += OnActivityChanged;
        RefreshStartEnabled();
    }

    /// <summary>
    /// Wires the Auto W maximizer panel so the climber can auto-run it once after it parks at
    /// peak. The one-shot's "lifetime" matches the distance-budget lifetime: it re-arms when the
    /// operator presses Set Start (re-anchor) or Clear Distance.
    /// </summary>
    public void SetAutoWPanel(AutoWPanel autoW) => _autoW = autoW;

    private void OnActivityChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshStartEnabled();
        else Dispatcher.BeginInvoke(RefreshStartEnabled);
    }

    /// <summary>Start is available only when nothing else (a process step or another alignment) holds the hexapod.</summary>
    private void RefreshStartEnabled()
    {
        if (_host == null) return;
        bool running = _engine?.IsRunning ?? false;
        bool othersBusy = _host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !running);
        StartButton.IsEnabled = !running && !othersBusy;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || _engine == null || _feedback == null || _engine.IsRunning) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            StateText.Text = "Busy — wait for the current run";
            Log(LogLevel.Warning, "Hill climb blocked: a process step or alignment is running.");
            return;
        }

        if (!_host.Engine.Motion.IsConnected(HexDevice))
        {
            StateText.Text = "Hexapod not connected";
            Log(LogLevel.Warning, "Hill climb blocked: hexapod not connected.");
            return;
        }
        if (!_feedback.HasSample)
        {
            StateText.Text = "SiPhOG not streaming";
            Log(LogLevel.Warning, "Hill climb blocked: no SiPhOG feedback (connect the SiPhOG widget).");
            return;
        }

        // With a lifetime anchor set, the per-run budget is the remaining travel against the
        // ceiling (so a 2nd run only gets what the 1st didn't consume); otherwise it's manual.
        double distance;
        if (_lifetimeStartZmm is not null)
        {
            var remaining = await RefreshRemaining();
            if (remaining is not double rem || rem < LifetimeFloorUm)
            {
                StateText.Text = "Lifetime Z budget exhausted";
                Log(LogLevel.Warning, "Hill climb blocked: lifetime Z budget exhausted — Clear Distance to reset.");
                return;
            }
            distance = Math.Clamp(rem, 1.0, 500.0);
        }
        else
        {
            if (!double.TryParse(DistanceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
            {
                StateText.Text = "Bad distance";
                return;
            }
            distance = Math.Clamp(distance, 1.0, 500.0);
            DistanceBox.Text = distance.ToString("0.0", CultureInfo.InvariantCulture);
        }
        _budgetUm = distance;

        // Optional Sagnac target — exits the climb early at ≥90% (0/blank = disabled).
        double target = 0;
        if (!string.IsNullOrWhiteSpace(TargetBox.Text)
            && !double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
        {
            StateText.Text = "Bad target V";
            return;
        }
        if (target < 0) target = 0;
        TargetBox.Text = target.ToString("0.######", CultureInfo.InvariantCulture);

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        // Block the process step / DUT-start buttons while the climb drives the hexapod.
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Hill climb");

        Log(LogLevel.Info,
            $"Hill climb starting: distance={distance:0.0} µm; auto-SLED handoff at peak ≥ {AutoSledTriggerV:0.###} V → {AutoSledCurrentMa} mA "
            + $"(trimmed −{AutoSledTrimStepMa:0.#} mA/{AutoSledTrimSettleMs / 1000.0:0.#} s while target SAG > {AutoSledTrimEnterV:0.###} V, until < {AutoSledTrimExitV:0.###} V), then continue"
            + (target > 0 ? $" toward target V={target:0.######} (exit at ≥90% = {target * 0.9:0.######})." : "."));
        _lastClimbResult = null;
        _host.Activities.Publish(OperatorActivity.Section(ActivityCategory.HillClimb, "started",
            $"distance={distance:0.0} µm" + (target > 0 ? $", target V={target:0.######}" : "")));
        bool climbedOk = false;
        try
        {
            // ---- Phase 1: climb until the SAG peak reaches the auto-SLED trigger (or budget/convergence). ----
            var phase1 = HillClimbConfig.Default;
            phase1.ZBudget_um = distance;
            phase1.TargetSagnacV = AutoSledTriggerV;
            phase1.TargetReachedFraction = 1.0;   // fire exactly at peak ≥ trigger, not at 90%
            var r1 = await _engine.RunAsync(HexDevice, _feedback, phase1, _cts.Token);

            // ---- Auto-SLED handoff + one continuation: only when the trigger was actually reached. ----
            var finalReason = r1.StopReason;
            if (r1.StopReason == HillClimbStopReason.TargetReached && !_cts.IsCancellationRequested)
                finalReason = await AutoSledHandoffAndContinueAsync(distance, target, _cts.Token);

            // A "normal finish" parks the hexapod at the global peak (budget / convergence / target);
            // cancel + error do not. Only that arms the one-shot Auto W maximizer below.
            climbedOk = !_cts.IsCancellationRequested
                && finalReason is HillClimbStopReason.ZBudgetReached
                    or HillClimbStopReason.Converged
                    or HillClimbStopReason.TargetReached;
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Info, "Hill climb cancelled.");
        }
        catch (Exception ex)
        {
            StateText.Text = $"Error: {ex.Message}";
            Log(LogLevel.Error, $"Hill climb failed: {ex.Message}");
        }
        finally
        {
            activity.Dispose();
            StopButton.IsEnabled = false;
            RefreshStartEnabled();
            _cts?.Dispose();
            _cts = null;
            // Update the remaining lifetime travel now that this run consumed some Z.
            if (_lifetimeStartZmm is not null) await RefreshRemaining();
        }

        var climbRes = _lastClimbResult;
        _host.Activities.Publish(OperatorActivity.Section(ActivityCategory.HillClimb,
            climbedOk ? "completed" : "stopped",
            climbRes != null
                ? $"V {climbRes.VStart:F6} → {climbRes.VPeakGlobal:F6}, Z={climbRes.ZTotalTraveled_um:F1} µm ({climbRes.StopReason})"
                : (climbedOk ? "completed" : "stopped"),
            climbRes != null
                ? new Dictionary<string, object?>
                {
                    ["vStart"] = climbRes.VStart,
                    ["vPeak"] = climbRes.VPeakGlobal,
                    ["zUm"] = climbRes.ZTotalTraveled_um,
                    ["stopReason"] = climbRes.StopReason.ToString(),
                    ["loops"] = climbRes.LoopsExecuted,
                }
                : null));

        // ---- One-shot Auto W maximizer: once per lifetime, re-armed on Set Start / Clear Distance. ----
        // The climb has parked at peak and released the hexapod (activity disposed in finally), so the
        // Auto W panel can take it. Runs at most once until the operator re-anchors or clears.
        if (climbedOk && !_autoWDoneThisLifetime && _autoW != null
            && (_host?.Settings.Current.FindFirstLight.AutoWEnabled ?? false))
        {
            _autoWDoneThisLifetime = true;
            Log(LogLevel.Info, "Hill climb parked at peak — auto-running Auto W maximizer once for this lifetime.");
            _autoW.RequestStart();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// One-shot SLED handoff fired when the climb's SAG peak first reaches
    /// <see cref="AutoSledTriggerV"/>: set the SiPhOG SLED to <see cref="AutoSledCurrentMa"/> mA,
    /// wait <see cref="AutoSledWaitMs"/> ms for it to settle, trim the current back down if that
    /// saturated the target SAG (see <see cref="TrimSledForTargetSagAsync"/>), then re-baseline and
    /// continue the climb once from the current position. A fresh <see cref="HillClimbEngine.RunAsync"/> records
    /// a new baseline, so the operating-current signal isn't compared against the old baseline. The
    /// continuation honors the panel's Target V box and does NOT switch the current again.
    /// </summary>
    /// <returns>The stop reason of the final climb the hexapod parked on (the continuation's, or
    /// the phase-1 <c>TargetReached</c> when the continuation is skipped) — used by the caller to
    /// decide whether the run ended in a normal peak-parked finish.</returns>
    private async System.Threading.Tasks.Task<HillClimbStopReason> AutoSledHandoffAndContinueAsync(
        double phase1Distance, double continueTarget, CancellationToken ct)
    {
        if (_host == null || _engine == null || _feedback == null) return HillClimbStopReason.TargetReached;

        // 1) Switch the SLED to its operating current.
        if (_siphog != null)
        {
            var reply = await _siphog.SendCommandAsync($"SET_CURRENT:{AutoSledCurrentMa}", ct);
            Log(LogLevel.Info, $"Auto-SLED: peak ≥ {AutoSledTriggerV:0.###} V — set SLED {AutoSledCurrentMa} mA → {reply}");
        }
        else
        {
            Log(LogLevel.Warning, "Auto-SLED: no SiPhOG client — skipping current change, continuing climb.");
        }

        // 2) Let the new SLED current settle (operator-tunable in Find First Light settings).
        int settleMs = AutoSledWaitMs;
        Log(LogLevel.Info, $"Auto-SLED: settling {settleMs} ms before re-baselining.");
        await System.Threading.Tasks.Task.Delay(settleMs, ct);

        // 3) Trim the current back down if the operating current saturated the target SAG.
        await TrimSledForTargetSagAsync(ct);

        // 4) Budget for the continuation: remaining lifetime travel if anchored, else a fresh run.
        double continueDistance = phase1Distance;
        if (_lifetimeStartZmm is not null)
        {
            var remaining = await RefreshRemaining();
            if (remaining is not double rem || rem < LifetimeFloorUm)
            {
                Log(LogLevel.Warning, "Auto-SLED: lifetime Z budget exhausted — SLED set, continuation skipped.");
                return HillClimbStopReason.TargetReached;   // still parked at the phase-1 peak
            }
            continueDistance = Math.Clamp(rem, 1.0, 500.0);
        }
        _budgetUm = continueDistance;

        // 5) Re-baseline + continue climbing from the current position (one continuation, no further switch).
        Log(LogLevel.Info, $"Auto-SLED: re-baselining and continuing climb (distance={continueDistance:0.0} µm).");
        var phase2 = HillClimbConfig.Default;
        phase2.ZBudget_um = continueDistance;
        phase2.TargetSagnacV = continueTarget;   // 0 = run to budget/convergence
        var r2 = await _engine.RunAsync(HexDevice, _feedback, phase2, ct);
        return r2.StopReason;
    }

    /// <summary>
    /// SLED trim, run once after the operating current has settled: while the SiPhOG reports a
    /// TARGET SAG PWR above <see cref="AutoSledTrimEnterV"/>, lower the server's SLED current by
    /// <see cref="AutoSledTrimStepMa"/> mA (110 → 100 → …), wait <see cref="AutoSledTrimSettleMs"/>
    /// ms for it to stabilise and re-measure, until the target reads below
    /// <see cref="AutoSledTrimExitV"/>. The enter/exit gap is hysteresis: the trim only starts on a
    /// clearly saturated target, then keeps going to a comfortable margin.
    ///
    /// A trim that cannot finish (no frames, current floor, rejected command, attempts exhausted)
    /// logs and returns — the SLED is already at a sane-or-lower current and the hexapod is parked
    /// at the phase-1 peak, so the continuation still runs; it just climbs on the hotter signal.
    /// </summary>
    private async System.Threading.Tasks.Task TrimSledForTargetSagAsync(CancellationToken ct)
    {
        if (_siphog == null) return;

        var entry = _siphog.Latest;
        if (entry is null)
        {
            Log(LogLevel.Warning, "SLED trim: no SiPhOG frame — skipping the trim, continuing the climb.");
            return;
        }
        if (entry.Value.TargetSagPwrV <= AutoSledTrimEnterV)
        {
            Log(LogLevel.Info,
                $"SLED trim: target SAG {entry.Value.TargetSagPwrV:0.000} V ≤ {AutoSledTrimEnterV:0.###} V — no trim needed.");
            return;
        }

        Log(LogLevel.Info,
            $"SLED trim: target SAG {entry.Value.TargetSagPwrV:0.000} V > {AutoSledTrimEnterV:0.###} V — " +
            $"stepping the SLED down {AutoSledTrimStepMa:0.#} mA at a time until it reads < {AutoSledTrimExitV:0.###} V.");

        for (int attempt = 0; attempt < MaxAutoSledTrimAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var frame = _siphog.Latest;
            if (frame is null)
            {
                Log(LogLevel.Warning, "SLED trim: SiPhOG stopped streaming — trim abandoned, continuing the climb.");
                return;
            }

            double targetSag = frame.Value.TargetSagPwrV;
            if (targetSag < AutoSledTrimExitV)
            {
                Log(LogLevel.Info,
                    $"SLED trim: target SAG {targetSag:0.000} V < {AutoSledTrimExitV:0.###} V after {attempt} reduction(s) " +
                    $"(SLED now {frame.Value.SledCurrentMa:0.0} mA).");
                return;
            }

            double next = frame.Value.SledCurrentMa - AutoSledTrimStepMa;
            if (next < AutoSledTrimMinMa)
            {
                Log(LogLevel.Warning,
                    $"SLED trim: target SAG {targetSag:0.000} V still ≥ {AutoSledTrimExitV:0.###} V, but the next reduction " +
                    $"({next:0.0} mA) is below the {AutoSledTrimMinMa:0.0} mA floor — trim stopped, continuing the climb.");
                return;
            }

            StateText.Text = $"SLED trim: {next:0.0} mA";
            var reply = await _siphog.SendCommandAsync(
                "SET_CURRENT:" + next.ToString("0.###", CultureInfo.InvariantCulture), ct);
            Log(LogLevel.Info,
                $"SLED trim: target SAG {targetSag:0.000} V ≥ {AutoSledTrimExitV:0.###} V — " +
                $"SLED {frame.Value.SledCurrentMa:0.0} → {next:0.0} mA ({reply}); waiting {AutoSledTrimSettleMs} ms.");
            if (reply.StartsWith("ERR:", StringComparison.Ordinal))
            {
                Log(LogLevel.Warning,
                    $"SLED trim: the SiPhOG server rejected the current change ({reply}) — trim stopped, continuing the climb.");
                return;
            }

            await System.Threading.Tasks.Task.Delay(AutoSledTrimSettleMs, ct);
        }

        Log(LogLevel.Warning,
            $"SLED trim: target SAG still ≥ {AutoSledTrimExitV:0.###} V after {MaxAutoSledTrimAttempts} reductions — " +
            "trim gave up, continuing the climb.");
    }

    // --- Lifetime Z anchor ---

    /// <summary>Anchors the lifetime budget at the current hex-right Z against the fixed 98 µm ceiling.</summary>
    private async void OnSetStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || (_engine?.IsRunning ?? false)) return;

        if (!_host.Engine.Motion.IsConnected(HexDevice))
        {
            StateText.Text = "Hexapod not connected";
            Log(LogLevel.Warning, "Set Start blocked: hexapod not connected.");
            return;
        }

        var pos = await _host.Engine.Motion.GetPositionAsync(HexDevice);
        if (!pos.IsSuccess)
        {
            StateText.Text = "Z read failed";
            Log(LogLevel.Warning, $"Set Start blocked: could not read hex-right Z ({pos.Message}).");
            return;
        }

        _lifetimeStartZmm = pos.Value.Components[2];   // Z (X,Y,Z,U,V,W)
        _autoWDoneThisLifetime = false;                // re-arm the one-shot Auto W for the new lifetime
        ClearDistanceButton.IsEnabled = true;
        Log(LogLevel.Info, $"Lifetime Z anchor set at Z={_lifetimeStartZmm:F4} mm, ceiling {_lifetimeCeilingUm:0.0} µm.");
        await RefreshRemaining();
    }

    /// <summary>Forgets the lifetime anchor; Distance returns to manual entry.</summary>
    private void OnClearDistance(object sender, RoutedEventArgs e)
    {
        if (_engine?.IsRunning ?? false) return;
        _lifetimeStartZmm = null;
        _autoWDoneThisLifetime = false;                // re-arm the one-shot Auto W
        ClearDistanceButton.IsEnabled = false;
        DistanceLabel.Text = "Distance (µm)";
        DistanceBox.IsReadOnly = false;
        DistanceBox.Text = _lifetimeCeilingUm.ToString("0.0", CultureInfo.InvariantCulture);
        Log(LogLevel.Info, "Lifetime Z anchor cleared.");
    }

    /// <summary>
    /// Pencil button: edits the lifetime ceiling (default 98 µm) inline in the Distance box.
    /// First click drops the box into ceiling-edit mode; second click (✓) commits, clamps to
    /// 1–500 µm, and — if an anchor is set — recomputes the remaining travel against it.
    /// </summary>
    private async void OnEditCeiling(object sender, RoutedEventArgs e)
    {
        if (_engine?.IsRunning ?? false) return;

        if (!_editingCeiling)
        {
            _editingCeiling = true;
            EditCeilingButton.Content = "✓";   // ✓
            DistanceLabel.Text = "Ceiling (µm)";
            DistanceBox.IsReadOnly = false;
            DistanceBox.Text = _lifetimeCeilingUm.ToString("0.0", CultureInfo.InvariantCulture);
            DistanceBox.Focus();
            DistanceBox.SelectAll();
            return;
        }

        // Commit.
        if (double.TryParse(DistanceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
            _lifetimeCeilingUm = Math.Clamp(c, 1.0, 500.0);
        _editingCeiling = false;
        EditCeilingButton.Content = "✎";       // ✎
        Log(LogLevel.Info, $"Hill-climb lifetime ceiling set to {_lifetimeCeilingUm:0.0} µm.");

        if (_lifetimeStartZmm is not null)
        {
            await RefreshRemaining();               // re-meter against the new ceiling
        }
        else
        {
            DistanceLabel.Text = "Distance (µm)";
            DistanceBox.IsReadOnly = false;
            DistanceBox.Text = _lifetimeCeilingUm.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Live remaining lifetime travel = ceiling − |currentZ − start| in µm, clamped to ≥ 0.
    /// The climb only travels Z+ from the anchor and parks at the peak, so the magnitude of
    /// the Z displacement is the consumed travel — and using |·| keeps it correct whichever
    /// sign the controller assigns to a Z+ move. Writes the result into the (read-only)
    /// Distance box and returns it; returns null when no anchor is set.
    /// </summary>
    private async System.Threading.Tasks.Task<double?> RefreshRemaining()
    {
        if (_lifetimeStartZmm is not double startMm || _host == null) return null;

        double consumedUm = 0;
        if (_host.Engine.Motion.IsConnected(HexDevice))
        {
            var pos = await _host.Engine.Motion.GetPositionAsync(HexDevice);
            if (pos.IsSuccess)
                consumedUm = Math.Abs(pos.Value.Components[2] - startMm) * 1000.0;
        }

        double remaining = Math.Max(0, _lifetimeCeilingUm - consumedUm);
        DistanceBox.IsReadOnly = true;
        DistanceLabel.Text = $"Remaining (µm)  [used {consumedUm:0.0}]";
        DistanceBox.Text = remaining.ToString("0.0", CultureInfo.InvariantCulture);
        return remaining;
    }

    // --- Engine events (fire on a background thread → marshal to the UI) ---

    private void OnStateChanged(object? sender, HillClimbStateChangedEventArgs e) => Ui(() =>
    {
        _lastState = e.NewState;
        StateText.Text = e.NewState.ToString();

        // Yellow panel while a climb is actively driving the hexapod; default panel colour otherwise.
        bool running = e.NewState is HillClimbState.RecordingBaseline
            or HillClimbState.Climbing or HillClimbState.MovingToPeak;
        RootBorder.Background = (Brush)FindResource(running ? "Running" : "Panel");

        StateText.Foreground = e.NewState switch
        {
            HillClimbState.Idle => (Brush)FindResource("Subtle"),
            HillClimbState.Completed => (Brush)FindResource("Ok"),
            HillClimbState.Error => Brushes.IndianRed,
            HillClimbState.Cancelled => (Brush)FindResource("Subtle"),
            _ => (Brush)FindResource("Accent"),
        };
        RunStateChanged?.Invoke();   // refresh the launcher badge
    });

    private void OnBaselineRecorded(object? sender, HillClimbBaselineRecordedEventArgs e) => Ui(() =>
    {
        VStartText.Text = e.VStart.ToString("F6");
        VCurText.Text = e.VStart.ToString("F6");
        VPeakText.Text = e.VStart.ToString("F6");
        ThresholdText.Text = $"{e.EffectiveThreshold * 100:F2}%  (σ={e.Sigma:F6})";
    });

    private void OnProgressUpdated(object? sender, HillClimbProgressEventArgs e) => Ui(() =>
    {
        VCurText.Text = e.VCurrent.ToString("F6");
        VPeakText.Text = e.VPeakGlobal.ToString("F6");
        ZCumText.Text = $"{e.ZCumulative_um:F1} / {_budgetUm:F1}";
        StepText.Text = $"{e.CurrentZStep_um:F1} / {e.CurrentXYStep_um:F1}";
    });

    private void OnStepCompleted(object? sender, HillClimbStepEventArgs e) => Ui(() =>
    {
        VCurText.Text = e.VAfter.ToString("F6");
        if (e.ImprovedGlobal) VPeakText.Text = e.VAfter.ToString("F6");
        ZCumText.Text = $"{e.ZCumulative_um:F1} / {_budgetUm:F1}";
    });

    /// <summary>Final result of the most recent completed <see cref="HillClimbEngine.RunAsync"/> —
    /// with the SLED-handoff continuation this ends as the true parked result. Captured synchronously
    /// (not on the UI marshal) so <see cref="OnStart"/>'s finally can read it without a race.</summary>
    private HillClimbResult? _lastClimbResult;

    private void OnClimbCompleted(object? sender, HillClimbResult r)
    {
        _lastClimbResult = r;
        Ui(() =>
        {
            VPeakText.Text = r.VPeakGlobal.ToString("F6");
            ZCumText.Text = $"{r.ZTotalTraveled_um:F1} / {_budgetUm:F1}";
            Log(LogLevel.Info,
                $"Hill climb {r.StopReason}: V {r.VStart:F6} → {r.VPeakGlobal:F6}, Z={r.ZTotalTraveled_um:F1} µm, {r.LoopsExecuted} loops.");
        });
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "HillClimb", message));
}
