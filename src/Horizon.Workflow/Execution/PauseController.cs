namespace Horizon.Workflow;

/// <summary>
/// Async pause / step gate shared between the <see cref="IProcessService"/>
/// pause controls and the <see cref="WorkflowRunner"/>. Same pause-and-yield
/// idea as <c>flow.askUser</c>: the runner awaits the gate before each step;
/// the front-end decides when to release it.
///
/// One instance lives per run (created by <see cref="ProcessService.RunAsync"/>),
/// so a finished or cancelled run never leaves stale paused state behind.
/// </summary>
internal sealed class PauseController
{
    private readonly object _lock = new();
    private TaskCompletionSource<bool>? _gate;   // non-null while paused
    private bool _stepPending;                   // release one step, then re-pause

    /// <summary>Raised when the paused state changes (true = paused).</summary>
    public event Action<bool>? PausedChanged;

    public bool IsPaused
    {
        get { lock (_lock) return _gate != null; }
    }

    /// <summary>Requests a pause before the runner's next step.</summary>
    public void Pause()
    {
        if (OpenGateOrFalse()) PausedChanged?.Invoke(true);
    }

    /// <summary>Releases a paused run to continue freely.</summary>
    public void Resume() => Release(stepPending: false);

    /// <summary>Releases a paused run for exactly one step, then re-pauses.</summary>
    public void Step() => Release(stepPending: true);

    /// <summary>
    /// Called by the runner before each step. Blocks while paused — whether
    /// from <see cref="Pause"/>, a one-shot <see cref="Step"/>, or a
    /// <paramref name="breakpoint"/> on the upcoming step.
    /// </summary>
    public async Task WaitAsync(bool breakpoint, CancellationToken ct)
    {
        bool nowPaused = false;
        Task? gate;
        lock (_lock)
        {
            // A prior Step() let one step through — re-pause for the next.
            if (_stepPending)
            {
                _stepPending = false;
                _gate = NewGate();
                nowPaused = true;
            }
            else if (breakpoint && _gate == null)
            {
                _gate = NewGate();
                nowPaused = true;
            }
            gate = _gate?.Task;
        }

        if (nowPaused) PausedChanged?.Invoke(true);
        if (gate != null)
            await gate.WaitAsync(ct).ConfigureAwait(false);
    }

    private bool OpenGateOrFalse()
    {
        lock (_lock)
        {
            if (_gate != null) return false;
            _gate = NewGate();
            return true;
        }
    }

    private void Release(bool stepPending)
    {
        TaskCompletionSource<bool>? gate;
        lock (_lock)
        {
            _stepPending = stepPending;
            gate = _gate;
            _gate = null;
        }
        if (gate != null)
        {
            gate.TrySetResult(true);
            PausedChanged?.Invoke(false);
        }
    }

    private static TaskCompletionSource<bool> NewGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
