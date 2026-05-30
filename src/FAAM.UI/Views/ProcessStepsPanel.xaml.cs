using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// The Run-page process panel — operator-clicked FAU-attach steps, a process
/// timer, a STOP (halts all motion), and a Teach toggle. With Teach ON, clicking
/// a step records the gantry's current pose for that step (persisted to
/// faam_teach_points.json); with Teach OFF, clicking runs the step's hardcoded
/// sequence (Phase 1 — see <see cref="RunStepAsync"/>).
///
/// Device/pin mapping is hardcoded in the constants below — edit to match the rig.
/// </summary>
public partial class ProcessStepsPanel : UserControl
{
    // --- Device / pin mapping (edit to match the rig) ---
    private const string TeachDevice = "gantry-main";       // motion device taught + replayed
    private const string IoDevice = "IOBottom";             // IO module carrying the outputs
    private const string GripperPin = "R_Gripper";          // on = close, off = open
    private const string DispensePin = "";                  // dispense-trigger output (blank = no pulse, dwell only) — SET to enable
    private const string DispenserSlide = "Dispenser_Head"; // pneumatic: extend = dispense, retract = idle
    private const string UvSlide = "UV_Head";               // pneumatic: extend = UV, retract = idle
    private const int DispenseMs = 800;                     // dispense pulse / dwell
    private const int UvDwellMs = 3000;                     // UV cure dwell per step

    private const string TeachFile = "faam_teach_points";

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, MotionPosition> _taught = new();
    private EngineHost? _host;
    private DateTime _startUtc;
    private bool _running;

    public ProcessStepsPanel()
    {
        InitializeComponent();
        StepList.ItemsSource = DefaultSteps();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimer();

        // Load any previously-taught points.
        var loaded = ConfigLoader.LoadJson<Dictionary<string, MotionPosition>>(
            ConfigLoader.GetConfigDirectory(), TeachFile);
        if (loaded != null)
            foreach (var kv in loaded)
                _taught[kv.Key] = kv.Value;

        // Load the button icons; fall back to text if an asset isn't present yet.
        try { TeachIcon.Source = new BitmapImage(new Uri("pack://application:,,,/assets/teach.png")); }
        catch { TeachButton.Content = "Teach"; }
        try { PropertiesIcon.Source = new BitmapImage(new Uri("pack://application:,,,/assets/properties.png")); }
        catch { PropertiesButton.Content = "Props"; }
    }

    /// <summary>Supplies the engine host (for the step sequences, teach, STOP, logging).</summary>
    public void Attach(EngineHost host) => _host = host;

