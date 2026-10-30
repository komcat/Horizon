using Horizon.Engine;

namespace Horizon.Station.UI;

/// <summary>
/// Thread-safe, bounded log buffer fed by the engine and workflow
/// <c>LogEmitted</c> events. Producers run on engine threads; consumers (the UI)
/// read <see cref="Snapshot"/> once and subscribe to <see cref="Appended"/> for
/// live lines — marshalling to the UI thread is the subscriber's responsibility.
/// </summary>
public sealed class LogHub
{
    private const int Capacity = 1000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _buffer = new();

    /// <summary>Raised for every new line. May fire on any thread.</summary>
    public event Action<LogEntry>? Appended;

    /// <summary>Appends a line, evicting the oldest once capacity is exceeded.</summary>
    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > Capacity)
                _buffer.Dequeue();
        }
        Appended?.Invoke(entry);
    }

    /// <summary>A point-in-time copy of the buffered lines, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _buffer.ToArray();
    }
}
