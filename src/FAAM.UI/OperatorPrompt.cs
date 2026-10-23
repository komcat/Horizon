using System.Windows;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// WPF implementation of the Layer 2 operator-prompt contract. A workflow
/// <c>flow.askUser</c> step blocks on <see cref="ConfirmAsync"/> until the
/// operator answers a modal <see cref="OperatorPromptWindow"/>. Registered on
/// <see cref="IProcessService.OperatorPrompt"/> by the shell.
/// </summary>
public sealed class OperatorPrompt : IOperatorPrompt
{
    private readonly EngineHost _host;

    /// <summary>The <paramref name="host"/> backs the manual-jog popup (<c>flow.jogPopup</c>).</summary>
    public OperatorPrompt(EngineHost host) => _host = host;

    /// <summary>Marks the operator as awaited (tower light blinks yellow) until a non-modal prompt's task ends.</summary>
    private void HoldAttentionUntil(Task prompt)
    {
        var attention = _host.Attention.Begin();
        _ = prompt.ContinueWith(_ => attention.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.FromResult(false);

        // The runner calls in on a background thread — show the dialog on the UI thread.
        return dispatcher.InvokeAsync(() => Show(title, message, ct)).Task;
    }

    private bool Show(string title, string message, CancellationToken ct)
    {
        using var attention = _host.Attention.Begin();   // tower light: waiting on the operator
        var window = new OperatorPromptWindow(title, message)
        {
            Owner = Application.Current.MainWindow,
        };

        // A run cancellation closes the dialog (treated as a decline).
        using var registration = ct.Register(() =>
            window.Dispatcher.BeginInvoke(() => { if (window.IsLoaded) window.Close(); }));

        var confirmed = window.ShowDialog();
        ct.ThrowIfCancellationRequested();   // surfaces a Stop as a cancellation
        return confirmed == true;
    }

    /// <summary>
    /// Modal free-text entry — the <c>dut.promptSerial</c> step blocks here until the
    /// operator enters a serial (returned) or cancels (null → the step fails the run).
    /// </summary>
    public Task<string?> InputTextAsync(string title, string message, string? defaultValue, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.FromResult<string?>(null);

        return dispatcher.InvokeAsync(() => ShowInput(title, message, defaultValue, ct)).Task;
    }

    private string? ShowInput(string title, string message, string? defaultValue, CancellationToken ct)
    {
        using var attention = _host.Attention.Begin();   // tower light: waiting on the operator
        var window = new SerialEntryWindow(title, message, defaultValue)
        {
            Owner = Application.Current.MainWindow,
        };

        using var registration = ct.Register(() =>
            window.Dispatcher.BeginInvoke(() => { if (window.IsLoaded) window.Close(); }));

        var ok = window.ShowDialog();
        ct.ThrowIfCancellationRequested();   // surfaces a Stop as a cancellation
        return ok == true ? window.Serial : null;
    }

    /// <summary>
    /// Non-modal, always-on-top countdown — the <c>flow.waitPopup</c> step blocks on
    /// the returned task until the timer elapses (true) or the operator presses Cancel
    /// (false → the step fails the run). A run Stop closes the window and surfaces as
    /// a cancellation.
    /// </summary>
    public Task<bool> CountdownAsync(string title, string message, int durationMs, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.FromResult(true);

        // InvokeAsync(...).Task is Task<Task<bool>> — unwrap the inner countdown task.
        return dispatcher.InvokeAsync(() => ShowCountdown(title, message, durationMs, ct)).Task.Unwrap();
    }

    private static Task<bool> ShowCountdown(string title, string message, int durationMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new CountdownWindow(title, message, durationMs)
        {
            Owner = Application.Current.MainWindow,
        };

        // A run Stop closes the window and surfaces as a cancellation to the runner.
        var registration = ct.Register(() =>
            window.Dispatcher.BeginInvoke(() =>
            {
                tcs.TrySetCanceled(ct);
                if (window.IsLoaded) window.Close();
            }));

        // Finished(true) = elapsed, Finished(false) = operator cancelled or closed.
        window.Finished += completed =>
        {
            registration.Dispose();
            tcs.TrySetResult(completed);
            if (window.IsLoaded) window.Close();
        };

        window.Show();
        return tcs.Task;
    }

    /// <summary>
    /// Non-modal, always-on-top yellow alert — the <c>io.alertBuzzer</c> step sounds its
    /// buzzer until the returned task completes (operator pressed Acknowledge). A run Stop
    /// closes the window and surfaces as a cancellation.
    /// </summary>
    public Task AcknowledgeAsync(string title, string message, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.CompletedTask;

        // InvokeAsync(...).Task is Task<Task> — unwrap the inner acknowledge task.
        return dispatcher.InvokeAsync(() => ShowAlert(title, message, ct)).Task.Unwrap();
    }

    private Task ShowAlert(string title, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldAttentionUntil(tcs.Task);
        var window = new AlertWindow(title, message)
        {
            Owner = Application.Current.MainWindow,
        };

        // A run Stop closes the window and surfaces as a cancellation to the runner.
        var registration = ct.Register(() =>
            window.Dispatcher.BeginInvoke(() =>
            {
                tcs.TrySetCanceled(ct);
                if (window.IsLoaded) window.Close();
            }));

        window.Acknowledged += () =>
        {
            registration.Dispose();
            tcs.TrySetResult();
            if (window.IsLoaded) window.Close();
        };

        window.Show();
        return tcs.Task;
    }

    /// <summary>
    /// Non-modal, always-on-top big-STOP window shown while <c>motion.slowApproach</c> crawls.
    /// Completes <c>true</c> when the operator presses STOP; the runner cancels
    /// <paramref name="ct"/> when the move ends, which closes the window → <c>false</c>.
    /// </summary>
    public Task<bool> MotionStopAsync(string title, string message, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.FromResult(false);

        // InvokeAsync(...).Task is Task<Task<bool>> — unwrap the inner STOP task.
        return dispatcher.InvokeAsync(() => ShowMotionStop(title, message, ct)).Task.Unwrap();
    }

    private static Task<bool> ShowMotionStop(string title, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetResult(false);   // the move already ended — nothing to show
            return tcs.Task;
        }

        var window = new MotionStopWindow(title, message)
        {
            Owner = Application.Current.MainWindow,
        };

        // Move ended (or run Stop) — close the window; Finished(false) resolves the task.
        var registration = ct.Register(() =>
            window.Dispatcher.BeginInvoke(() =>
            {
                tcs.TrySetResult(false);
                if (window.IsLoaded) window.Close();
            }));

        window.Finished += stop =>
        {
            registration.Dispose();
            tcs.TrySetResult(stop);
            if (window.IsLoaded) window.Close();
        };

        window.Show();
        return tcs.Task;
    }

