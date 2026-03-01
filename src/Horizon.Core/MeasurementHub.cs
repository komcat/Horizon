using System.Collections.Concurrent;

namespace Horizon.Core;

/// <summary>
/// Per-channel circular buffer for timestamped measurement data.
/// Thread-safe: Push from any thread, Snapshot from UI thread.
/// </summary>
public sealed class MeasurementChannel
{
    private readonly int _capacity;
    private readonly double[] _timestamps;
    private readonly double[] _values;
    private int _head;
    private int _count;
    private DateTimeOffset _origin;
    private bool _originSet;
    private readonly object _lock = new();

    public string Key { get; }

    public MeasurementChannel(string key, int capacity = 500)
    {
        Key = key;
        _capacity = capacity;
        _timestamps = new double[capacity];
        _values = new double[capacity];
    }

    public void Push(double value, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            if (!_originSet)
            {
                _origin = timestamp;
                _originSet = true;
            }

            double relTime = (timestamp - _origin).TotalSeconds;
            _timestamps[_head] = relTime;
            _values[_head] = value;

            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
                _count++;
        }
    }

    /// <summary>
    /// Copy current buffer state into pre-allocated arrays. Returns sample count.
    /// Samples are written in chronological order (oldest first).
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

/// <summary>
/// Central registry of measurement channels. Any producer pushes data,
/// any consumer reads via channel keys. Channels are auto-created on first Push.
/// </summary>
public sealed class MeasurementHub
{
    private readonly ConcurrentDictionary<string, MeasurementChannel> _channels = new(StringComparer.OrdinalIgnoreCase);

    public void Push(string key, double value, DateTimeOffset timestamp)
    {
        var channel = _channels.GetOrAdd(key, k => new MeasurementChannel(k));
        channel.Push(value, timestamp);
    }

    public MeasurementChannel? GetChannel(string key)
    {
        _channels.TryGetValue(key, out var channel);
        return channel;
    }

    public MeasurementChannel GetOrCreateChannel(string key)
    {
        return _channels.GetOrAdd(key, k => new MeasurementChannel(k));
    }

    public IReadOnlyList<MeasurementChannel> GetChannelsForDevice(string deviceName)
    {
        var prefix = deviceName + ".";
        var result = new List<MeasurementChannel>();
        foreach (var kvp in _channels)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result.Add(kvp.Value);
        }
        return result;
    }

    public IReadOnlyList<string> GetAllChannelKeys()
    {
        return _channels.Keys.ToList();
    }

    public void Clear()
    {
        foreach (var kvp in _channels)
            kvp.Value.Clear();
        _channels.Clear();
    }
}
