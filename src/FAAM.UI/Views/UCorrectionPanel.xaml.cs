using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.UCorrection;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page U-correction panel — replicates the legacy acs_solution_saa U-correction
/// UI (Move-to / Save-to P1·P2·F1·F2, Start / Capture / Clear / Apply U) but drives
/// it through the Layer 2 <see cref="IProcessService.RunUCorrectionAsync(UCorrectionOptions,IOperatorPrompt,System.Threading.CancellationToken)"/>
/// routine.
///
/// The panel <b>is</b> the routine's <see cref="IOperatorPrompt"/>: the routine moves
/// the gantry to each reference point and then blocks on <see cref="ConfirmAsync"/>;
/// the inline <c>Capture Point</c> button completes that wait (so the operator can keep
/// jogging via the live camera meanwhile — no blocking modal), and <c>Apply U</c>
/// completes the final apply confirmation. Save/Move use Layer 2 teach / recall.
/// </summary>
public partial class UCorrectionPanel : UserControl, IOperatorPrompt
{
    // FAAM rig device mapping (mirrors ProcessStepsPanel).
    private const string GantryDevice = "gantry-main";   // ACS gantry (X/Y/Z) — the four reference points live here
    private const string HexDevice = "hex-right";        // PI hexapod — the U axis is corrected here

    // The routine titles its confirmations; the apply gate is the one we route to "Apply U".
    private const string ApplyTitle = "Apply U correction";

    // Capture order P1 → P2 → F1 → F2 (PIC line then FAU line), matching UCorrectionOptions.
    private static readonly string[] Order = { "P1", "P2", "F1", "F2" };

    private EngineHost? _host;
    private Ellipse[] _dots = System.Array.Empty<Ellipse>();

    private CancellationTokenSource? _cts;
    private IDisposable? _activity;                 // process-busy token held for the run
    private TaskCompletionSource<bool>? _pending;   // the in-flight ConfirmAsync, if any
    private bool _awaitingApply;
    private int _captureIndex;
    private bool _running;

    public UCorrectionPanel()
    {
        InitializeComponent();
        _dots = new[] { DotP1, DotP2, DotF1, DotF2 };
    }

    /// <summary>Supplies the engine host (Layer 2 teach / recall / correction run + logging).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        host.Activity.Changed += OnActivityChanged;   // block Start/Move while a process step or other run holds the hexapod
        RefreshButtons();
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

    /// <summary>Start is available only when nothing else (a process step or another alignment) holds the hexapod.</summary>
    private void RefreshStartEnabled()
    {
        if (_host == null) return;
        bool othersBusy = _host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !_running);
        StartButton.IsEnabled = !_running && !othersBusy;
    }

    private static string PositionName(string point) => $"UCorr_{point}";

    // --- Reference points: teach (Save) + recall (Move), via Layer 2 ---

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = PositionName(point);
        var velocity = _host.Process.GetPosition(GantryDevice, name)?.Velocity ?? 0;
        var r = await _host.Process.TeachPositionAsync(GantryDevice, name, velocity);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"U correction: taught {point} ({name})."
                        : $"U correction: teach {point} failed — {r.Message}");
    }

    private async void OnMove(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement { Tag: string point }) return;

        var name = PositionName(point);
        var r = await _host.Process.MoveToNamedPositionAsync(GantryDevice, name);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"U correction: moved to {point}."
                        : $"U correction: move to {point} failed — {r.Message} (Save it first).");
    }

    private void OnLockChanged(object sender, RoutedEventArgs e) => RefreshReferenceButtons();

    // --- Correction run ---

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || _running) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            StatusText.Text = "Busy — wait for the current run";
            Log(LogLevel.Warning, "U correction blocked: a process step or alignment is running.");
            return;
        }

        // All four reference points must be taught before the run can navigate them.
        var missing = Order.Where(p => _host.Process.GetPosition(GantryDevice, PositionName(p)) == null).ToList();
        if (missing.Count > 0)
        {
            StatusText.Text = $"Save these first: {string.Join(", ", missing)}";
            Log(LogLevel.Warning, $"U correction: cannot start — untaught points: {string.Join(", ", missing)}.");
            return;
        }

        _running = true;
        _captureIndex = 0;
        ResetDots();
        RefreshReferenceButtons();
        StartButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        _activity = _host.Activity.Begin(ActivityKind.Alignment, "U correction");   // block process buttons during the run

        var options = new UCorrectionOptions(
            GantryDevice, HexDevice,
            PositionName("P1"), PositionName("P2"), PositionName("F1"), PositionName("F2"));

        try
        {
            var r = await _host.Process.RunUCorrectionAsync(options, this, _cts.Token);
            StatusText.Text = r.IsSuccess
                ? (r.Value.Applied
                    ? $"Applied U = {r.Value.CorrectionDegrees:F4}° (angle {r.Value.AngleDegrees:F4}°)"
                    : $"Declined — angle {r.Value.AngleDegrees:F4}°")
                : r.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            Log(LogLevel.Error, $"U correction run error: {ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Capture the current point — completes the routine's per-point <see cref="ConfirmAsync"/>.</summary>
    private void OnCapture(object sender, RoutedEventArgs e)
    {
        var pending = _pending;
        if (pending == null || _awaitingApply) return;

        _pending = null;
        CaptureButton.IsEnabled = false;
        SetDot(_captureIndex, captured: true);
        _captureIndex++;
        StatusText.Text = "Capturing…";
        pending.TrySetResult(true);   // routine reads the live pose, then advances
    }

    /// <summary>Apply the correction — completes the routine's final apply <see cref="ConfirmAsync"/>.</summary>
    private void OnApplyU(object sender, RoutedEventArgs e)
    {
        var pending = _pending;
        if (pending == null || !_awaitingApply) return;

        _pending = null;
        _awaitingApply = false;
        ApplyButton.IsEnabled = false;
        StatusText.Text = "Applying U…";
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
            StatusText.Text = "Idle";
        }
    }

    // --- IOperatorPrompt (inline, non-modal) — the routine blocks here per point + on apply ---

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetResult(false));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        // The routine awaits us from a background thread — set up the UI on the dispatcher.
        Dispatcher.Invoke(() =>
        {
            _pending = tcs;
            _awaitingApply = title.Contains("Apply", System.StringComparison.OrdinalIgnoreCase);

            if (_awaitingApply)
            {
                StatusText.Text = message.Replace("\n", " ");
                ApplyButton.IsEnabled = true;
                CaptureButton.IsEnabled = false;
            }
            else
            {
                StatusText.Text = message;
                SetDot(_captureIndex, captured: false);   // highlight the point now being refined
                CaptureButton.IsEnabled = true;
            }
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
        foreach (var b in new[] { MoveP1, MoveP2, MoveF1, MoveF2, SaveP1, SaveP2, SaveF1, SaveF2 })
            b.IsEnabled = enabled;
    }

    private void Finish()
    {
        _running = false;
        _awaitingApply = false;
        _pending = null;
        _activity?.Dispose();
        _activity = null;
        _cts?.Dispose();
        _cts = null;
        CaptureButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        RefreshButtons();
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, System.DateTimeOffset.Now, "UCorr", message));
}
