namespace Horizon.UI.Telemetry;

/// <summary>
/// A fixed-capacity circular buffer of timestamped samples for one telemetry
/// channel — a port of the proven legacy <c>Horizon.Core.MeasurementChannel</c>.
/// Lock-protected: producers push on engine background threads, the UI reads a
/// snapshot on the dispatcher thread.
/// </summary>
public sealed class TelemetryChannel
{
    private readonly int _capacity;
    private readonly double[] _timestamps;
    private readonly double[] _values;
    private readonly object _lock = new();
    private int _head;
    private int _count;
    private DateTime _origin;
    private bool _originSet;

    public TelemetryChannel(string key, int capacity = 500)
    {
        Key = key;
        _capacity = capacity;
        _timestamps = new double[capacity];
        _values = new double[capacity];
    }

    /// <summary>Channel key, e.g. <c>"SMU1S.CH1.current"</c>.</summary>
    public string Key { get; }

    /// <summary>Number of samples currently buffered (0..capacity).</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>The most recent value, or null if no samples yet.</summary>
    public double? LatestValue
    {
        get
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                return _values[(_head - 1 + _capacity) % _capacity];
            }
        }
    }

    /// <summary>Appends a sample. Timestamps are stored relative to the first push.</summary>
    public void Push(double value, DateTime timestamp)
    {
        lock (_lock)
        {
            if (!_originSet)
            {
                _origin = timestamp;
                _originSet = true;
            }
            _timestamps[_head] = (timestamp - _origin).TotalSeconds;
            _values[_head] = value;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    /// <summary>
    /// Copies the buffer into pre-allocated arrays (oldest first) and returns
    /// the sample count — zero-allocation when the consumer reuses the arrays.
    /// </summary>
    public int Snapshot(double[] outTimestamps, double[] outValues)
    {
        lock (_lock)
        {
            if (_count == 0) return 0;
            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % _capacity;
                outTimestamps[i] = _timestamps[idx];
                outValues[i] = _values[idx];
            }
            return _count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
            _originSet = false;
        }
    }
}
