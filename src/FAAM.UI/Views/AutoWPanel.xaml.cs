using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FAAM.UI.Siphog;
using Horizon.Engine;
using Horizon.Workflow.AutoW;

namespace FAAM.UI.Views;

/// <summary>
/// <b>DEPRECATED — do not use or build on this.</b> Removed from the Run page; superseded by the Scanning panel's script presets (FAU_PowerClimb).
///
/// Run-page Auto W Maximizer panel — drives the Layer 2 <see cref="AutoWEngine"/>, a 1-D
/// line search over the hexapod W (rotation) axis that re-peaks XY at each W to score it,
/// maximising the SiPhOG <c>SAG PWR (V)</c>. Shares the same feedback seam
/// (<see cref="SiphogHillClimbFeedback"/>) and activity-gating as the Hill Climber panel; the
/// engine's events update the readouts. Sits below the Hill Climber on the Run-page grid.
/// </summary>
public partial class AutoWPanel : UserControl
{
    // FAAM rig: the PI hexapod carries the FAU (mirrors HillClimbPanel / UCorrectionPanel).
    private const string HexDevice = "hex-right";

    private EngineHost? _host;
    private AutoWEngine? _engine;
    private SiphogHillClimbFeedback? _feedback;
    private CancellationTokenSource? _cts;

    private AutoWState _lastState = AutoWState.Idle;

    /// <summary>Raised whenever the run state changes — the utility-app launcher badge listens.</summary>
    public event Action? RunStateChanged;

    /// <summary>True while a search is in progress (drives the launcher's Running badge).</summary>
    public bool IsRunning => _engine?.IsRunning ?? false;

    /// <summary>True when the last run ended in an error (drives the launcher's Error badge).</summary>
    public bool IsError => _lastState == AutoWState.Error;

    /// <summary>Current state text, surfaced to the launcher badge / app header.</summary>
    public string RunStatusDetail => StateText.Text;

    /// <summary>Begins a run (utility-app <c>app.start</c>) — same as pressing Start.</summary>
    public void RequestStart() => OnStart(this, new RoutedEventArgs());

    /// <summary>Cancels an in-flight run (utility-app <c>app.stop</c>); no-op when idle.</summary>
    public void RequestStop() => _cts?.Cancel();

    public AutoWPanel()
    {
        InitializeComponent();
    }

    /// <summary>Supplies the engine host (Layer 1 motion + logging) and the SiPhOG feedback client.</summary>
    public void Attach(EngineHost host, SiphogClient siphog)
    {
        _host = host;
        _feedback = new SiphogHillClimbFeedback(siphog);
        _engine = new AutoWEngine(host.Engine, Log);

        _engine.StateChanged += OnStateChanged;
        _engine.BaselineRecorded += OnBaselineRecorded;
        _engine.Measured += OnMeasured;
        _engine.Completed += OnCompleted;

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
            Log(LogLevel.Warning, "Auto W blocked: a process step or alignment is running.");
            return;
        }

        if (!_host.Engine.Motion.IsConnected(HexDevice))
        {
            StateText.Text = "Hexapod not connected";
            Log(LogLevel.Warning, "Auto W blocked: hexapod not connected.");
            return;
        }
        if (!_feedback.HasSample)
        {
            StateText.Text = "SiPhOG not streaming";
            Log(LogLevel.Warning, "Auto W blocked: no SiPhOG feedback (connect the SiPhOG widget).");
            return;
        }

        var config = AutoWConfig.Default;

        if (double.TryParse(WStepBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var wStep))
            config.WStep_deg = Math.Clamp(wStep, 0.001, 5.0);
        WStepBox.Text = config.WStep_deg.ToString("0.###", CultureInfo.InvariantCulture);

        if (double.TryParse(MaxWBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxW))
            config.MaxWExcursion_deg = Math.Clamp(maxW, config.WStep_deg, 30.0);
        MaxWBox.Text = config.MaxWExcursion_deg.ToString("0.###", CultureInfo.InvariantCulture);

