using System.Threading;

namespace FAAM.UI;

/// <summary>Which kind of exclusive work is running — the two sides of the interlock.</summary>
public enum ActivityKind
{
    /// <summary>An automated FAU-attach process step sequence (drives the gantry/hexapod).</summary>
    Process,
    /// <summary>A manual alignment / scan run (Hill Climber, U-correction, FAU pre-align).</summary>
    Alignment,
}

/// <summary>
/// A process-wide mutual-exclusion signal between the automated process steps and
/// the manual alignment/scan runs — they all drive the shared hexapod, so the two
/// sides must not run at once. Each side brackets its run with <see cref="Begin"/>
/// (tagged <see cref="ActivityKind"/>); the process panel blocks its step / DUT-start
/// buttons while an <see cref="ActivityKind.Alignment"/> run holds the gate, and the
/// alignment panels block their Start while an <see cref="ActivityKind.Process"/> run
/// (or another alignment) holds it. Lives on <see cref="EngineHost"/> so any Layer-3
/// panel can reach it without knowing about the others.
///
/// Re-entrant and disposal-safe. <see cref="Changed"/> fires on every transition and
/// may fire on any thread — subscribers must marshal to the UI and recompute their
/// own enabled state from <see cref="ProcessBusy"/> / <see cref="AlignmentBusy"/>.
/// </summary>
public sealed class ActivityGate
{
    private readonly object _lock = new();
    private int _process;
    private int _alignment;

    /// <summary>True while an automated process step sequence is executing.</summary>
    public bool ProcessBusy { get { lock (_lock) return _process > 0; } }

    /// <summary>True while a manual alignment / scan run is executing.</summary>
    public bool AlignmentBusy { get { lock (_lock) return _alignment > 0; } }

    /// <summary>Raised on every busy transition (either side). May fire on any thread.</summary>
    public event Action? Changed;

    /// <summary>Marks an operation of <paramref name="kind"/> in flight; dispose the token when it finishes.</summary>
    public IDisposable Begin(ActivityKind kind, string name)
    {
        lock (_lock)
        {
            if (kind == ActivityKind.Process) _process++;
            else _alignment++;
        }
        Changed?.Invoke();
        return new Token(this, kind);
    }

    private void End(ActivityKind kind)
    {
        lock (_lock)
        {
            if (kind == ActivityKind.Process) { if (_process > 0) _process--; }
            else { if (_alignment > 0) _alignment--; }
        }
        Changed?.Invoke();
    }

    private sealed class Token : IDisposable
    {
        private ActivityGate? _gate;
        private readonly ActivityKind _kind;

        public Token(ActivityGate gate, ActivityKind kind)
        {
            _gate = gate;
            _kind = kind;
        }

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.End(_kind);
    }
}
