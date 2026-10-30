using System.Threading;

namespace Horizon.Station.UI;

/// <summary>
/// "The machine is waiting on the operator" — counted while a blocking operator prompt is
/// up (a <c>flow.askUser</c> confirm, serial entry, alert acknowledge, jog popup; see
/// <see cref="OperatorPrompt"/>). The tower light blinks yellow while <see cref="Waiting"/>
/// so someone walks over. Re-entrant and disposal-safe; readable from any thread.
/// </summary>
public sealed class OperatorAttention
{
    private int _count;

    /// <summary>True while at least one operator prompt is open.</summary>
    public bool Waiting => Volatile.Read(ref _count) > 0;

    /// <summary>Marks a prompt open; dispose the token when it closes (answered, cancelled or Stop).</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        return new Token(this);
    }

    private sealed class Token : IDisposable
    {
        private OperatorAttention? _owner;
        public Token(OperatorAttention owner) => _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } o) Interlocked.Decrement(ref o._count);
        }
    }
}