    /// <summary>
    /// Non-modal, always-on-top manual-jog popup — the <c>flow.jogPopup</c> step blocks
    /// on the returned task until the operator presses Continue (true) or Cancel/timeout
    /// (false → the step fails the run). A run Stop closes the window and surfaces as a
    /// cancellation.
    /// </summary>
    public Task<bool> JogPopupAsync(JogPopupRequest request, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.FromResult(true);

        // InvokeAsync(...).Task is Task<Task<bool>> — unwrap the inner jog task.
        return dispatcher.InvokeAsync(() => ShowJogPopup(request, ct)).Task.Unwrap();
    }

    private Task<bool> ShowJogPopup(JogPopupRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldAttentionUntil(tcs.Task);
        var window = new JogPopupWindow(request, _host)
        {
            Owner = Application.Current.MainWindow,
        };

        // A run Stop closes the window and surfaces as a cancellation to the runner.
        var registration = ct.Register(() =>
            window.Dispatcher.BeginInvoke(() =>
            {
                tcs.TrySetCanceled(ct);
                if (window.IsLoaded) window.Close();
            }));

        // Finished(true) = Continue, Finished(false) = Cancel / timeout / closed.
        window.Finished += carryOn =>
        {
            registration.Dispose();
            tcs.TrySetResult(carryOn);
            if (window.IsLoaded) window.Close();
        };

        window.Show();
        return tcs.Task;
    }
}
