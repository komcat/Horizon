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
    private CancellationTokenSource? _cts;
    private double _budgetUm = 99.0;

    // Lifetime Z anchor. When set, Z+ travel is metered against a single ceiling across
    // repeated runs, so a second climb only gets the budget the first one didn't consume.
    private double? _lifetimeStartZmm;   // hex-right Z absolute (mm) captured at Set Start
    private double _lifetimeCeilingUm = 98.0;        // lifetime Z+ budget — editable via the ✎ button
    private bool _editingCeiling;                    // true while the ✎ button has the box in ceiling-edit mode
    private const double LifetimeFloorUm = 1.0;      // below this the anchor is spent

    public HillClimbPanel()
    {
        InitializeComponent();
    }

    /// <summary>Supplies the engine host (Layer 1 motion + logging) and the SiPhOG feedback client.</summary>
    public void Attach(EngineHost host, SiphogClient siphog)
    {
        _host = host;
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

        var config = HillClimbConfig.Default;
        config.ZBudget_um = distance;
        config.TargetSagnacV = target;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        // Block the process step / DUT-start buttons while the climb drives the hexapod.
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Hill climb");

        Log(LogLevel.Info,
            target > 0
                ? $"Hill climb starting: distance={distance:0.0} µm, target V={target:0.######} (exit at ≥90% = {target * 0.9:0.######})."
                : $"Hill climb starting: distance={distance:0.0} µm.");
        try
        {
            await _engine.RunAsync(HexDevice, _feedback, config, _cts.Token);
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
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

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
        ClearDistanceButton.IsEnabled = true;
        Log(LogLevel.Info, $"Lifetime Z anchor set at Z={_lifetimeStartZmm:F4} mm, ceiling {_lifetimeCeilingUm:0.0} µm.");
        await RefreshRemaining();
    }

    /// <summary>Forgets the lifetime anchor; Distance returns to manual entry.</summary>
    private void OnClearDistance(object sender, RoutedEventArgs e)
    {
        if (_engine?.IsRunning ?? false) return;
        _lifetimeStartZmm = null;
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
        StateText.Text = e.NewState.ToString();
        StateText.Foreground = e.NewState switch
        {
            HillClimbState.Idle => (Brush)FindResource("Subtle"),
            HillClimbState.Completed => (Brush)FindResource("Ok"),
            HillClimbState.Error => Brushes.IndianRed,
            HillClimbState.Cancelled => (Brush)FindResource("Subtle"),
            _ => (Brush)FindResource("Accent"),
        };
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

    private void OnClimbCompleted(object? sender, HillClimbResult r) => Ui(() =>
    {
        VPeakText.Text = r.VPeakGlobal.ToString("F6");
        ZCumText.Text = $"{r.ZTotalTraveled_um:F1} / {_budgetUm:F1}";
        Log(LogLevel.Info,
            $"Hill climb {r.StopReason}: V {r.VStart:F6} → {r.VPeakGlobal:F6}, Z={r.ZTotalTraveled_um:F1} µm, {r.LoopsExecuted} loops.");
    });

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "HillClimb", message));
}
