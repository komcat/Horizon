using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page operator action: dispense a saved routine at the current camera target. The
/// operator points the camera (MoveClick / jog) at the spot, picks a routine, and hits
/// Dispense — Layer 2 uses the camera→needle calibration to place the needle and lay the
/// pattern down. Holds the <see cref="ActivityKind.Alignment"/> gate for the run (exclusive
/// with the process steps + other alignment panels, since it drives the shared gantry).
/// </summary>
public partial class DispensePanel : UserControl
{
    private EngineHost? _host;
    private CancellationTokenSource? _cts;
    private IDisposable? _activity;

    public DispensePanel()
    {
        InitializeComponent();
    }

    /// <summary>Wire to the engine host (called from the Run view).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        host.Process.ProductChanged += _ => Dispatcher.Invoke(RefreshRoutines);
        host.Activity.Changed += () => Dispatcher.Invoke(UpdateButtons);
        RefreshRoutines();
        UpdateButtons();
    }

    private void RefreshRoutines()
    {
        if (_host == null) return;
        var keep = RoutineCombo.SelectedItem as string;
        RoutineCombo.ItemsSource = _host.Process.ListDispenseRoutines();
        if (keep != null) RoutineCombo.SelectedItem = keep;
    }

    private void OnDropDownOpened(object sender, EventArgs e) => RefreshRoutines();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        bool running = _cts != null;
        bool gateBusy = _host?.Activity is { } a && (a.ProcessBusy || a.AlignmentBusy);
        DispenseButton.IsEnabled = !running && !gateBusy && RoutineCombo.SelectedItem is string;
        CancelButton.IsEnabled = running;
        ToNeedleButton.IsEnabled = !running && !gateBusy;
        ToCameraButton.IsEnabled = !running && !gateBusy;
    }

    private async void OnToNeedle(object sender, System.Windows.RoutedEventArgs e) => await JogOffsetAsync(toNeedle: true);

    private async void OnToCamera(object sender, System.Windows.RoutedEventArgs e) => await JogOffsetAsync(toNeedle: false);

    private async System.Threading.Tasks.Task JogOffsetAsync(bool toNeedle)
    {
        if (_host == null || _cts != null) return;
        if (_host.Activity is { } a && (a.ProcessBusy || a.AlignmentBusy)) return;

        _cts = new CancellationTokenSource();
        _activity = _host.Activity.Begin(ActivityKind.Alignment, toNeedle ? "Move to needle" : "Move to camera");
        UpdateButtons();
        SetStatus(toNeedle ? "Moving needle to camera target…" : "Returning camera…");
        try
        {
            var r = await _host.Process.JogCameraNeedleOffsetAsync(toNeedle, _cts.Token);
            SetStatus(r.IsSuccess ? (toNeedle ? "Needle over the camera target." : "Camera returned over the point.")
                                  : r.Message, warn: !r.IsSuccess);
            if (!r.IsSuccess) Log(LogLevel.Error, $"Move to {(toNeedle ? "needle" : "camera")} failed: {r.Message}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, warn: true);
        }
        finally
        {
            _activity?.Dispose();
            _activity = null;
            _cts?.Dispose();
            _cts = null;
            UpdateButtons();
        }
    }

    private async void OnDispense(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_host == null || _cts != null) return;
        if (RoutineCombo.SelectedItem is not string name)
        {
            SetStatus("Pick a routine first.", warn: true);
            return;
        }

        _cts = new CancellationTokenSource();
        _activity = _host.Activity.Begin(ActivityKind.Alignment, $"Dispense ({name})");
        UpdateButtons();
        SetStatus($"Dispensing '{name}'…");
        try
        {
            var r = await _host.Process.RunDispenseRoutineAsync(name, _cts.Token);
            SetStatus(r.IsSuccess ? $"Dispense '{name}' complete." : r.Message, warn: !r.IsSuccess);
            Log(r.IsSuccess ? LogLevel.Info : LogLevel.Error,
                r.IsSuccess ? $"Dispense '{name}' complete." : $"Dispense '{name}' failed: {r.Message}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, warn: true);
            Log(LogLevel.Error, $"Dispense '{name}' error: {ex.Message}");
        }
        finally
        {
            _activity?.Dispose();
            _activity = null;
            _cts?.Dispose();
            _cts = null;
            UpdateButtons();
        }
    }

    private void OnCancel(object sender, System.Windows.RoutedEventArgs e) => _cts?.Cancel();

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Dispense", message));

    private void SetStatus(string text, bool warn = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(warn ? "Danger" : "Subtle");
    }
}
