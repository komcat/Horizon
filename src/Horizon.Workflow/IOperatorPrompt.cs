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

    // Room to grow without touching IProcessService — add when a step op needs it:
    // Task<string?> InputTextAsync(string title, string message, CancellationToken ct);
    // Task<int?>    ChooseAsync(string title, IReadOnlyList<string> options, CancellationToken ct);
}
