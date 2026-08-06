using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FAAM.UI.Siphog;
using Horizon.Engine;
using Horizon.Workflow.SpiralScan;

namespace FAAM.UI.Views;

/// <summary>
/// Engineer &gt; Testing Ground panel that drives the Layer 2
/// <see cref="SpiralSearchEngine"/> — a constant-pitch outward square-spiral signal
/// search in the XY plane. Feedback is the same live SiPhOG <c>SAG PWR (V)</c> the
/// hill climber uses, obtained through the Layer 2 feedback provider the Run page
/// registers (<c>IProcessService.GetHillClimbFeedback</c>), so no second SiPhOG
/// connection is needed. The engine's events update the readouts.
/// </summary>
public partial class SpiralRunPanel : UserControl
{
    private EngineHost? _host;
    private SpiralSearchEngine? _engine;
    private CancellationTokenSource? _cts;

    private SpiralSearchState _lastState = SpiralSearchState.Idle;

    public SpiralRunPanel()
    {
        InitializeComponent();
    }

    /// <summary>Supplies the engine host (Layer 1 motion + logging, Layer 2 feedback provider).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _engine = new SpiralSearchEngine(host.Engine, Log);

        _engine.StateChanged += OnStateChanged;
        _engine.BaselineRecorded += OnBaselineRecorded;
        _engine.ProgressUpdated += OnProgressUpdated;
        _engine.PointSampled += OnPointSampled;
        _engine.SearchCompleted += OnSearchCompleted;

        // Block Start while a process step (or another alignment) holds the stage.
        host.Activity.Changed += OnActivityChanged;
        RefreshStartEnabled();
    }

    /// <summary>Cancels any in-flight search when the Testing Ground tab / Engineer page is left.</summary>
    public void Deactivate() => _cts?.Cancel();

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

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || _engine == null || _engine.IsRunning) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            StateText.Text = "Busy — wait for the current run";
            Log(LogLevel.Warning, "Spiral blocked: a process step or alignment is running.");
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
            Log(LogLevel.Warning, $"Spiral blocked: '{device}' not connected.");
            return;
        }

        var feedback = _host.Process.GetHillClimbFeedback();
        if (feedback == null || !feedback.HasSample)
        {
            StateText.Text = "SiPhOG not streaming";
            Log(LogLevel.Warning, "Spiral blocked: no SiPhOG feedback (connect the SiPhOG widget on the Run page).");
            return;
        }

        if (!TryReadDouble(PitchBox, 0.1, 500.0, "0.0", out double pitch))
        {
            StateText.Text = "Bad pitch";
            return;
        }
        if (!TryReadDouble(RadiusBox, pitch, 5000.0, "0.0", out double radius))
        {
            StateText.Text = "Bad max radius";
            return;
        }

        double target = 0;
        if (!string.IsNullOrWhiteSpace(TargetBox.Text)
            && !double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
        {
            StateText.Text = "Bad target V";
            return;
        }
        if (target < 0) target = 0;
        TargetBox.Text = target.ToString("0.######", CultureInfo.InvariantCulture);

        var config = SpiralSearchConfig.Default;
        config.Pitch_um = pitch;
        config.MaxRadius_um = radius;
        config.TargetSagnacV = target;
        // Default plane axes (A=X=0, B=Y=1) already match the requested XY plane.

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Spiral search");

        Log(LogLevel.Info,
            $"Spiral search starting on '{device}': pitch={pitch:0.0} µm, max radius={radius:0.0} µm"
            + (target > 0 ? $", target V={target:0.######} (exit at ≥90% = {target * 0.9:0.######})." : "."));
        try
        {
            await _engine.RunAsync(device, new HillClimbToSpiralFeedback(feedback), config, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Info, "Spiral search cancelled.");
        }
        catch (Exception ex)
        {
            StateText.Text = $"Error: {ex.Message}";
            Log(LogLevel.Error, $"Spiral search failed: {ex.Message}");
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

    private static bool TryReadDouble(TextBox box, double min, double max, string fmt, out double value)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        value = Math.Clamp(value, min, max);
        box.Text = value.ToString(fmt, CultureInfo.InvariantCulture);
        return true;
    }

    // --- Engine events (fire on a background thread → marshal to the UI) ---

    private void OnStateChanged(object? sender, SpiralSearchStateChangedEventArgs e) => Ui(() =>
    {
        _lastState = e.NewState;
        StateText.Text = e.NewState.ToString();

        bool running = e.NewState is SpiralSearchState.RecordingBaseline
            or SpiralSearchState.Searching or SpiralSearchState.MovingToBest;
        RootBorder.Background = (Brush)FindResource(running ? "Running" : "Panel");

        StateText.Foreground = e.NewState switch
        {
            SpiralSearchState.Idle => (Brush)FindResource("Subtle"),
            SpiralSearchState.Completed => (Brush)FindResource("Ok"),
            SpiralSearchState.Error => Brushes.IndianRed,
            SpiralSearchState.Cancelled => (Brush)FindResource("Subtle"),
            _ => (Brush)FindResource("Accent"),
        };
    });

    private void OnBaselineRecorded(object? sender, SpiralSearchBaselineRecordedEventArgs e) => Ui(() =>
    {
        VStartText.Text = e.VStart.ToString("F6");
        VCurText.Text = e.VStart.ToString("F6");
        VBestText.Text = e.VStart.ToString("F6");
        ThresholdText.Text = $"{e.EffectiveThreshold * 100:F2}%  (σ={e.Sigma:F6})";
    });

    private void OnProgressUpdated(object? sender, SpiralSearchProgressEventArgs e) => Ui(() =>
    {
        VCurText.Text = e.VCurrent.ToString("F6");
        VBestText.Text = e.VBest.ToString("F6");
        ProgressText.Text = $"{e.PointsVisited}  /  {e.CurrentRadius_um:F1}";
    });

    private void OnPointSampled(object? sender, SpiralSearchPointEventArgs e) => Ui(() =>
    {
        VCurText.Text = e.V.ToString("F6");
        if (e.IsBest)
        {
            VBestText.Text = e.V.ToString("F6");
            BestOffsetText.Text = $"{e.OffsetA_um:+0.0;-0.0} / {e.OffsetB_um:+0.0;-0.0}";
        }
    });

    private void OnSearchCompleted(object? sender, SpiralSearchResult r) => Ui(() =>
    {
        VBestText.Text = r.VBest.ToString("F6");
        BestOffsetText.Text = $"{r.BestOffsetA_um:+0.0;-0.0} / {r.BestOffsetB_um:+0.0;-0.0}";
        ProgressText.Text = $"{r.PointsVisited}  /  {r.BestRadius_um:F1}";
        Log(LogLevel.Info,
            $"Spiral {r.StopReason}: V {r.VStart:F6} → {r.VBest:F6} at X{r.BestOffsetA_um:+0.0;-0.0} Y{r.BestOffsetB_um:+0.0;-0.0} µm, {r.PointsVisited} points.");
    });

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Spiral", message));
}