    /// <summary>The FAU-attach step list — edit here to retarget the process.</summary>
    private static List<ProcessStep> DefaultSteps() => new()
    {
        new(0, "(1) Gripper open and Move Home"),
        new(1, "(2) Move to grip FAU and close Gripper"),
        new(2, "(3) FAU is gripped and move to SiPhog"),
        new(3, "4.0 Move gantry to dispense"),
        new(4, "4.1. Dispense and Move to UV"),
        new(5, "4.2 — UV step1"),
        new(6, "4.3 — UV step2"),
        new(7, "(5) Complete UV and unload"),
    };

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProcessStep step)
            return;

        // Teach mode (toggle ON) records the position; Run mode (OFF) executes the step.
        if (TeachButton.IsChecked == true)
        {
            _ = TeachStepAsync(step);
            return;
        }

        if (!_running)
            StartTimer();
        Log(LogLevel.Info, $"Run — step: {step.Label}");
        _ = RunStepAsync(step);
    }

    /// <summary>
    /// Phase 1 — runs a step's hardcoded sequence on the engine facets. Moves
    /// replay the step's taught pose; gripper is a raw IO output; the dispenser /
    /// UV heads are pneumatic slides; dispense is an optional IO pulse.
    /// </summary>
    private async Task RunStepAsync(ProcessStep step)
    {
        if (_host == null)
            return;

        var pneu = _host.Engine.Pneumatic;
        try
        {
            switch (step.Index)
            {
                case 0:                                   // Gripper open + Move Home
                    await GripperAsync(close: false);
                    await MoveToTaughtAsync(step);
                    break;
                case 1:                                   // Move to grip + close gripper
                    await MoveToTaughtAsync(step);
                    await GripperAsync(close: true);
                    break;
                case 2:                                   // Move to SiPhog
                case 3:                                   // Move to dispense
                    await MoveToTaughtAsync(step);
                    break;
                case 4:                                   // Dispense + move to UV
                    await PneuAsync(pneu.ExtendAsync(DispenserSlide), $"{DispenserSlide} extend");
                    await DispensePulseAsync();
                    await PneuAsync(pneu.RetractAsync(DispenserSlide), $"{DispenserSlide} retract");
                    await MoveToTaughtAsync(step);
                    break;
                case 5:                                   // UV step 1 — head over part, cure
                    await PneuAsync(pneu.ExtendAsync(UvSlide), $"{UvSlide} extend");
                    await DwellAsync(UvDwellMs, "UV step1 cure");
                    break;
                case 6:                                   // UV step 2 — continue cure
                    await DwellAsync(UvDwellMs, "UV step2 cure");
                    break;
                case 7:                                   // Complete UV + unload
                    await PneuAsync(pneu.RetractAsync(UvSlide), $"{UvSlide} retract");
                    await MoveToTaughtAsync(step);
                    await GripperAsync(close: false);     // release
                    break;
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Step '{step.Label}' error: {ex.Message}");
        }
    }

    // TODO (Properties editor): per-step NON-motion settings — IO pin names,
    // inbound/outbound (debounce) delays, wait/dwell times, camera exposure, etc.
    // This button is a placeholder; wire the editor here later.
    private void OnProperties(object sender, RoutedEventArgs e)
        => Log(LogLevel.Info, "Step properties editor — not implemented yet (IO names, debounce/wait delays, camera exposure…).");

    /// <summary>Captures the current gantry pose for a step (Teach mode) and persists it.</summary>
    private async Task TeachStepAsync(ProcessStep step)
    {
        if (_host == null)
            return;

        var pos = await _host.Engine.Motion.GetPositionAsync(TeachDevice);
        if (!pos.IsSuccess)
        {
            Log(LogLevel.Warning, $"Teach — position read failed ({TeachDevice}): {pos.Message}");
            return;
        }

        _taught[step.Label] = pos.Value;
        ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), TeachFile, _taught);

        var p = pos.Value;
        Log(LogLevel.Info, $"Taught '{step.Label}' @ {TeachDevice}: X={p.X:F3} Y={p.Y:F3} Z={p.Z:F3}");
    }

    private async Task GripperAsync(bool close)
    {
        var r = await _host!.Engine.IO.SetOutputByNameAsync(IoDevice, GripperPin, close);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            $"Gripper {(close ? "close" : "open")} ({GripperPin}): {(r.IsSuccess ? "ok" : r.Message)}");
    }

    private async Task MoveToTaughtAsync(ProcessStep step)
    {
        // TODO (guided teach): when a step has no taught pose — or a suspicious
        // (0,0,0) one — the planned UX is to prompt the operator to jog there
        // manually, save, then advance to the next step. For now we warn + skip.
        if (!_taught.TryGetValue(step.Label, out var pos) || IsSuspectZero(pos))
        {
            Log(LogLevel.Warning,
                $"'{step.Label}' has no valid taught position — arm Teach, jog to the spot, then click the step. Skipping move.");
            return;
        }
        var r = await _host!.Engine.Motion.MoveToPositionAsync(TeachDevice, pos);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            $"Move {TeachDevice} → '{step.Label}': {(r.IsSuccess ? "ok" : r.Message)}");
    }

    /// <summary>A taught pose at the origin is almost certainly un-taught — treat as invalid.</summary>
    private static bool IsSuspectZero(MotionPosition p)
        => Math.Abs(p.X) < 1e-6 && Math.Abs(p.Y) < 1e-6 && Math.Abs(p.Z) < 1e-6;

    private async Task DispensePulseAsync()
    {
        if (string.IsNullOrWhiteSpace(DispensePin))
        {
            await DwellAsync(DispenseMs, "dispense dwell (no trigger pin set)");
            return;
        }
        await _host!.Engine.IO.SetOutputByNameAsync(IoDevice, DispensePin, true);
        await Task.Delay(DispenseMs);
        await _host!.Engine.IO.SetOutputByNameAsync(IoDevice, DispensePin, false);
        Log(LogLevel.Info, $"Dispense pulse ({DispensePin}, {DispenseMs} ms)");
    }

    private async Task PneuAsync(Task<Result> op, string what)
    {
        var r = await op;
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning, $"{what}: {(r.IsSuccess ? "ok" : r.Message)}");
    }

    private async Task DwellAsync(int ms, string what)
    {
        Log(LogLevel.Info, $"{what} ({ms} ms)…");
        await Task.Delay(ms);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        StopTimer();
        Log(LogLevel.Warning, "STOP pressed — halting motion.");

        if (_host != null)
        {
            foreach (var device in _host.Engine.Motion.DeviceNames)
            {
                try { await _host.Engine.Motion.StopAsync(device); }
                catch (Exception ex) { Log(LogLevel.Error, $"Stop {device} failed: {ex.Message}"); }
            }
        }
    }

    private void StartTimer()
    {
        _startUtc = DateTime.UtcNow;
        _running = true;
        _timer.Start();
        UpdateTimer();
    }

    private void StopTimer()
    {
        _running = false;
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        var elapsed = DateTime.UtcNow - _startUtc;
        TimerText.Text = $"Process: {elapsed:hh\\:mm\\:ss}";
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Process", message));
}

/// <summary>One process step — its fixed index (drives the hardcoded sequence) and display label.</summary>
public sealed record ProcessStep(int Index, string Label);
