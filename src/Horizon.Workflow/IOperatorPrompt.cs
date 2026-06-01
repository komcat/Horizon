using Horizon.Engine;

namespace Horizon.Workflow;

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

    // Room to grow without touching IProcessService — add when a step op needs it:
    // Task<int?> ChooseAsync(string title, IReadOnlyList<string> options, CancellationToken ct);
}
