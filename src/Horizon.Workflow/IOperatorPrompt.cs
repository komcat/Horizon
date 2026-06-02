using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Configuration for a <c>flow.jogPopup</c> manual-jog window: which motion devices /
/// axes the operator may nudge, the initial step size, and the count-up timeout. An
/// empty <see cref="Devices"/> list means "every connected motion device"; the
/// front-end resolves that. <see cref="Axes"/> is the subset of <c>XYZUVW</c> to show.
/// </summary>
public sealed record JogPopupRequest(
    string Title,
    string Message,
    IReadOnlyList<string> Devices,
    string Axes,
    double DefaultStepMm,
    int MaxSeconds);

/// <summary>
/// Layer 3 → Layer 2 callback contract. The runner suspends on these calls and
/// resumes when the front-end answers — the bidirectional half of the process
/// module: a front-end is an active participant the runner can block on, not a
/// passive viewer of <see cref="WorkflowStepEvent"/> / <see cref="LogEntry"/>.
///
/// This is <b>not</b> an engine facet. Engine facets (<c>Motion</c>, <c>IO</c>,
/// …) are called <i>down</i> into; this is an inverted dependency the runner
/// calls <i>up</i> into — Layer 3 provides it, Layer 2 consumes it. The
/// front-end registers an implementation on
/// <see cref="IProcessService.OperatorPrompt"/>.
///
/// Implementations may marshal threads or perform an async round-trip
/// internally (WPF → modal dialog on the dispatcher thread; web → SignalR
/// request keyed by a correlation id) — Layer 2 only ever sees the awaitable
/// result. The console reference implementation is the simplest form.
/// </summary>
public interface IOperatorPrompt
{
    /// <summary>
    /// Blocking yes/no confirmation. Returns <c>true</c> to continue the run,
    /// <c>false</c> for a controlled abort ("operator declined"). Cancellation
    /// of the run arrives via <paramref name="ct"/>.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);

    /// <summary>
    /// Blocking free-text input — the back-end for the <c>dut.promptSerial</c> step.
    /// Returns the entered text, or <c>null</c> when the operator cancels (the
    /// step treats a cancel as a program failure). A run Stop arrives via
    /// <paramref name="ct"/>. The default implementation has no input UI and
    /// always returns <c>null</c>; a Layer 3 head overrides it with a modal dialog.
    /// </summary>
    Task<string?> InputTextAsync(string title, string message, string? defaultValue, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// A real-time countdown the operator can watch (and cancel) — the back-end for
    /// the <c>flow.waitPopup</c> step. Returns <c>true</c> when the full
    /// <paramref name="durationMs"/> elapses, <c>false</c> when the operator cancels
    /// (the step treats a cancel as a program failure). A run Stop arrives via
    /// <paramref name="ct"/>. The default implementation is a plain headless wait
    /// (no window, no cancel button); a Layer 3 head overrides it with a popup.
    /// </summary>
    async Task<bool> CountdownAsync(string title, string message, int durationMs, CancellationToken ct)
    {
        if (durationMs > 0) await Task.Delay(durationMs, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A non-modal, always-on-top manual-jog popup with a count-up timer — the back-end
    /// for the <c>flow.jogPopup</c> step. The operator jogs the configured devices/axes,
    /// then presses Continue (<c>true</c> → the run resumes) or Cancel (<c>false</c> →
    /// the step fails the run); reaching <see cref="JogPopupRequest.MaxSeconds"/> resolves
    /// as Cancel. A run Stop arrives via <paramref name="ct"/>. The default implementation
    /// has no jog UI and immediately continues (<c>true</c>); a Layer 3 head overrides it.
    /// </summary>
    Task<bool> JogPopupAsync(JogPopupRequest request, CancellationToken ct) => Task.FromResult(true);

    // Room to grow without touching IProcessService — add when a step op needs it:
    // Task<int?> ChooseAsync(string title, IReadOnlyList<string> options, CancellationToken ct);
}
