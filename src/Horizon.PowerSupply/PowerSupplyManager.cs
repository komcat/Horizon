using System.Collections.Concurrent;
using Horizon.Config;
using Horizon.PowerSupply.Models;
using Serilog;

namespace Horizon.PowerSupply;

/// <summary>
/// Owns the configured power-supply devices. One adapter per device, keyed by
/// name; bulk connect/disconnect and per-device passthroughs.
/// </summary>
public sealed class PowerSupplyManager : IDisposable
{
    private readonly ConcurrentDictionary<string, IPowerSupplyDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void AddDevice(PowerSupplyConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name)) return;
        var adapter = CreateAdapter(config);
        if (_devices.TryAdd(config.Name, adapter))
            Log.Information("[PSU] device added: {Device} ({Type})", config.Name, config.Type);
    }

    private static IPowerSupplyDevice CreateAdapter(PowerSupplyConfig config)
    {
        // Dispatch by type. Unknown types fall back to the Keysight LXI adapter,
        // matching the historical default.
        return config.Type?.ToLowerInvariant() switch
        {
            "unit_t_udp3305s" or "unitt_udp3305s" or "udp3305s"
                => new UnitTUDP3305SAdapter(config),
            "siglent_spd1305x" or "spd1305x"
                => new SiglentSpd1305xAdapter(config),
            _   => new KeysightPowerSupplyAdapter(config),
        };
    }

    public IPowerSupplyDevice? GetDevice(string name)
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

    public PowerSupplyError SetVoltage(string device, int channel, double volts)
        => With(device, d => d.SetVoltage(channel, volts));

    public PowerSupplyError SetCurrentLimit(string device, int channel, double amps)
        => With(device, d => d.SetCurrentLimit(channel, amps));

    public PowerSupplyError OutputOn(string device, int channel)
        => With(device, d => d.OutputOn(channel));

    public PowerSupplyError OutputOff(string device, int channel)
        => With(device, d => d.OutputOff(channel));

    public PowerSupplyStatus? GetStatus(string device)
        => GetDevice(device)?.GetStatus();

    private PowerSupplyError With(string device, Func<IPowerSupplyDevice, PowerSupplyError> action)
    {
        var d = GetDevice(device);
        return d == null ? PowerSupplyError.DeviceNotFound : action(d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAll();
        _devices.Clear();
    }
}
