using System.Collections.Concurrent;
using Horizon.Config;
using Horizon.Lighting.Models;
using Serilog;

namespace Horizon.Lighting;

/// <summary>
/// Owns the configured lighting controllers. One adapter per controller, keyed
/// by device id; bulk connect/disconnect and per-device passthroughs.
/// </summary>
public sealed class LightingManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ILightingDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void AddDevice(LightingControllerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceId)) return;
        if (!config.Enabled)
        {
            Log.Information("[Lighting] device {Device} is disabled in config — skipped", config.DeviceId);
            return;
        }
        if (_devices.TryAdd(config.DeviceId, new CcsPd3Adapter(config)))
            Log.Information("[Lighting] device added: {Device} ({Type})", config.DeviceId, config.ControllerType);
    }

    public ILightingDevice? GetDevice(string deviceId)
        => _devices.TryGetValue(deviceId, out var device) ? device : null;

    public List<string> GetAllDeviceIds() => _devices.Keys.ToList();

    public bool ConnectAll()
    {
        var all = true;
        foreach (var device in _devices.Values)
            all &= device.Connect();
        return all;
    }

    public void DisconnectAll()
    {
        foreach (var device in _devices.Values)
            device.Disconnect();
    }

    public LightingError SetIntensity(string deviceId, int channel, int intensity)
    {
        var d = GetDevice(deviceId);
        return d == null ? LightingError.DeviceNotFound : d.SetIntensity(channel, intensity);
    }

    public LightingStatus? GetStatus(string deviceId) => GetDevice(deviceId)?.GetStatus();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAll();
        _devices.Clear();
    }
}
