using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FAAM.UI.Process;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

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
    private const string DispensePin = "Dispenser_Shot";    // dispense-trigger output (blank = no pulse, dwell only)
    private const string DispenserSlide = "Dispenser_Head"; // pneumatic: extend = dispense, retract = idle
    private const string UvSlide = "UV_Head";               // pneumatic: extend = UV, retract = idle
    private const int DispenseMs = 800;                     // dispense pulse / dwell
    private const int UvDwellMs = 3000;                     // UV cure dwell per step

    private readonly DispatcherTimer _timer;
    private EngineHost? _host;
    private DateTime _startUtc;
    private bool _running;

    public ProcessStepsPanel()
    {
        InitializeComponent();
        StepList.ItemsSource = DefaultSteps();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimer();

        // Load the button icons; fall back to text if an asset isn't present yet.
        try { TeachIcon.Source = new BitmapImage(new Uri("pack://application:,,,/assets/teach.png")); }
        catch { TeachButton.Content = "Teach"; }
        try { PropertiesIcon.Source = new BitmapImage(new Uri("pack://application:,,,/assets/properties.png")); }
        catch { PropertiesButton.Content = "Props"; }
    }

    /// <summary>Supplies the engine host (for the step sequences, teach, STOP, logging).</summary>
    public void Attach(EngineHost host) => _host = host;

    /// <summary>
    /// The FAU-attach step list — edit here to retarget the process. The third
    /// field is the reusable teach-point name (the key in faam_teach_points.json),
    /// modelled on the acs_solution_saa position names (Home, FAU, PIC, UV…). A
    /// null name means the step does not move (e.g. UV dwell steps); names may be
    /// shared across steps to reuse a taught pose.
    /// </summary>
    private static List<ProcessStep> DefaultSteps() => new()
    {
        new(0, "(1) Gripper open and Move Home", "Home"),
        new(1, "(2) Move to grip FAU and close Gripper", "FAU"),
        new(2, "(3) FAU is gripped and move to SiPhog", "PIC"),
        new(3, "4.0 Move gantry to dispense", "Dispense"),
        new(4, "4.1. Dispense and Move to UV", "UV"),
        new(5, "4.2 — UV step1"),
        new(6, "4.3 — UV step2"),
        new(7, "(5) Complete UV and unload", "Unload"),
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

        // Record the step into the open DUT run for traceability (no-op if none open).
        _host?.Process.RecordDutValue($"step{step.Index}", step.Label);

        Log(LogLevel.Info, $"Run — step: {step.Label}");
        _ = RunStepAsync(step);
    }

    // --- DUT run lifecycle (serial-bracketed, operator-stamped via Layer 2) ---

    private void OnStartDut(object sender, RoutedEventArgs e)
    {
        if (_host == null || _host.Process.HasActiveDutRun)
            return;

        var serial = SerialBox.Text?.Trim() ?? string.Empty;
        var r = _host.Process.BeginDutRun(serial, "FAU attach");
        if (!r.IsSuccess)
        {
            Log(LogLevel.Warning, $"Start DUT failed: {r.Message}");
            return;
        }

        SerialBox.IsEnabled = false;
        StartDutButton.IsEnabled = false;
        PassButton.IsEnabled = true;
        FailButton.IsEnabled = true;
        if (!_running)
            StartTimer();
    }

    private void OnPass(object sender, RoutedEventArgs e) => EndDut(DutVerdict.Pass);

    private void OnFail(object sender, RoutedEventArgs e) => EndDut(DutVerdict.Fail);

    private void EndDut(DutVerdict verdict)
    {
        if (_host == null || !_host.Process.HasActiveDutRun)
            return;

        var r = _host.Process.EndDutRun(verdict);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"DUT '{SerialBox.Text.Trim()}' → {verdict}." : $"End DUT failed: {r.Message}");

        StopTimer();
        SerialBox.IsEnabled = true;
        StartDutButton.IsEnabled = true;
        PassButton.IsEnabled = false;
        FailButton.IsEnabled = false;
        SerialBox.SelectAll();
        SerialBox.Focus();
    }

    private void OnResults(object sender, RoutedEventArgs e)
    {
        if (_host == null)
            return;
        new DutResultsWindow(_host) { Owner = Window.GetWindow(this) }.Show();
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

    /// <summary>
    /// Raised when a step's "Inspect Motion" context-menu item is chosen. Carries
    /// one row per distinct process position; the host view (RunView) renders it as
    /// a grid over the camera column.
    /// </summary>
    public event EventHandler<IReadOnlyList<MotionInspectRow>>? InspectMotionRequested;

    /// <summary>
    /// Builds the motion-inspection table for every distinct position used by the
    /// process (in step order) from the Layer 2 product: name, device, the
    /// position's stored velocity, its taught X/Y/Z/U/V/W ("—" when not taught),
    /// and its camera / exposure. Each row carries an <see cref="MotionInspectRow.Edit"/>
    /// action that opens the per-position editor.
    /// </summary>
    private void OnInspectMotion(object sender, RoutedEventArgs e)
    {
        if (_host == null)
            return;

        var rows = new List<MotionInspectRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in StepList.ItemsSource?.OfType<ProcessStep>() ?? Enumerable.Empty<ProcessStep>())
        {
            if (string.IsNullOrEmpty(step.Position) || !seen.Add(step.Position))
                continue;   // skip dwell-only steps and positions already listed

            var position = step.Position;
            var np = _host.Process.GetPosition(TeachDevice, position);
            var c = np?.Coordinates;
            rows.Add(new MotionInspectRow(
                position, TeachDevice, Fmt(np?.Velocity),
                Fmt(c?.X), Fmt(c?.Y), Fmt(c?.Z), Fmt(c?.U), Fmt(c?.V), Fmt(c?.W),
                np?.Camera ?? "—", Fmt(np?.ExposureUs))
            {
                Edit = () => OpenPositionEditor(position, np),
            });
        }

        InspectMotionRequested?.Invoke(this, rows);
    }

    /// <summary>Opens the non-modal per-position editor; on Apply the position is saved to the product via Layer 2.</summary>
    private void OpenPositionEditor(string position, NamedPosition? np)
    {
        if (_host == null)
            return;

        var info = _host.Engine.Motion.GetDevice(TeachDevice);
        if (info == null)
            return;

        var ctx = new PositionEditContext
        {
            Position = position,
            Device = info.Name,
            ControllerType = info.ControllerType,
            Axes = info.Axes,
            Velocity = np?.Velocity ?? 0,
            Pose = np?.Coordinates ?? MotionPosition.Zero,
            Cameras = _host.Engine.Camera.CameraIds,
            Camera = np?.Camera,
            ExposureUs = np?.ExposureUs,
        };

        new PositionEditorWindow(ctx, ApplyPositionEditAsync)
        {
            Owner = Window.GetWindow(this),
        }.Show();
    }

    /// <summary>Persists an edited position (coords + velocity + camera/exposure) to the product, then refreshes the grid.</summary>
    private Task ApplyPositionEditAsync(PositionEditResult result)
    {
        var r = _host!.Process.SetPositionFull(
            result.Device, result.Position, result.Pose, result.Velocity, result.Camera, result.ExposureUs);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess
                ? $"Position '{result.Position}' saved (velocity {result.Velocity:0.###})."
                : $"Save '{result.Position}' failed: {r.Message}");

        OnInspectMotion(this, new RoutedEventArgs());   // rebuild rows with the new values
        return Task.CompletedTask;
    }

    private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.###") : "—";

    /// <summary>Captures the current gantry pose under the step's reusable position name (Teach mode) and persists it.</summary>
    private async Task TeachStepAsync(ProcessStep step)
    {
        if (_host == null)
            return;

        if (string.IsNullOrEmpty(step.Position))
        {
            Log(LogLevel.Warning, $"'{step.Label}' has no teachable position (it does not move).");
            return;
        }

        // Teach through Layer 2 — captures the device's current pose under the
        // position name in the active product, preserving its existing velocity
        // and camera / exposure settings.
        var velocity = _host.Process.GetPosition(TeachDevice, step.Position)?.Velocity ?? 0;
        var r = await _host.Process.TeachPositionAsync(TeachDevice, step.Position, velocity);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess
                ? $"Taught '{step.Position}' @ {TeachDevice}."
                : $"Teach '{step.Position}' failed: {r.Message}");
    }

    private async Task GripperAsync(bool close)
    {
        var r = await _host!.Engine.IO.SetOutputByNameAsync(IoDevice, GripperPin, close);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            $"Gripper {(close ? "close" : "open")} ({GripperPin}): {(r.IsSuccess ? "ok" : r.Message)}");
    }

    private async Task MoveToTaughtAsync(ProcessStep step)
    {
        if (string.IsNullOrEmpty(step.Position))
            return;                                       // step has no move

        // Layer 2 runs the recall routine: set velocity → start move → set camera
        // exposure (while moving) → settle. Returns Fail for an untaught position.
        var r = await _host!.Process.MoveToNamedPositionAsync(TeachDevice, step.Position);
        if (!r.IsSuccess)
            Log(LogLevel.Warning,
                $"Move → '{step.Position}' ('{step.Label}') failed: {r.Message}. " +
                "If untaught: arm Teach, jog to the spot, then click the step.");
    }

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

/// <summary>
/// One process step — its fixed index (drives the hardcoded sequence), display
/// label, and an optional reusable teach-point name (<c>Position</c>) used as the
/// key in faam_teach_points.json. Null <c>Position</c> = the step does not move.
/// </summary>
public sealed record ProcessStep(int Index, string Label, string? Position = null);

/// <summary>One row of the motion inspector — a process position and its recall settings (values pre-formatted for display).</summary>
public sealed record MotionInspectRow(
    string Position, string Device, string Velocity,
    string X, string Y, string Z, string U, string V, string W,
    string Camera, string Exposure)
{
    /// <summary>Opens the per-position editor for this row (wired by the inspector builder).</summary>
    public Action? Edit { get; init; }
}
