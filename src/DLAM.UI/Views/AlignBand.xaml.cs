using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace DLAM.UI.Views;

/// <summary>
/// The double-lens alignment band on DLAM's Run page. Each button runs ONE Layer 2 block
/// (<c>scan.xcan</c> / <c>scan.climb</c> / <c>scan.xcanTwoLens</c>) as a one-step program
/// built from <see cref="AlignBandSettings"/>, so the band runs exactly the code an Engineer
/// program would — including the fail-safe park on the best point when stopped.
///
/// While a scan runs the band holds the activity gate's alignment side (the process steps lock),
/// and it refuses to start while a program or step sequence runs. Its Stop, the Run page's STOP
/// (<see cref="EngineHost.StopPressed"/>) and a safety trip all cancel it.
///
/// Readouts refresh every <see cref="PollInterval"/> while the page is shown: both lens poses,
/// and both SMU channels — read directly while nothing runs, otherwise from the SMU stream
/// (never a second query against an instrument a scan is reading).
/// </summary>
public partial class AlignBand : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _poll;
    private EngineHost? _host;
    private AlignBandSettings _settings = new();
    private CancellationTokenSource? _cts;   // the band's running scan; null when idle
    private bool _polling;

    // Latest photocurrent per channel (A); NaN = no reading. Written by the poll and the SMU stream.
    private double _ch1 = double.NaN;
    private double _ch2 = double.NaN;

    public AlignBand()
    {
        InitializeComponent();
        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += OnPollTick;
    }

    /// <summary>Wires the band to the engine host. Call once, when the Run page is built.</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        host.StopPressed += Cancel;
        host.SafetyAbort += _ => Dispatcher.BeginInvoke(Cancel);
        host.Activity.Changed += () => Dispatcher.BeginInvoke(UpdateEnabled);
        host.Engine.Smu.Sampled += OnSmuSampled;
        LoadSettings();
    }

    /// <summary>Re-reads the presets (Machine › Settings may have changed them) and starts the readouts.</summary>
    public void Activate()
    {
        LoadSettings();
        UpdateEnabled();
        _poll.Start();
    }

    public void Deactivate() => _poll.Stop();

    private void LoadSettings()
    {
        try
        {
            _settings = AlignBandSettings.Load();
        }
        catch (Exception ex)
        {
            _settings = new AlignBandSettings();
            Log(LogLevel.Warning, $"Alignment band: could not read {AlignBandSettings.FileName}.json — using defaults ({ex.Message}).");
        }

        LeftDevice.Text = _settings.LeftDevice;
        RightDevice.Text = _settings.RightDevice;
        Ch1Label.Text = ChannelLabel(1);
        Ch2Label.Text = ChannelLabel(2);
        Ch1Label.ToolTip = $"{_settings.Smu} channel 1";
        Ch2Label.ToolTip = $"{_settings.Smu} channel 2";
        TwoLensTarget.Text = $"→ {Ua(_settings.TwoLensTargetUa)}";
        TargetALabel.Text = Ua(_settings.TargetAUa);
        TargetBLabel.Text = Ua(_settings.TargetBUa);
        TargetAGroup.ToolTip = TargetTip(_settings.TargetAUa, _settings.TargetAZoneLowUa,
            _settings.TargetACoarseStepsUm, _settings.TargetAFineStepsUm);
        TargetBGroup.ToolTip = TargetTip(_settings.TargetBUa, _settings.TargetBZoneLowUa,
            _settings.TargetBCoarseStepsUm, _settings.TargetBFineStepsUm);
    }

    private static string TargetTip(double target, double zoneLow, string coarse, string fine)
        => $"One adaptive XCAN pass that stops at {Ua(target)}\n"
           + $"Zone {Ua(zoneLow)} … {Ua(target)} · coarse {coarse} µm · fine {fine} µm\n"
           + "Right-click to edit";

    private static string Ua(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " µA";

    private string ChannelLabel(int channel)
        => $"ch{channel}{(channel == _settings.FeedbackChannel ? " · feedback" : "")}";

    // --- run ---

    private async void OnLensRun(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
            return;

        var parts = tag.Split(':');
        bool left = parts[0] == "left";
        var device = left ? _settings.LeftDevice : _settings.RightDevice;
        var lens = left ? "Left lens" : "Right lens";

        var (label, step) = parts[1] switch
        {
            "coarse" => ($"{lens} XCAN coarse", Xcan(device, _settings.CoarseStepsUm)),
            "fine" => ($"{lens} XCAN fine", Xcan(device, _settings.FineStepsUm)),
            _ => ($"{lens} climb", Climb(device)),
        };
        await RunAsync(label, step);
    }

    /// <summary>
    /// An XCAN-to-target button (tag <c>a|b : left|right : coarse|fine</c>): one pass with the
    /// ladder, adaptive over [the target's zone low, the target], exiting at the target.
    /// </summary>
    private async void OnTargetRun(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
            return;

        var parts = tag.Split(':');
        bool targetB = parts[0] == "b";
        bool left = parts[1] == "left";
        bool fine = parts[2] == "fine";
        double target = targetB ? _settings.TargetBUa : _settings.TargetAUa;
        double zoneLow = targetB ? _settings.TargetBZoneLowUa : _settings.TargetAZoneLowUa;
        string ladder = (targetB, fine) switch
        {
            (true, true) => _settings.TargetBFineStepsUm,
            (true, false) => _settings.TargetBCoarseStepsUm,
            (false, true) => _settings.TargetAFineStepsUm,
            _ => _settings.TargetACoarseStepsUm,
        };

        var step = Step("scan.xcan", left ? _settings.LeftDevice : _settings.RightDevice, new Dictionary<string, string>
        {
            ["axes"] = _settings.Axes,
            ["stepsUm"] = ladder,
            ["minSignal"] = Num(_settings.MinSignalUa),
            ["adaptive"] = "true",
            ["zoneLow"] = Num(zoneLow),
            ["zoneHigh"] = Num(target),
            ["exitTarget"] = Num(target),
            ["captureAs"] = "band_xcan",
        });
        await RunAsync($"{(left ? "Left" : "Right")} {(fine ? "fine" : "coarse")} XCAN → {Ua(target)}", step);
    }

    /// <summary>Right-click on a target group: edit its threshold, zone low and ladders.</summary>
    private void OnTargetEdit(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
            return;
        e.Handled = true;

        LoadSettings();   // edit the file's current values, not a stale copy
        bool targetB = tag == "b";
        var dlg = new XcanTargetWindow(_settings, targetB) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Saved is not { } saved)
            return;

        LoadSettings();
        var (target, zoneLow, coarse, fine) = targetB
            ? (saved.TargetBUa, saved.TargetBZoneLowUa, saved.TargetBCoarseStepsUm, saved.TargetBFineStepsUm)
            : (saved.TargetAUa, saved.TargetAZoneLowUa, saved.TargetACoarseStepsUm, saved.TargetAFineStepsUm);
        var what = $"XCAN target {(targetB ? "B" : "A")} set: {Ua(target)}, zone low {Ua(zoneLow)}, coarse {coarse} µm, fine {fine} µm.";
        ShowStatus(what);
        Log(LogLevel.Info, what);
    }

    private async void OnTwoLens(object sender, RoutedEventArgs e)
    {
        var step = Step("scan.xcanTwoLens", device: "", new Dictionary<string, string>
        {
            ["left"] = _settings.LeftDevice,
            ["right"] = _settings.RightDevice,
            ["target"] = Num(_settings.TwoLensTargetUa),
            ["zoneLow"] = Num(_settings.ZoneLowUa),
            ["axes"] = _settings.Axes,
            ["coarseStepsUm"] = _settings.CoarseStepsUm,
            ["fineStepsUm"] = _settings.FineStepsUm,
            ["minSignal"] = Num(_settings.MinSignalUa),
            ["captureAs"] = "band_xtl",
        });
        await RunAsync("Two-lens XCAN", step);
    }

    private WorkflowStep Xcan(string device, string stepsUm)
    {
        var p = new Dictionary<string, string>
        {
            ["axes"] = _settings.Axes,
            ["stepsUm"] = stepsUm,
            ["minSignal"] = Num(_settings.MinSignalUa),
            ["captureAs"] = "band_xcan",
        };
        if (_settings.ExitTargetUa > 0)
            p["exitTarget"] = Num(_settings.ExitTargetUa);
        return Step("scan.xcan", device, p);
    }

    private WorkflowStep Climb(string device) => Step("scan.climb", device, new Dictionary<string, string>
    {
        ["target"] = Num(_settings.ClimbTargetUa),
        ["zStepUm"] = Num(_settings.ClimbZStepUm),
        ["maxZUm"] = Num(_settings.ClimbMaxZUm),
        ["captureAs"] = "band_climb",
    });

    /// <summary>One block with the band's feedback (the configured SMU channel, levels in µA).</summary>
    private WorkflowStep Step(string op, string device, Dictionary<string, string> parameters)
    {
        parameters["signal"] = _settings.Signal;
        parameters["unit"] = "uA";
        return new WorkflowStep { Id = "align-band", Name = op, Op = op, Device = device, Parameters = parameters };
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs one block as a one-step program under the alignment side of the activity gate.
    /// Refused while anything else runs, while a safety flag is raised, or with unusable presets.
    /// </summary>
    private async Task RunAsync(string label, WorkflowStep step)
    {
        if (_host == null)
            return;

        LoadSettings();   // presets edited on Machine › Settings since the page was shown
        string? refused = BusyReason()
            ?? (_host.Engine.Safety.GetState().FlagRaised ? "a safety flag is raised — acknowledge it first" : null)
            ?? (_settings.Validate() is { } bad ? $"the alignment band settings are not usable: {bad}" : null);
        if (refused != null)
        {
            ShowStatus($"{label} refused — {refused}.");
            Log(LogLevel.Warning, $"{label} refused — {refused}.");
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        UpdateEnabled();
        ShowStatus($"{label} — running…");
        Log(LogLevel.Info, $"{label} started ({step.Op}).");
        _host.Process.RecordDutValue("align", $"{label}: started");
        var started = DateTime.UtcNow;

        string outcome;
        try
        {
            using var gate = _host.Activity.Begin(ActivityKind.Alignment, label);
            var program = new WorkflowProgram { Name = $"Run page · {label}", Steps = new List<WorkflowStep> { step } };
            var r = await _host.Process.RunAsync(program, serial: null, cts.Token);
            var secs = (DateTime.UtcNow - started).TotalSeconds;
            outcome = r.IsSuccess ? $"{label} — done in {secs:0.0} s."
                : r.Error == ErrorCode.ProcessCancelled ? $"{label} — stopped after {secs:0.0} s."
                : $"{label} — failed: {r.Message}";
        }
        catch (OperationCanceledException)
        {
            outcome = $"{label} — stopped.";
        }
        catch (Exception ex)
        {
            outcome = $"{label} — error: {ex.Message}";
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            UpdateEnabled();
        }

        ShowStatus(outcome);
        Log(outcome.Contains("— done", StringComparison.Ordinal) ? LogLevel.Info : LogLevel.Warning, outcome);
        _host.Process.RecordDutValue("align", outcome);
    }

    private void OnStop(object sender, RoutedEventArgs e) => Cancel();

    /// <summary>Cancels the band's scan (the scan parks on its best point). UI thread.</summary>
    private void Cancel()
    {
        if (_cts == null)
            return;
        _cts.Cancel();
        ShowStatus("Stopping — returning to the best point…");
    }

    /// <summary>Why a scan can't start now, or null.</summary>
    private string? BusyReason()
    {
        if (_cts != null) return "a band scan is already running";
        if (_host == null) return "not connected";
        if (_host.Process.IsRunning) return "a program is running";
        if (_host.Activity.ProcessBusy) return "a process step is running";
        if (_host.Activity.AlignmentBusy) return "another alignment is running";
        return null;
    }

    private void UpdateEnabled()
    {
        bool idle = BusyReason() == null;
        bool leftOk = idle && HasDevice(_settings.LeftDevice);
        bool rightOk = idle && HasDevice(_settings.RightDevice);
        LeftButtons.IsEnabled = leftOk;
        RightButtons.IsEnabled = rightOk;
        TargetALeftCoarse.IsEnabled = TargetBLeftCoarse.IsEnabled = TargetBLeftFine.IsEnabled = leftOk;
        TargetBRightCoarse.IsEnabled = TargetBRightFine.IsEnabled = rightOk;
        TwoLensButton.IsEnabled = idle && HasDevice(_settings.LeftDevice) && HasDevice(_settings.RightDevice);
        StopButton.IsEnabled = _cts != null;
    }

    private bool HasDevice(string device) => _host?.Engine.Motion.GetDevice(device) != null;

    // --- readouts ---

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_polling || _host == null)
            return;
        _polling = true;
        try
        {
            await ShowPoseAsync(_settings.LeftDevice, LeftPose, LeftNote);
            await ShowPoseAsync(_settings.RightDevice, RightPose, RightNote);
            if (BusyReason() == null)
                await ReadSmuAsync();
            Ch1Value.Text = Microamps(_ch1);
            Ch2Value.Text = Microamps(_ch2);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, $"Alignment band readout: {ex.Message}");
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// Shows a lens's live pose in its 3-column grid (X Y Z / U V W), or a note instead (not
    /// configured / not connected / no reading).
    /// </summary>
    private async Task ShowPoseAsync(string device, UniformGrid grid, TextBlock note)
    {
        var (pose, problem) = await ReadPoseAsync(device);
        note.Text = problem ?? string.Empty;
        note.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        grid.Visibility = pose == null ? Visibility.Collapsed : Visibility.Visible;
        if (pose == null)
            return;

        if (grid.Children.Count == 0)
            for (int i = 0; i < 6; i++)
                grid.Children.Add(new TextBlock { Style = (Style)FindResource("PoseCell") });

        double[] values = { pose.X, pose.Y, pose.Z, pose.U, pose.V, pose.W };
        for (int i = 0; i < 6; i++)
            ((TextBlock)grid.Children[i]).Text =
                string.Create(CultureInfo.InvariantCulture, $"{"XYZUVW"[i]} {values[i]:0.0000}");
    }

    private async Task<(MotionPosition? Pose, string? Problem)> ReadPoseAsync(string device)
    {
        var motion = _host!.Engine.Motion;
        if (motion.GetDevice(device) == null) return (null, "not configured");
        if (!motion.IsConnected(device)) return (null, "not connected");
        try
        {
            var r = await motion.GetPositionAsync(device).WaitAsync(ReadTimeout);
            return r.IsSuccess ? (r.Value, null) : (null, "no position");
        }
        catch (TimeoutException)
        {
            return (null, "no position (timeout)");
        }
    }

    /// <summary>Reads both channels directly — only while nothing else uses the SMU.</summary>
    private async Task ReadSmuAsync()
    {
        var smu = _host!.Engine.Smu;
        if (!smu.IsConnected(_settings.Smu))
        {
            _ch1 = _ch2 = double.NaN;
            return;
        }
        _ch1 = await ReadChannelAsync(1);
        _ch2 = await ReadChannelAsync(2);

        async Task<double> ReadChannelAsync(int channel)
        {
            try
            {
                var r = await smu.ReadMeasurementAsync(_settings.Smu, channel).WaitAsync(ReadTimeout);
                return r.IsSuccess ? r.Value.Current : double.NaN;
            }
            catch (TimeoutException)
            {
                return double.NaN;
            }
        }
    }

    /// <summary>SMU stream sample (any thread) — keeps the readout live while a scan streams.</summary>
    private void OnSmuSampled(SmuSample s)
    {
        if (!string.Equals(s.Device, _settings.Smu, StringComparison.OrdinalIgnoreCase))
            return;
        if (s.Channel == 1) _ch1 = s.Current;
        else if (s.Channel == 2) _ch2 = s.Current;
    }

    private static string Microamps(double amps)
        => double.IsFinite(amps)
            ? (amps * 1e6).ToString(Math.Abs(amps) < 1e-5 ? "0.000" : "0.0", CultureInfo.InvariantCulture) + " µA"
            : "—";

    private void ShowStatus(string text) => StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {text}";

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Align", message));
}
