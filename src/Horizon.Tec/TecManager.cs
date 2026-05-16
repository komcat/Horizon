using System.Collections.Concurrent;
using Horizon.Config;
using Horizon.Tec.Models;
using Serilog;

namespace Horizon.Tec;

/// <summary>
/// Owns the configured standalone TEC controllers. One adapter per device,
/// keyed by name; bulk connect/disconnect and per-device passthroughs.
/// </summary>
public sealed class TecManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ITecDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void AddDevice(TecDeviceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name)) return;
        if (_devices.TryAdd(config.Name, new Ted4015Adapter(config)))
            Log.Information("[TEC] device added: {Device} ({Type})", config.Name, config.ControllerType);
    }

    public ITecDevice? GetDevice(string name)
        => _devices.TryGetValue(name, out var device) ? device : null;

    public List<string> GetAllDeviceNames() => _devices.Keys.ToList();

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

    public TecError SetTemperature(string device, double celsius)
        => With(device, d => d.SetTemperature(celsius));

    public TecError TecOn(string device) => With(device, d => d.TecOn());
    public TecError TecOff(string device) => With(device, d => d.TecOff());

    public TecStatus? GetStatus(string device) => GetDevice(device)?.GetStatus();

    private TecError With(string device, Func<ITecDevice, TecError> action)
    {
        var d = GetDevice(device);
        return d == null ? TecError.DeviceNotFound : action(d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAll();
        _devices.Clear();
    }
}
