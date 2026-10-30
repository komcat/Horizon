using System.Collections.Concurrent;

namespace Horizon.Station.UI.Telemetry;

/// <summary>
/// Per-channel ring-buffer history for live telemetry — a port of the legacy
/// <c>Horizon.Core.MeasurementHub</c>. Channels are auto-created on first push.
/// Fed by Layer 1 sample events (e.g. <c>ISmuFacet.Sampled</c>) on background
/// threads; read by the UI at display cadence. Key convention:
/// <c>"{Device}.CH{N}.{quantity}"</c> — quantity is <c>current</c> or <c>voltage</c>.
/// </summary>
public sealed class TelemetryStore
{
    private readonly ConcurrentDictionary<string, TelemetryChannel> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    // One origin for the whole store — the first sample's timestamp. Every
    // channel measures relative time from it, so channels that start at
    // different moments still share one time axis.
    private readonly object _originLock = new();
    private DateTime? _origin;

    /// <summary>Appends a sample, creating the channel on first use.</summary>
    public void Push(string key, double value, DateTime timestamp)
    {
        DateTime origin;
        lock (_originLock)
        {
            _origin ??= timestamp;
            origin = _origin.Value;
        }
        _channels.GetOrAdd(key, k => new TelemetryChannel(k)).Push(value, timestamp, origin);
    }

    /// <summary>The channel for a key, or null if nothing has pushed to it yet.</summary>
    public TelemetryChannel? GetChannel(string key)
        => _channels.TryGetValue(key, out var c) ? c : null;

    /// <summary>All channels whose key starts with <c>"{device}."</c>.</summary>
    public IReadOnlyList<TelemetryChannel> GetChannelsForDevice(string device)
    {
        var prefix = device + ".";
        return _channels
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .ToList();
    }

    public IReadOnlyList<string> GetAllChannelKeys() => _channels.Keys.ToList();

    public void Clear()
    {
        lock (_originLock) _origin = null;
        _channels.Clear();
    }
}
