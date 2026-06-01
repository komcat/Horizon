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

        if (!double.TryParse(DistanceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance))
        {
            StateText.Text = "Bad distance";
            return;
        }
        distance = Math.Clamp(distance, 1.0, 500.0);
        DistanceBox.Text = distance.ToString("0.0", CultureInfo.InvariantCulture);
        _budgetUm = distance;

        var config = HillClimbConfig.Default;
        config.ZBudget_um = distance;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        // Block the process step / DUT-start buttons while the climb drives the hexapod.
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Hill climb");

        Log(LogLevel.Info, $"Hill climb starting: distance={distance:0.0} µm.");
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
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

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