        if (double.TryParse(VelocityBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vel))
            config.SystemVelocity_mmPerS = Math.Clamp(vel, 0.01, 10.0);
        VelocityBox.Text = config.SystemVelocity_mmPerS.ToString("0.##", CultureInfo.InvariantCulture);

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        // Block the process step / DUT-start buttons while the search drives the hexapod.
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Auto W");

        Log(LogLevel.Info,
            $"Auto W starting: W step={config.WStep_deg:0.###}°, max ±{config.MaxWExcursion_deg:0.###}°, velocity={config.SystemVelocity_mmPerS:0.##} mm/s.");
        _host.Activities.Publish(OperatorActivity.Section(FaamActivity.AutoW, "started",
            $"W step={config.WStep_deg:0.###}°, max ±{config.MaxWExcursion_deg:0.###}°"));
        try
        {
            await _engine.RunAsync(HexDevice, _feedback, config, _cts.Token);
        }
        catch (Exception ex)
        {
            StateText.Text = $"Error: {ex.Message}";
            Log(LogLevel.Error, $"Auto W failed: {ex.Message}");
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

    private void OnStateChanged(object? sender, AutoWStateChangedEventArgs e) => Ui(() =>
    {
        _lastState = e.NewState;
        StateText.Text = e.NewState.ToString();
        StateText.Foreground = e.NewState switch
        {
            AutoWState.Idle => (Brush)FindResource("Subtle"),
            AutoWState.Completed => (Brush)FindResource("Ok"),
            AutoWState.Error => Brushes.IndianRed,
            AutoWState.Cancelled => (Brush)FindResource("Subtle"),
            _ => (Brush)FindResource("Accent"),
        };
        RunStateChanged?.Invoke();
    });

    private void OnBaselineRecorded(object? sender, AutoWBaselineRecordedEventArgs e) => Ui(() =>
    {
        T0Text.Text = e.T0.ToString("F6");
        TCurText.Text = e.T0.ToString("F6");
        TPeakText.Text = e.T0.ToString("F6");
        WOffsetText.Text = "0.000";
        WPeakText.Text = "0.000";
        ThresholdText.Text = $"{e.EffectiveThreshold * 100:F2}%  (σ={e.Sigma:F6})";
    });

    private void OnMeasured(object? sender, AutoWMeasuredEventArgs e) => Ui(() =>
    {
        TCurText.Text = e.T.ToString("F6");
        TPeakText.Text = e.TPeakGlobal.ToString("F6");
        WOffsetText.Text = e.WOffsetDeg.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        if (e.ImprovedGlobal)
            WPeakText.Text = e.WOffsetDeg.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
    });

    private void OnCompleted(object? sender, AutoWResult r) => Ui(() =>
    {
        TPeakText.Text = r.TPeakGlobal.ToString("F6");
        WPeakText.Text = r.WPeakOffsetDeg.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        Log(LogLevel.Info,
            $"Auto W {r.StopReason}: T {r.T0:F6} → {r.TPeakGlobal:F6}, W offset {r.WPeakOffsetDeg:+0.###;-0.###}°, {r.MeasurementsTaken} measurements.");
        _host?.Activities.Publish(OperatorActivity.Section(FaamActivity.AutoW, "completed",
            $"T {r.T0:F6} → {r.TPeakGlobal:F6}, W offset {r.WPeakOffsetDeg:+0.###;-0.###}° ({r.StopReason})",
            new Dictionary<string, object?>
            {
                ["t0"] = r.T0,
                ["tPeak"] = r.TPeakGlobal,
                ["wPeakOffsetDeg"] = r.WPeakOffsetDeg,
                ["stopReason"] = r.StopReason.ToString(),
                ["measurements"] = r.MeasurementsTaken,
            }));
    });

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "AutoW", message));
}
