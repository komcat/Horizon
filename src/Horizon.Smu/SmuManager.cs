using System.Collections.Concurrent;
using Horizon.Config;
using Horizon.Smu.Models;
using Serilog;

namespace Horizon.Smu;

/// <summary>
/// Owns the configured source-measure units. One adapter per device, keyed by
/// name; bulk connect/disconnect and per-device passthroughs.
/// </summary>
public sealed class SmuManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ISmuDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void AddDevice(SmuDeviceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name)) return;

        // Series 2600 (2602B) speaks TSP over a raw socket; the 2400 speaks
        // SCPI over VISA. Pick the adapter by configured type.
        ISmuDevice adapter = config.Type.Contains("2602", StringComparison.OrdinalIgnoreCase)
            ? new Keithley2602BAdapter(config)
            : new Keithley2400Adapter(config);

        if (_devices.TryAdd(config.Name, adapter))
            Log.Information("[SMU] device added: {Device} ({Type})", config.Name, config.Type);
    }

    public ISmuDevice? GetDevice(string name)
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

    public SmuError SetSourceVoltage(string device, double volts)
        => With(device, d => d.SetSourceVoltage(volts));

    public SmuError SetSourceCurrent(string device, double amps)
        => With(device, d => d.SetSourceCurrent(amps));

    public SmuError SetCompliance(string device, double value)
        => With(device, d => d.SetCompliance(value));

    public SmuError OutputOn(string device) => With(device, d => d.OutputOn());
    public SmuError OutputOff(string device) => With(device, d => d.OutputOff());

    public SmuStatus? GetStatus(string device) => GetDevice(device)?.GetStatus();

    private SmuError With(string device, Func<ISmuDevice, SmuError> action)
    {
        var d = GetDevice(device);
        return d == null ? SmuError.DeviceNotFound : action(d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAll();
        _devices.Clear();
    }
}
